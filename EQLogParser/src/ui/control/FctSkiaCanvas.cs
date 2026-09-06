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
  /* Per-hit render state for the SkiaSharp FCT backend. Plain data — no WPF text objects, so the
   * per-frame cost is C++-side shaping/drawing on one SKCanvas instead of a WPF display list. */
  internal sealed class FctSkiaHit
  {
    public FctSimLane Lane;
    public double X0, Y0;                      // spawn position (logical px)
    public double Rise, Arc;                   // total upward travel / sideways arc amplitude
    public double MotionMs;                    // rise+arc complete by this age, then hold (see RaisedY)
    public double SideMin, SideMax;            // half-canvas clamp for the value's center x
    public double ValueWidth;                  // measured, refreshed only when the value changes
    public double ValueFontSize, SourceFontSize;
    public SKColor ValueColor;
    public double LifetimeMs, FadeMs;
    public bool Blowout;                       // crit scale pop on top of the float curve
    public double SpawnMs, AgeAtCountStartMs;  // canvas clock time in ms
    public double TargetValue, CountBaseValue;
    public double CountUpMs;                   // 0 => no count-up
    public string Action;
    public string LastValueKey = "";
    public string HaloKey;                     // cached blur-glow sprite (crits only)
    public int HaloPad;
  }

  /*
   * SkiaSharp FCT renderer: one CPU-raster SKSurface redrawn every frame and blitted into the WPF
   * tree as a single image. Outline is FillAndStroke (2 passes instead of NAG's 5), glow is a true
   * Gaussian blur baked once per unique crit value into a ref-counted halo sprite. Mirrors the lane,
   * motion (bottom-third spawn, rise + half-clamped arc), count-up and stats logic of FctSimCanvas so
   * both backends are directly comparable — see docs/NagFctReference.md for the design.
   */
  internal class FctSkiaCanvas : FrameworkElement, IFctSimCanvas
  {
    private const double DamageDealtFontSize = 30;
    private const double DamageTakenFontSize = 28;
    private const double HealingFontSize = 24;
    /* Crits are common in EQ (roughly every third number), so the emphasis stays a size step up
     * from normal damage, not a spectacle. */
    private const double CritFontSize = 34;
    private const double MinorFontSize = 19;
    private const double SourceFontMin = 12;
    private const float GlowSigma = 5f;

    /* How early in its life a hit finishes moving; see RaisedY for why motion must not span the
     * whole (adaptive) display time. */
    private const double MotionWindowMs = 2000;

    /* Ref-counted blur-glow sprite: rendered once per unique (value, size), disposed when the last
     * hit using it expires. NAG's glow is a black wide radial text-shadow, so color is irrelevant. */
    private sealed class HaloEntry
    {
      public SKImage Image;
      public int Refs, Pad;
    }

    private readonly Random _rand = new(1234);
    private readonly List<FctSkiaHit> _hits = [];
    private readonly FctLifeController _life = new();
    private readonly Dictionary<string, HaloEntry> _halos = new();
    private readonly Dictionary<(byte style, int size), SKFont> _fonts = new();
    private SKTypeface _boldTypeface, _regularTypeface;
    private SKMaskFilter _glowBlur;
    private SKSurface _surface;
    private int _surfaceWidth, _surfaceHeight;
    private double _pixelsPerDip;
    private Stopwatch _clock;

    public event Action<double> EventsFrame; // canvas clock ms since Start()
    public int ActiveCount => _hits.Count;
    public double Fps { get; private set; }
    public double AvgFrameMs { get; private set; }
    public double LastFrameMs { get; private set; }
    public double DrawsPerSec { get; private set; }

    private double _statsWindowStartMs;
    private long _statFrames, _statDrawsTotal, _statDrawsWindow;
    private double _statFrameMsSum;
    private bool _dirty;

    public void Start()
    {
      _clock = Stopwatch.StartNew();
      _statsWindowStartMs = 0;
      if (PresentationSource.FromVisual(this) is not null)
      {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
      }

      CompositionTarget.Rendering += OnRendering;
      _dirty = true;
    }

    public void Stop()
    {
      CompositionTarget.Rendering -= OnRendering;

      foreach (var entry in _halos.Values)
      {
        entry.Image.Dispose();
      }

      _halos.Clear();
      _surface?.Dispose();
      _surface = null;
      _glowBlur?.Dispose();
      _glowBlur = null;
      foreach (var font in _fonts.Values)
      {
        font.Dispose();
      }

      _fonts.Clear();
      _boldTypeface?.Dispose();
      _boldTypeface = null;
      _regularTypeface?.Dispose();
      _regularTypeface = null;
      _hits.Clear();
    }

    /* Mirrors FctSimCanvas.AddHit: bottom-third spawn in the lane's half, rise + arc. */
    public void AddHit(FctSimLane lane, double value, string action, bool crit, bool minor = false)
    {
      if (_clock is null)
      {
        return;
      }

      var w = ActualWidth;
      var h = ActualHeight;
      if (w < 100 || h < 100)
      {
        return;
      }

      // captured before crits are pooled into their own lane, so a taken-crit stays on the incoming side
      var leftSide = lane is FctSimLane.DamageTaken or FctSimLane.HealingReceived;
      if (crit)
      {
        lane = FctSimLane.Crit;
      }

      // home band: each side's lanes centered as a pair within their own half (text is drawn
      // center-aligned on x); crits sit at the middle of their half and spread wider
      var cx = lane switch
      {
        FctSimLane.DamageTaken => w * 0.14,
        FctSimLane.HealingReceived => w * 0.35,
        FctSimLane.Crit => leftSide ? w * 0.25 : w * 0.75,
        FctSimLane.HealingDealt => w * 0.86,
        _ => w * 0.65, // DamageDealt
      };

      var hit = NewHitState(lane, value, action, minor,
        x: cx + ((_rand.NextDouble() * 2 - 1) * (lane == FctSimLane.Crit ? w * 0.17 : w * 0.09)),
        y: h * (0.68 + _rand.NextDouble() * 0.17), // bottom third
        now: _clock.Elapsed.TotalMilliseconds);

      hit.Rise = h * (0.34 + _rand.NextDouble() * 0.12) * (lane is FctSimLane.HealingDealt or FctSimLane.HealingReceived ? 0.8 : 1.0);
      hit.Arc = (_rand.NextDouble() * 2 - 1) * w * (lane == FctSimLane.Crit ? 0.15 : 0.12);

      hit.SideMin = leftSide ? 8 : w / 2 + 4;
      hit.SideMax = leftSide ? w / 2 - 4 : w - 8;

      // adaptive display time (see FctLifeController); crits keep a fixed lifetime and stay prominent
      if (lane == FctSimLane.Crit)
      {
        hit.LifetimeMs = 2800;
      }
      else
      {
        var live = 0;
        foreach (var existing in _hits)
        {
          if (existing.Lane == lane)
          {
            live++;
          }
        }

        hit.LifetimeMs = _life.NextLifetime(lane, live, _clock.Elapsed.TotalMilliseconds);
      }

      hit.MotionMs = Math.Min(MotionWindowMs, hit.LifetimeMs);
      hit.FadeMs = Math.Clamp(hit.LifetimeMs * 0.18, 250, 700); // fade is a share of the life, capped
      _hits.Add(hit);
      _dirty = true;
    }

    /* Mirrors FctSimCanvas.TryAccumulate: folds a small hit into the newest live non-crit hit of the lane. */
    public bool TryAccumulate(FctSimLane lane, double amount)
    {
      var now = _clock.Elapsed.TotalMilliseconds;

      for (var i = _hits.Count - 1; i > -1; i--)
      {
        var hit = _hits[i];
        if (hit.Lane != lane || hit.Blowout || now - hit.SpawnMs > 1500)
        {
          continue;
        }

        var age = now - hit.SpawnMs;
        hit.CountBaseValue = GetDisplayValue(hit, age);
        hit.AgeAtCountStartMs = age;
        hit.CountUpMs = 300;
        hit.TargetValue += amount;
        _dirty = true;
        return true;
      }

      return false;
    }

    protected override void OnRender(DrawingContext dc)
    {
      var now = _clock is null ? 0 : _clock.Elapsed.TotalMilliseconds;
      var scale = _pixelsPerDip is 0 ? 1.0 : _pixelsPerDip;
      var w = (int)Math.Ceiling(ActualWidth * scale);
      var h = (int)Math.Ceiling(ActualHeight * scale);
      if (w < 50 || h < 50)
      {
        return;
      }

      if (_surface is null || _surfaceWidth != w || _surfaceHeight != h)
      {
        _surface?.Dispose();
        _surface = SKSurface.Create(new SKImageInfo(w, h));
        _surfaceWidth = w;
        _surfaceHeight = h;
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

          var age = now - hit.SpawnMs;
          var opacity = FadeOpacity(age, hit.LifetimeMs, hit.FadeMs);
          if (opacity <= 0)
          {
            continue;
          }

          var key = FctText.FormatHitValue(GetDisplayValue(hit, age));
          if (key != hit.LastValueKey)
          {
            hit.LastValueKey = key;
            hit.ValueWidth = TextWidth(key, hit.ValueFontSize, bold: true);
          }

          DrawHit(canvas, hit, age, opacity);
        }
      }

      /*
       * Blit in device space, not logical: the surface holds ceil(logical * scale) pixels, so drawing over
       * ActualWidth DIUs gives WPF a destination of logical * scale device pixels - non-integer and off by up
       * to one pixel. WPF then resamples the bitmap every frame, and that resampling phase shifts with each
       * sub-pixel step of the motion, which reads as fine graininess on slow text. Sizing the rect from the
       * surface's real pixel count makes the mapping exactly 1:1; the sub-pixel overshoot from the ceil is
       * clipped at the canvas edge.
       */
      using var image = _surface.Snapshot();
      dc.DrawImage(ToWriteableBitmap(image), new Rect(0, 0, (double)_surfaceWidth / scale, (double)_surfaceHeight / scale));
      _dirty = false;
    }

    /*
     * CPU surface to WPF blit. The surface is created with the default SKImageInfo (Bgra8888, premultiplied),
     * which is byte-identical to WPF's native Bgra32 format, so reading back with image.Info performs no
     * conversion. Replaces the SkiaSharp.Views.WPF ToWriteableBitmap() extension; that package drags an
     * OpenTK stack that conflicts with the Kokoro TTS one, and nothing else in it is used.
     */
    private static WriteableBitmap ToWriteableBitmap(SKImage image)
    {
      var stride = image.Width * 4;
      var pixels = new byte[stride * image.Height];
      var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
      try
      {
        image.ReadPixels(image.Info, handle.AddrOfPinnedObject(), stride);
      }
      finally
      {
        handle.Free();
      }

      var bitmap = new WriteableBitmap(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null);
      bitmap.WritePixels(new Int32Rect(0, 0, image.Width, image.Height), pixels, stride, 0);
      return bitmap;
    }

    private void OnRendering(object sender, EventArgs e)
    {
      var sw = Stopwatch.StartNew();
      var now = _clock.Elapsed.TotalMilliseconds;

      // remove expired hits (releasing their halo sprite when the last user goes)
      for (var i = _hits.Count - 1; i > -1; i--)
      {
        if (now - _hits[i].SpawnMs > _hits[i].LifetimeMs)
        {
          ReleaseHalo(_hits[i]);
          _hits.RemoveAt(i);
          _dirty = true;
        }
      }

      EventsFrame?.Invoke(now);

      LastFrameMs = sw.Elapsed.TotalMilliseconds;

      /*
       * The content is animated: a hit's position depends on the clock, so any frame with live hits must
       * repaint or the text stands still until the next spawn/expiry (visible as shaking at low intensity).
       * _dirty alone only drives the one frame that clears the canvas when the last hit expires; with no
       * hits at all the CPU raster is skipped entirely.
       */
      if (_dirty || _hits.Count > 0)
      {
        InvalidateVisual();
      }

      // publish per-second stats when the one second window rolls over
      if (now - _statsWindowStartMs >= 1000)
      {
        var seconds = (now - _statsWindowStartMs) / 1000.0;
        Fps = _statFrames / seconds;
        AvgFrameMs = _statFrames > 0 ? _statFrameMsSum / _statFrames : 0;
        DrawsPerSec = (_statDrawsTotal - _statDrawsWindow) / seconds;
        _statsWindowStartMs = now;
        _statFrames = 0;
        _statFrameMsSum = 0;
        _statDrawsWindow = _statDrawsTotal;
      }

      _statFrames++;
      _statFrameMsSum += LastFrameMs;
    }

    private FctSkiaHit NewHitState(FctSimLane lane, double value, string action, bool minor, double x, double y, double now)
    {
      var hit = new FctSkiaHit
      {
        Lane = lane,
        X0 = x,
        Y0 = y,
        SpawnMs = now,
        TargetValue = value,
        CountBaseValue = value,
        Action = action,
        LastValueKey = FctText.FormatHitValue(value),
      };

      switch (lane)
      {
        // lifetime/fade are assigned by AddHit (adaptive - see FctLifeController)
        case FctSimLane.Crit:
          hit.Blowout = true;
          hit.ValueFontSize = CritFontSize;
          hit.ValueColor = new SKColor(0xFF, 0xA3, 0x2E); // orange
          break;

        case FctSimLane.HealingDealt or FctSimLane.HealingReceived:
          hit.ValueFontSize = HealingFontSize;
          hit.ValueColor = new SKColor(0x7F, 0xE0, 0x61); // green
          break;

        case FctSimLane.DamageDealt:
          hit.ValueFontSize = minor ? MinorFontSize : DamageDealtFontSize;
          hit.ValueColor = new SKColor(0xFF, 0xD7, 0x5E); // yellow
          break;

        default: // DamageTaken
          hit.ValueFontSize = DamageTakenFontSize;
          hit.ValueColor = new SKColor(0xFF, 0x6B, 0x5E); // red
          break;
      }

      hit.SourceFontSize = Math.Max(SourceFontMin, hit.ValueFontSize * 0.42);
      hit.ValueWidth = TextWidth(hit.LastValueKey, hit.ValueFontSize, bold: true);

      // true NAG-style radial glow, crits only (default groups carry no halo)
      if (hit.Blowout)
      {
        AcquireHalo(hit);
      }

      return hit;
    }

    private double GetDisplayValue(FctSkiaHit hit, double ageMs)
    {
      if (hit.CountUpMs <= 0 || hit.TargetValue == hit.CountBaseValue)
      {
        return hit.TargetValue;
      }

      var p = Math.Clamp((ageMs - hit.AgeAtCountStartMs) / hit.CountUpMs, 0.0, 1.0);
      return hit.CountBaseValue + ((hit.TargetValue - hit.CountBaseValue) * p);
    }

    private void AcquireHalo(FctSkiaHit hit)
    {
      var key = $"{hit.LastValueKey}|{hit.ValueFontSize:F0}";

      if (_halos.TryGetValue(key, out var entry))
      {
        entry.Refs++;
        hit.HaloKey = key;
        hit.HaloPad = entry.Pad;
        return;
      }

      var pad = (int)(GlowSigma * 3.0) + 2;
      EnsureSkiaResources();
      using (var font = new SKFont(_boldTypeface, (float)hit.ValueFontSize, 1f, 0f))
      {
        // 3.119.2 MeasureText overloads require a paint argument but only read it for encoding
        using var measure = new SKPaint();
        var textWidth = (int)Math.Ceiling(font.MeasureText(hit.LastValueKey, measure));
        var textHeight = (int)Math.Ceiling(hit.ValueFontSize * 1.25);

        using var surf = SKSurface.Create(new SKImageInfo(textWidth + pad * 2, textHeight + pad * 2));
        var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true, MaskFilter = _glowBlur };
        surf.Canvas.DrawText(hit.LastValueKey, pad, (float)(pad + hit.ValueFontSize * 0.82), SKTextAlign.Left, font, paint);
        paint.Dispose();

        var image = surf.Snapshot();
        _halos[key] = new HaloEntry { Image = image, Refs = 1, Pad = pad };
        hit.HaloKey = key;
        hit.HaloPad = pad;
      }

      CountDraw(1);
    }

    private void ReleaseHalo(FctSkiaHit hit)
    {
      if (hit.HaloKey is null || !_halos.TryGetValue(hit.HaloKey, out var entry))
      {
        return;
      }

      entry.Refs--;
      if (entry.Refs <= 0)
      {
        entry.Image.Dispose();
        _halos.Remove(hit.HaloKey);
      }

      hit.HaloKey = null;
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
    }

    /* One hit = halo blit (crits) + value outline/fill + source outline/fill: ~5-6 Skia draw ops. */
    private void DrawHit(SKCanvas canvas, FctSkiaHit hit, double ageMs, double opacity)
    {
      EnsureSkiaResources();
      var alpha = (byte)Math.Round(opacity * 255.0);

      // motion runs on its own short clock: rise/arc finish and hold before the fade (see RaisedY)
      var t = Math.Clamp(ageMs / hit.MotionMs, 0.0, 1.0);
      var x = ArcedX(hit, t);
      var y = RaisedY(hit, t);

      if (hit.Blowout)
      {
        var s = (float)BlowoutScale(ageMs, hit.LifetimeMs);
        // x is the text's center now, so it doubles as the blowout pivot
        var cy = y + (hit.ValueFontSize * 0.45);
        canvas.Save();
        canvas.Translate((float)x, (float)cy);
        canvas.Scale(s, s);
        canvas.Translate(-(float)x, -(float)cy);
      }

      var valueBase = y + (hit.ValueFontSize * 0.82);

      // glow underlay (pre-blurred sprite, crits only); white modulation keeps the black halo, alpha fades it
      if (hit.HaloKey is not null && _halos.TryGetValue(hit.HaloKey, out var halo))
      {
        var blit = MakePaint(SKColors.White, alpha, SKPaintStyle.Fill, 0);
        canvas.DrawImage(halo.Image, (float)(x - hit.ValueWidth / 2.0 - hit.HaloPad), (float)(y - hit.HaloPad), blit);
        blit.Dispose();
        CountDraw(1);
      }

      // value: black outline pass, then colored fill pass
      DrawOutlinedText(canvas, hit.LastValueKey, (float)x, (float)valueBase, hit.ValueFontSize, true, hit.ValueColor, alpha);

      // source line under the value
      var sourceBase = y + (hit.ValueFontSize * 1.25) + (hit.SourceFontSize * 0.85);
      DrawOutlinedText(canvas, $"({hit.Action})", (float)x, (float)sourceBase, hit.SourceFontSize, false, new SKColor(0x6E, 0x93, 0xC8), (byte)(alpha * 0.95));

      if (hit.Blowout)
      {
        canvas.Restore();
      }
    }

    /* NAG's multi-shadow outline in two passes: black StrokeAndFill under a colored Fill. */
    private void DrawOutlinedText(SKCanvas canvas, string text, float x, float baselineY, double size, bool bold, SKColor color, byte alpha)
    {
      var font = GetFont(bold, size);

      var outline = MakePaint(SKColors.Black, alpha, SKPaintStyle.StrokeAndFill, bold ? 2f : 1.5f);
      canvas.DrawText(text, x, baselineY, SKTextAlign.Center, font, outline);
      outline.Dispose();

      var fill = MakePaint(color, alpha, SKPaintStyle.Fill, 0);
      canvas.DrawText(text, x, baselineY, SKTextAlign.Center, font, fill);
      fill.Dispose();

      CountDraw(2);
    }

    /* Fonts are shared per (style, size) — lane sizes are stable, so the cache holds a handful of entries. */
    private SKFont GetFont(bool bold, double size)
    {
      var key = ((byte)(bold ? 1 : 0), (int)Math.Round(size));

      if (!_fonts.TryGetValue(key, out var font))
      {
        EnsureSkiaResources();
        font = new SKFont(bold ? _boldTypeface : _regularTypeface, (float)size, 1f, 0f);
        _fonts[key] = font;
      }

      return font;
    }

    /* 3.x SKPaint has no Alpha property — alpha rides on the color. */
    private static SKPaint MakePaint(SKColor color, byte alpha, SKPaintStyle style, float strokeWidth) =>
      new() { Color = color.WithAlpha(alpha), Style = style, StrokeWidth = strokeWidth, IsAntialias = true };

    /* 3.119.2 MeasureText overloads require a paint argument but only read it for encoding. */
    private float TextWidth(string text, double size, bool bold)
    {
      using var measure = new SKPaint();
      return GetFont(bold, size).MeasureText(text, measure);
    }

    /*
     * Rise and arc both complete within the hit's motion window (early in its life), after which the
     * text holds position until it fades. A raster display cannot show motion slower than ~1 device
     * pixel per frame without visible stepping or anti-alias-phase shimmer, and a whole-lifetime
     * ease-out spends most of a 7s display down in that zone - that was the "grainy slow vertical"
     * artifact (horizontal looked fine because the old arc accelerated while the rise stalled). A
     * bounded window keeps the motion fast enough to stay smooth, ends at zero velocity (no hitch
     * into the hold), and leaves the long float as a crisp still hold + fade - NAG's own pattern:
     * ~1s fountain, then opacity 1 for the full 7s fadeOut. Short adaptive lifetimes (congestion)
     * are shorter than the window, so under load there is no hold at all.
     */
    private static double RaisedY(FctSkiaHit hit, double t) => hit.Y0 - (hit.Rise * EaseOutQuad(t));

    /* Arc settles with the rise; clamped to the hit's half of the canvas including its text width. */
    private static double ArcedX(FctSkiaHit hit, double t) =>
      Math.Clamp(hit.X0 + (hit.Arc * EaseOutQuad(t)), hit.SideMin + hit.ValueWidth / 2.0, hit.SideMax - hit.ValueWidth / 2.0);

    /* NAG blowout, toned down: quick ramp to ~1.3x, hold, then shrink to ~0 while fading. */
    private static double BlowoutScale(double ageMs, double lifetimeMs)
    {
      const double inMs = 90;
      const double outMs = 700;
      if (ageMs < inMs)
      {
        return 1 + (0.30 * (ageMs / inMs));
      }

      if (ageMs < lifetimeMs - outMs)
      {
        return 1.30;
      }

      var p = Math.Clamp((ageMs - (lifetimeMs - outMs)) / outMs, 0.0, 1.0);
      return 1.30 - ((1.30 - 0.06) * p * p);
    }

    private static double FadeOpacity(double ageMs, double lifetimeMs, double fadeMs)
    {
      const double fadeInMs = 160;
      var o = ageMs < fadeInMs ? ageMs / fadeInMs : 1.0;
      var fadeStart = lifetimeMs - fadeMs;

      if (ageMs > fadeStart)
      {
        o *= Math.Max(0, 1 - ((ageMs - fadeStart) / fadeMs));
      }

      return Math.Clamp(o, 0.0, 1.0);
    }

    private static double EaseOutQuad(double p) => 1 - ((1 - p) * (1 - p));

    private void CountDraw(int n) => _statDrawsTotal += n;
  }
}
