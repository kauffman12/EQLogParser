using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace EQLogParser
{
  /*
   * SkiaSharp FCT renderer — the production backend. All hits rasterize into one CPU SKSurface per frame
   * and blit into the WPF tree as a single image, so per-frame cost tracks C++ draw ops (~5 per hit)
   * rather than WPF display-list elements. Hit policy is shared with the vector backend through
   * FctIngest/FctLayout/FctMotion; this file owns only Skia resources and the draw/blit.
   *
   * Outline is FillAndStroke (2 passes instead of NAG's 5), glow is a true Gaussian blur baked once per
   * unique crit label into a ref-counted halo sprite. See docs/NagFctReference.md and
   * docs/DesignNotes.md → Floating Combat Text.
   */
  internal class FctSkiaCanvas : FrameworkElement, IFctCanvas, IFctDiagnostics
  {

    // re-read DPI about once a second: per-monitor scaling changes neither the size nor any event we get
    private const double DpiCheckMs = 1000;

    private const float GlowSigma = 5f;

    /* Blur sprites are baked per unique crit label; bounded so a long session cannot grow without end. */
    private const int HaloCacheMax = 64;

    // the outline under every label; the literal needs the cast because 0xFF000000 does not fit in an int
    private const int BlackArgb = unchecked((int)0xFF000000);

    /* Ref-counted blur-glow sprite. NAG's glow is a black wide radial text-shadow, so color is irrelevant. */
    private sealed class HaloEntry
    {
      public SKImage Image;
      public int Refs, Pad;
    }

    private readonly List<FctHitState> _hits = [];
    private readonly FctIngest _ingest = new();
    private readonly Dictionary<string, HaloEntry> _halos = new();
    private readonly Dictionary<(byte style, int size), SKFont> _fonts = new();
    private readonly Dictionary<FctHitState, string> _haloKey = [];

    private SKTypeface _boldTypeface, _regularTypeface;
    private SKMaskFilter _glowBlur;

    /* Paints are reused and reconfigured per draw: allocating 4-6 of them per hit per frame was the hot path. */
    private SKPaint _outlinePaint, _fillPaint, _blitPaint;

    private SKSurface _surface;
    private WriteableBitmap _bitmap;
    private byte[] _pixelCopy;
    private GCHandle _pixelHandle;
    private int _surfaceWidth, _surfaceHeight;
    private double _pixelsPerDip = 1.0;
    private double _lastDpiCheckMs;
    private double _statFrameMsMax;
    private Stopwatch _clock;
    private bool _dirty;

    /* Which render ticks get rastered, measured from tick spacing; shared with the vector backend. */
    private readonly FctFramePacer _pacer = new();

    private double _statsWindowStartMs;
    private long _statFrames, _statDrawsTotal, _statDrawsWindow;
    private double _statFrameMsSum;

    public event Action<double> EventsFrame; // canvas clock ms since Start()

    public int ActiveCount => _hits.Count;
    /* Which motion new hits get (hold / fountain / pulse / spray); forwarded to ingest like Layout below. */
    public FctMotionStyle MotionStyle { get => _ingest.Style; set => _ingest.Style = value; }

    /* The layout scheme is ingest's decision, so the canvas just forwards it; see FctLayout for the two modes. */
    public FctLayoutMode Layout { get => _ingest.Mode; set => _ingest.Mode = value; }

    public double Fps { get; private set; }
    public double AvgFrameMs { get; private set; }

    /* Worst frame in the current stats window: average frame time hides the spike that reads as a hitch. */
    public double MaxFrameMs { get; private set; }

    /* What the monitor is actually running at, so a low fps can be told apart from a deliberate pacing cap. */
    public double DisplayHz => _pacer.DisplayHz;
    public double LastFrameMs { get; private set; }
    public double DrawsPerSec { get; private set; }
    public int DroppedCount => _ingest.DroppedCount;

    /* The host drains its feed from EventsFrame, which fires before the paint decision below. */
    public void Start()
    {
      _clock = Stopwatch.StartNew();
      _statsWindowStartMs = 0;
      _pacer.Reset();
      _dirty = true;
      RefreshDpi();
      CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
      CompositionTarget.Rendering -= OnRendering;
      ReleaseAllResources();
    }

    /*
     * Adds one hit. Everything about what happens to it — pooled crit lane, half of the canvas, style,
     * geometry, adaptive lifetime, folding into a live number at the cap — is decided in FctIngest.
     */
    public void AddHit(FctLane lane, double value, string source, bool crit, bool minor = false, bool periodic = false, string valueText = null)
    {
      if (_clock is null)
      {
        return;
      }

      var hit = _ingest.Accept(_hits, lane, value, source, crit, minor, periodic, valueText, ActualWidth, ActualHeight, _clock.Elapsed.TotalMilliseconds);
      if (hit is null)
      {
        _dirty = true; // either folded into a live hit or dropped at the cap: either way, repaint
        return;
      }

      RebuildGlyphs(hit);
      _dirty = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
      base.OnRenderSizeChanged(sizeInfo);
      RefreshDpi();
    }

    protected override void OnRender(DrawingContext dc)
    {
      var now = _clock?.Elapsed.TotalMilliseconds ?? 0;
      var scale = _pixelsPerDip > 0 ? _pixelsPerDip : 1.0;
      var w = (int)Math.Ceiling(ActualWidth * scale);
      var h = (int)Math.Ceiling(ActualHeight * scale);
      if (w < 50 || h < 50)
      {
        return;
      }

      if (!EnsureSurface(w, h))
      {
        return;
      }

      var canvas = _surface.Canvas;
      canvas.Clear(SKColors.Transparent);
      canvas.ResetMatrix();
      canvas.Scale((float)scale, (float)scale); // draw in logical coordinates

      // two passes: regular hits first, crits last — crits draw on top of everything
      for (var pass = 0; pass < 2; pass++)
      {
        foreach (var hit in _hits)
        {
          if (hit.Blowout != (pass == 1))
          {
            continue;
          }

          DrawHit(canvas, hit, now - hit.SpawnMs);
        }
      }

      Blit(dc, scale);
      _dirty = false;
    }

    private void OnRendering(object sender, EventArgs e)
    {
      if (_clock is null)
      {
        return;
      }

      var now = _clock.Elapsed.TotalMilliseconds;

      /* First, and on every tick even the ones this method drops: the pacer measures refresh interval from tick
       * spacing, so measuring only the frames it likes would make it blind to exactly the displays it exists for. */
      var shouldPaint = _pacer.Tick(now);

      // fires every tick, not just painted ones: the simulation paces its whole record schedule off this
      EventsFrame?.Invoke(now);

      // idle and nothing to clear: skip the raster entirely
      if (_hits.Count == 0 && !_dirty)
      {
        return;
      }

      if (!shouldPaint)
      {
        return;
      }

      var sw = Stopwatch.StartNew();
      _pacer.Painted();

      if (now - _lastDpiCheckMs >= DpiCheckMs)
      {
        _lastDpiCheckMs = now;
        RefreshDpi();
      }

      if (_ingest.PruneExpired(_hits, now, ReleaseHalo) > 0)
      {
        _dirty = true;
      }

      LastFrameMs = sw.Elapsed.TotalMilliseconds;

      /*
       * Animated content: a hit's position is a function of the clock, so any frame with live hits must
       * repaint or the text stands still until the next spawn/expiry. _dirty alone adds the one frame that
       * clears the canvas when the last hit expires.
       */
      if (_dirty || _hits.Count > 0)
      {
        InvalidateVisual();
      }

      PublishStats(now);
    }

    /* One hit = halo blit (crits) + value outline/fill + source outline/fill: ~5-6 Skia draw ops. */
    private void DrawHit(SKCanvas canvas, FctHitState hit, double ageMs)
    {
      var opacity = FctMotion.FadeOpacity(hit, ageMs);
      if (opacity <= 0)
      {
        return;
      }

      FctMotion.RefreshText(hit, ageMs);
      if (hit.TextDirty)
      {
        RebuildGlyphs(hit);
      }

      var t = FctMotion.Progress(hit, ageMs);
      var x = FctMotion.ArcedX(hit, t);
      var y = FctMotion.RaisedY(hit, t);
      var s = FctMotion.ScaleOf(hit, ageMs);

      if (s != 1.0)
      {
        // x is the text's center, so it doubles as the scale pivot
        var cy = y + (hit.ValueFontSize * 0.45);
        canvas.Save();
        canvas.Translate((float)x, (float)cy);
        canvas.Scale((float)s, (float)s);
        canvas.Translate(-(float)x, -(float)cy);
      }

      // glow underlay (pre-blurred sprite, crits only); white modulation keeps the black halo, alpha fades it
      if (_haloKey.TryGetValue(hit, out var key) && _halos.TryGetValue(key, out var halo))
      {
        Configure(_blitPaint, SKColors.White.WithAlpha(AlphaOf(opacity)), SKPaintStyle.Fill, 0);
        canvas.DrawImage(halo.Image, (float)(x - hit.ValueWidth / 2.0 - halo.Pad), (float)(y - halo.Pad), _blitPaint);
        CountDraw(1);
      }

      // value: black outline pass, then colored fill pass; source line under it
      DrawOutlinedText(canvas, hit.DisplayText, (float)x, (float)(y + (hit.ValueFontSize * 0.82)), hit.ValueFontSize, true, hit.ValueArgb, opacity);

      if (!string.IsNullOrEmpty(hit.Source))
      {
        var sourceBase = y + (hit.ValueFontSize * 1.25) + (hit.SourceFontSize * 0.85);
        DrawOutlinedText(canvas, $"({hit.Source})", (float)x, (float)sourceBase, hit.SourceFontSize, false, hit.SourceArgb, opacity);
      }

      if (s != 1.0)
      {
        canvas.Restore();
      }
    }

    /* NAG's multi-shadow outline in two passes: black StrokeAndFill under a colored Fill. */
    private void DrawOutlinedText(SKCanvas canvas, string text, float x, float baselineY, double size, bool bold, int argb, double opacity)
    {
      var font = GetFont(bold, size);

      Configure(_outlinePaint, ColorOf(BlackArgb, opacity), SKPaintStyle.StrokeAndFill, bold ? 2f : 1.5f);
      canvas.DrawText(text, x, baselineY, SKTextAlign.Center, font, _outlinePaint);

      Configure(_fillPaint, ColorOf(argb, opacity), SKPaintStyle.Fill, 0);
      canvas.DrawText(text, x, baselineY, SKTextAlign.Center, font, _fillPaint);

      CountDraw(2);
    }

    /*
     * CPU surface to WPF blit, in device space and into a reused bitmap.
     *
     * Device space, not logical: the surface holds ceil(logical * scale) pixels, so drawing over ActualWidth
     * DIUs hands WPF a destination of logical * scale device pixels - non-integer and off by up to one pixel.
     * WPF then resamples every frame, and that resampling phase shifts with each sub-pixel step of the motion,
     * which reads as fine graininess on slow text. Sizing the rect from the surface's real pixel count makes
     * the mapping exactly 1:1; the sub-pixel overshoot from the ceil is clipped at the canvas edge.
     *
     * The bitmap and the copy buffer are allocated once per size and reused: a fresh WriteableBitmap plus a
     * fresh byte[w*h*4] every frame was ~5 MB a frame of large-object-heap garbage, and a new texture upload
     * each time instead of an update of the existing one. The surface is Bgra8888/premultiplied, which is
     * byte-identical to WPF's native Bgra32, so ReadPixels performs no conversion. (Eliminating the last copy
     * means D3DImage; see docs/DesignNotes.md.)
     */
    private void Blit(DrawingContext dc, double scale)
    {
      using var image = _surface.Snapshot();
      if (image is null)
      {
        return;
      }

      var stride = _surfaceWidth * 4;
      image.ReadPixels(image.Info, _pixelHandle.AddrOfPinnedObject(), stride);

      _bitmap.WritePixels(new Int32Rect(0, 0, _surfaceWidth, _surfaceHeight), _pixelCopy, stride, 0);
      dc.DrawImage(_bitmap, new Rect(0, 0, (double)_surfaceWidth / scale, (double)_surfaceHeight / scale));
    }

    /* Surface, destination bitmap and the pinned copy buffer all key off the same pixel size. */
    private bool EnsureSurface(int w, int h)
    {
      if (_surface is not null && _surfaceWidth == w && _surfaceHeight == h)
      {
        return true;
      }

      ReleaseSurface();

      /*
       * Explicit Bgra8888/premultiplied because the blit assumes it (byte-identical to WPF's native Bgra32);
       * Create returns null rather than converting, so a null here means the frame cannot be drawn at all.
       */
      _surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
      if (_surface is null)
      {
        return false;
      }

      _surfaceWidth = w;
      _surfaceHeight = h;
      _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
      _pixelCopy = new byte[w * h * 4];
      _pixelHandle = GCHandle.Alloc(_pixelCopy, GCHandleType.Pinned);
      return true;
    }

    /* Measures the label and (re)acquires the crit halo: both depend on text that can change on count-up. */
    private void RebuildGlyphs(FctHitState hit)
    {
      EnsureSkiaResources();
      var measured = TextWidth(hit.DisplayText, hit.ValueFontSize, bold: true);

      /*
       * A folded hit counts up, and if its width grew with the digits then so did the clamp band ArcedX reads: a DoT
       * total sliding to a wider number would drift sideways as it climbed, which looks like the number is unstable.
       * Hold the widest measurement while counting; every other hit takes its own.
       */
      hit.ValueWidth = FctMotion.IsCountingUp(hit) ? Math.Max(hit.ValueWidth, measured) : measured;
      hit.TextDirty = false;

      if (hit.Blowout)
      {
        AcquireHalo(hit);
      }
    }

    private void AcquireHalo(FctHitState hit)
    {
      var key = $"{hit.DisplayText}|{hit.ValueFontSize:F0}";
      if (_haloKey.TryGetValue(hit, out var current) && current == key)
      {
        return; // already holding the sprite for this label
      }

      ReleaseHalo(hit);

      if (!_halos.TryGetValue(key, out var entry))
      {
        EvictUnreferencedHalos();

        var pad = (int)(GlowSigma * 3.0) + 2;
        using (var font = new SKFont(_boldTypeface, (float)hit.ValueFontSize, 1f, 0f))
        {
          /* Same shaping as the crisp pass in GetFont: the halo is a blurred twin of these exact glyph outlines, so
           * a hinted sprite behind unhinted text puts the glow a fraction off the number it is supposed to bloom. */
          font.Hinting = SKFontHinting.None;

          // 3.119.2 MeasureText overloads require a paint argument but only read it for encoding
          using var measure = new SKPaint();
          var textWidth = (int)Math.Ceiling(font.MeasureText(hit.DisplayText, measure));
          var textHeight = (int)Math.Ceiling(hit.ValueFontSize * 1.25);

          using var surf = SKSurface.Create(new SKImageInfo(textWidth + (pad * 2), textHeight + (pad * 2)));
          if (surf is null)
          {
            return;
          }

          using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true, MaskFilter = _glowBlur })
          {
            surf.Canvas.DrawText(hit.DisplayText, pad, (float)(pad + (hit.ValueFontSize * 0.82)), SKTextAlign.Left, font, paint);
          }

          entry = new HaloEntry { Image = surf.Snapshot(), Refs = 0, Pad = pad };
          _halos[key] = entry;
        }
      }

      entry.Refs++;
      _haloKey[hit] = key;
      CountDraw(1);
    }

    private void ReleaseHalo(FctHitState hit)
    {
      if (!_haloKey.Remove(hit, out var key) || !_halos.TryGetValue(key, out var entry))
      {
        return;
      }

      entry.Refs--;
      if (entry.Refs <= 0)
      {
        entry.Image.Dispose();
        _halos.Remove(key);
      }
    }

    /* Crit labels churn (every crit is a different number), so the sprite cache has to shed its own weight. */
    private void EvictUnreferencedHalos()
    {
      if (_halos.Count < HaloCacheMax)
      {
        return;
      }

      List<string> idle = null;
      foreach (var pair in _halos)
      {
        if (pair.Value.Refs <= 0)
        {
          idle ??= [];
          idle.Add(pair.Key);
        }
      }

      if (idle is null)
      {
        return; // everything on screen still uses it; a temporary overflow is cheaper than dropping glow
      }

      foreach (var key in idle)
      {
        _halos[key].Image.Dispose();
        _halos.Remove(key);
      }
    }

    private void EnsureSkiaResources()
    {
      if (_boldTypeface is not null)
      {
        return;
      }

      _boldTypeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
      _regularTypeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal);
      _glowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, GlowSigma);
      _outlinePaint = new SKPaint { IsAntialias = true };
      _fillPaint = new SKPaint { IsAntialias = true };
      _blitPaint = new SKPaint { IsAntialias = true };
    }

    /* Fonts are shared per (style, size) — lane sizes are stable, so the cache holds a handful of entries. */
    private SKFont GetFont(bool bold, double size)
    {
      var key = ((byte)(bold ? 1 : 0), (int)Math.Round(size));
      if (_fonts.TryGetValue(key, out var font))
      {
        return font;
      }

      EnsureSkiaResources();
      font = new SKFont(bold ? _boldTypeface : _regularTypeface, (float)size, 1f, 0f);

      /*
       * Animated text wants the opposite defaults from static text. Hinting reshapes a glyph according to which pixel
       * rows it lands on, so a number drifting a pixel per frame silently changes its own outline every frame — the
       * crawl that reads as jitter even though the position maths is smooth. And without subpixel positioning Skia
       * snaps each run to a whole pixel, which quantises the very motion FctMotion just interpolated. Together these
       * two are why the float stops stepping.
       */
      font.Hinting = SKFontHinting.None;
      font.Subpixel = true;
      _fonts[key] = font;
      return font;
    }

    private static void Configure(SKPaint paint, SKColor color, SKPaintStyle style, float strokeWidth)
    {
      // 3.x SKPaint has no Alpha property — alpha rides on the color
      paint.Color = color;
      paint.Style = style;
      paint.StrokeWidth = strokeWidth;

      /* Antialias is off by default in SkiaSharp 3, and every paint here draws glyphs (or the blurred crit halo):
       * without it a 34 px number has staircase edges and the halo blits with nearest-neighbour steps. */
      paint.IsAntialias = true;
    }

    private static SKColor ColorOf(int argb, double opacity) => new SKColor((uint)argb).WithAlpha(AlphaOf(opacity, (argb >>> 24) & 0xFF));

    private static byte AlphaOf(double opacity, int max = 255) => (byte)Math.Round(Math.Clamp(opacity, 0.0, 1.0) * max);

    /* 3.119.2 MeasureText overloads require a paint argument but only read it for encoding. */
    private float TextWidth(string text, double size, bool bold)
    {
      using var measure = new SKPaint();
      return GetFont(bold, size).MeasureText(text, measure);
    }

    private void RefreshDpi()
    {
      if (PresentationSource.FromVisual(this) is not null)
      {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
      }
    }

    private void PublishStats(double now)
    {
      if (now - _statsWindowStartMs >= 1000)
      {
        var seconds = (now - _statsWindowStartMs) / 1000.0;
        Fps = _statFrames / seconds;
        AvgFrameMs = _statFrames > 0 ? _statFrameMsSum / _statFrames : 0;
        MaxFrameMs = _statFrameMsMax;
        DrawsPerSec = (_statDrawsTotal - _statDrawsWindow) / seconds;
        _statsWindowStartMs = now;
        _statFrames = 0;
        _statFrameMsSum = 0;
        _statFrameMsMax = 0;
        _statDrawsWindow = _statDrawsTotal;
      }

      _statFrames++;
      _statFrameMsSum += LastFrameMs;
      _statFrameMsMax = Math.Max(_statFrameMsMax, LastFrameMs);
    }

    private void ReleaseSurface()
    {
      _surface?.Dispose();
      _surface = null;

      if (_pixelHandle.IsAllocated)
      {
        _pixelHandle.Free();
      }

      _pixelCopy = null;
      _bitmap = null;
    }

    private void ReleaseAllResources()
    {
      foreach (var entry in _halos.Values)
      {
        entry.Image.Dispose();
      }

      _halos.Clear();
      _haloKey.Clear();
      _hits.Clear();
      ReleaseSurface();

      _glowBlur?.Dispose();
      _glowBlur = null;
      _outlinePaint?.Dispose();
      _fillPaint?.Dispose();
      _blitPaint?.Dispose();
      _outlinePaint = _fillPaint = _blitPaint = null;

      foreach (var font in _fonts.Values)
      {
        font.Dispose();
      }

      _fonts.Clear();
      _boldTypeface?.Dispose();
      _regularTypeface?.Dispose();
      _boldTypeface = _regularTypeface = null;
    }

    private void CountDraw(int n) => _statDrawsTotal += n;
  }
}
