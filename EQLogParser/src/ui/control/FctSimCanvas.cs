using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EQLogParser
{
  /*
   * WPF vector FCT renderer: the A/B reference that lost to FctSkiaCanvas (~30 fps vs ~100 fps at ×10 raid
   * scale — docs/NagFctReference.md → A/B verdict). Kept wired behind Tools so the two can be compared, and
   * it stays honest because all hit policy is shared: FctIngest/FctLayout/FctMotion decide what a hit does,
   * this file only decides how one is drawn with DrawingContext. Outline/glow are offset draws of a cached
   * FormattedText rather than GPU effects; there is no UIElement and no Storyboard per hit.
   */
  internal class FctSimCanvas : FrameworkElement, IFctCanvas, IFctDiagnostics
  {
    private const double DpiCheckMs = 1000;

    private const int BlackArgb = unchecked((int)0xF7000000); // the outline stack is a touch translucent, like NAG's

    /* A/B: EQ's FCT font is a pixel font, so un-antialiased rasterization is both cheaper and more faithful. */
    private const bool AliasedTextRendering = true;

    /* Cached glyph runs for one hit; WPF objects do not belong in the shared FctHitState. */
    private sealed class Glyphs
    {
      public FormattedText Value, ValueOutline, ValueGlow, Source, SourceOutline;
    }

    private static readonly FontFamily _fontFamily = new("Arial");
    private static readonly Typeface _valueTypeface = new(_fontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface _sourceTypeface = new(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Dictionary<int, Brush> _brushes = [];

    private readonly List<FctHitState> _hits = [];
    private readonly FctIngest _ingest = new();
    private readonly Dictionary<FctHitState, Glyphs> _glyphs = [];
    private readonly Brush _outlineBrush = FrozenBrush(BlackArgb);
    private readonly Brush _glowBrush = FrozenBrush(unchecked((int)0x40000000));

    private double _pixelsPerDip = 1.0;
    private double _lastDpiCheckMs;
    private double _statFrameMsMax;
    private Stopwatch _clock;

    /* Which render ticks get rastered, measured from tick spacing; shared with the Skia backend. */
    private readonly FctFramePacer _pacer = new();

    private double _statsWindowStartMs;
    private long _statFrames, _statDrawsTotal, _statDrawsWindow;
    private double _statFrameMsSum;
    private bool _dirty;

    public event Action<double> EventsFrame; // canvas clock ms since Start()

    public int ActiveCount => _hits.Count;
    /* Which motion new hits get (hold / fountain / pulse / spray); forwarded to ingest, which is what applies it. */
    public FctMotionStyle MotionStyle { get => _ingest.Style; set => _ingest.Style = value; }

    public double Fps { get; private set; }
    public double AvgFrameMs { get; private set; }

    /* Worst frame in the current stats window: average frame time hides the spike that reads as a hitch. */
    public double MaxFrameMs { get; private set; }

    /* What the monitor is actually running at, so a low fps can be told apart from a deliberate pacing cap. */
    public double DisplayHz => _pacer.DisplayHz;
    public double LastFrameMs { get; private set; }
    public double DrawsPerSec { get; private set; }
    public int DroppedCount => _ingest.DroppedCount;

    public void Start()
    {
      _clock = Stopwatch.StartNew();
      _statsWindowStartMs = 0;
      _pacer.Reset();
      RefreshDpi();

      if (AliasedTextRendering)
      {
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Aliased);
      }

      CompositionTarget.Rendering += OnRendering;
      _dirty = true;
    }

    public void Stop()
    {
      CompositionTarget.Rendering -= OnRendering;
      _hits.Clear();
      _glyphs.Clear();
    }

    public void AddHit(FctLane lane, double value, string source, bool crit, bool minor = false, bool periodic = false, string valueText = null, bool proc = false)
    {
      if (_clock is null)
      {
        return;
      }

      /* The eviction sink for pulse mode: a hit whose cell was taken must have its cached glyph run dropped with it. */
      var hit = _ingest.Accept(_hits, lane, value, source, crit, minor, periodic, valueText, ActualWidth, ActualHeight,
        _clock.Elapsed.TotalMilliseconds, proc, h => _glyphs.Remove(h));
      if (hit is null)
      {
        _dirty = true; // either folded into a live hit or dropped at the cap: either way, repaint
        return;
      }

      BuildGlyphs(hit);
      _dirty = true;
    }

    /* Numbers already in flight move with the canvas, exactly as they do on the overlay: see FctResize. The simulation window is
     * an ordinary resizable window, which makes it the best place to see a resize behave before the overlay does. */
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
      base.OnRenderSizeChanged(sizeInfo);
      RefreshDpi();
      FctResize.Rescale(_hits, sizeInfo.PreviousSize.Width, sizeInfo.PreviousSize.Height, sizeInfo.NewSize.Width, sizeInfo.NewSize.Height);
      _dirty = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
      var now = _clock?.Elapsed.TotalMilliseconds ?? 0;

      // two passes: regular hits first, crits last — crits draw on top (there is no Z order in one OnRender)
      for (var pass = 0; pass < 2; pass++)
      {
        foreach (var hit in _hits)
        {
          if (hit.Blowout == (pass == 1))
          {
            DrawHit(dc, hit, now - hit.SpawnMs);
          }
        }
      }

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

      if (_ingest.PruneExpired(_hits, now, h => _glyphs.Remove(h)) > 0)
      {
        _dirty = true;
      }

      LastFrameMs = sw.Elapsed.TotalMilliseconds;

      // animated content repaints every frame while live; _dirty adds the frame that clears the canvas
      if (_dirty || _hits.Count > 0)
      {
        InvalidateVisual();
      }

      PublishStats(now);
    }

    private void DrawHit(DrawingContext dc, FctHitState hit, double ageMs)
    {
      var opacity = FctMotion.FadeOpacity(hit, ageMs);
      if (opacity <= 0)
      {
        return;
      }

      // the text itself is ingest's business (FctMotion.RefreshText), which flags TextDirty when a duplicate folds in
      if (!_glyphs.TryGetValue(hit, out var glyphs) || hit.TextDirty)
      {
        BuildGlyphs(hit);
        glyphs = _glyphs[hit];
      }

      var fading = opacity < 1.0;
      if (fading)
      {
        dc.PushOpacity(opacity);
      }

      // PushOpacity creates a render layer per call, so it is used once per hit and only while fading
      var t = FctMotion.Progress(hit, ageMs);
      var x = FctMotion.ArcedX(hit, t);
      var y = FctMotion.RaisedY(hit, t);
      var s = FctMotion.ScaleOf(hit, ageMs);

      var transformed = Math.Abs(s - 1.0) > 0.0001;
      if (transformed)
      {
        // x is the text's center, so it doubles as the scale pivot
        var cy = y + (glyphs.Value.Height / 2);
        dc.PushTransform(new TranslateTransform(x, cy));
        dc.PushTransform(new ScaleTransform(s, s));
        dc.PushTransform(new TranslateTransform(-x, -cy));
      }

      DrawOutlined(dc, glyphs.ValueOutline, glyphs.ValueGlow, glyphs.Value, x, y);

      if (glyphs.Source is not null)
      {
        DrawOutlined(dc, glyphs.SourceOutline, null, glyphs.Source, x, y + (glyphs.Value.Height * 0.95));
      }

      if (transformed)
      {
        dc.Pop();
        dc.Pop();
        dc.Pop();
      }

      if (fading)
      {
        dc.Pop();
      }
    }

    /* Re-lays out only when the displayed text actually changes (spawn and count-ups). */
    private void BuildGlyphs(FctHitState hit)
    {
      RefreshDpi();

      var glyphs = new Glyphs();
      glyphs.Value = MakeText(hit.DisplayText, hit.ValueFontSize, _valueTypeface, BrushFor(hit.ValueArgb));
      glyphs.ValueOutline = MakeText(hit.DisplayText, hit.ValueFontSize, _valueTypeface, _outlineBrush);

      // glow is crit-only (NAG's default non-crit groups carry no halo) — one less cached text per normal hit
      glyphs.ValueGlow = hit.Blowout ? MakeText(hit.DisplayText, hit.ValueFontSize, _valueTypeface, _glowBrush) : null;

      if (!string.IsNullOrEmpty(hit.Source))
      {
        var source = $"({hit.Source})";
        glyphs.Source = MakeText(source, hit.SourceFontSize, _sourceTypeface, BrushFor(hit.SourceArgb));
        glyphs.SourceOutline = MakeText(source, hit.SourceFontSize, _sourceTypeface, _outlineBrush);
      }

      hit.ValueWidth = glyphs.Value.WidthIncludingTrailingWhitespace;
      hit.TextDirty = false;
      _glyphs[hit] = glyphs;
    }

    private FormattedText MakeText(string text, double size, Typeface typeface, Brush brush)
    {
      var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush, _pixelsPerDip);
      ft.TextAlignment = TextAlignment.Center; // x is the value's center in every draw pass
      return ft;
    }

    /* NAG's exact outline: 0 0 / ±1 ±1 (center + 4 diagonals) plus a diagonal glow stack for crits. */
    private void DrawOutlined(DrawingContext dc, FormattedText outline, FormattedText glow, FormattedText fill, double x, double y)
    {
      DrawOffset(dc, outline, x, y, 0, 0);
      DrawOffset(dc, outline, x, y, -1, -1);
      DrawOffset(dc, outline, x, y, 1, -1);
      DrawOffset(dc, outline, x, y, -1, 1);
      DrawOffset(dc, outline, x, y, 1, 1);

      if (glow is not null)
      {
        DrawOffset(dc, glow, x, y, -3, -3);
        DrawOffset(dc, glow, x, y, 3, -3);
        DrawOffset(dc, glow, x, y, -3, 3);
        DrawOffset(dc, glow, x, y, 3, 3);
      }

      DrawOffset(dc, fill, x, y, 0, 0);
    }

    private void DrawOffset(DrawingContext dc, FormattedText ft, double x, double y, double dx, double dy)
    {
      dc.DrawText(ft, new Point(x + dx, y + dy));
      _statDrawsTotal++;
    }

    private static Brush BrushFor(int argb)
    {
      if (!_brushes.TryGetValue(argb, out var brush))
      {
        brush = FrozenBrush(argb);
        _brushes[argb] = brush;
      }

      return brush;
    }

    /* All brushes are frozen so they can be shared without per-frame allocation cost. */
    private static Brush FrozenBrush(int argb)
    {
      var brush = new SolidColorBrush(Color.FromArgb((byte)((argb >>> 24) & 0xFF), (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF)));
      brush.Freeze();
      return brush;
    }

    private void RefreshDpi()
    {
      if (_pixelsPerDip > 0 && PresentationSource.FromVisual(this) is null)
      {
        return;
      }

      _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
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
  }
}
