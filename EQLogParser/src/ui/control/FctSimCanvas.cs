using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EQLogParser
{
  /* The screen area a simulated hit renders in. */
  internal enum FctSimLane
  {
    MyDamage,
    DamageTaken,
    Healing,
    Crit
  }

  /* Per-hit render state for the FCT simulation canvas. There is deliberately no UIElement per hit:
   * every active hit is plain data drawn in a single OnRender pass and positioned/scaled/faded with
   * math each frame (no Storyboards, no per-element effects). Rationale: docs/NagFctReference.md. */
  internal sealed class FctSimHitState
  {
    public FctSimLane Lane;
    public double X, Y;                       // base position of the value text
    public double ValueFontSize, SourceFontSize;
    public Brush ValueBrush, SourceBrush;
    public double FloatDistance, DriftX, LifetimeMs, FadeMs;
    public bool Blowout;                      // crit scale curve instead of float curve
    public double SpawnMs, AgeAtCountStartMs; // canvas clock time in ms
    public double TargetValue, CountBaseValue;
    public double CountUpMs;                  // 0 => no count-up
    public string Action;
    public FormattedText ValueText, ValueOutline, ValueGlow, SourceText, SourceOutline;
    public string LastValueKey = "";
  }

  /*
   * Single-visual floating combat text renderer. One CompositionTarget.Rendering pass per frame
   * updates all active hits and one OnRender draws them, mirroring how NAG's browser compositor
   * runs CSS keyframes (docs/NagFctReference.md). Outline/glow are vector offset-draws of the
   * cached FormattedText, not GPU effects. The loop unsubscribes itself when idle.
   */
  internal class FctSimCanvas : FrameworkElement, IFctSimCanvas
  {
    private const double MyDamageFontSize = 30;
    private const double DamageTakenFontSize = 28;
    private const double HealingFontSize = 24;
    private const double CritFontSize = 44;
    private const double MinorFontSize = 19;
    private const double SourceFontMin = 12;

    /* A/B: EQ's FCT font is a pixel font, so un-antialiased rasterization is both cheaper and more faithful. */
    private const bool AliasedTextRendering = true;

    private static readonly FontFamily _fontFamily = new("Arial");
    private static readonly Typeface _valueTypeface = new(_fontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface _sourceTypeface = new(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /* PushOpacity creates a render layer per call, so it is only used once per hit while that hit
     * is actually fading — never per draw (that was the cause of the initial lag). */
    private readonly Random _rand = new(1234);
    private readonly List<FctSimHitState> _hits = [];
    private readonly Brush _outlineBrush = MakeFrozenBrush(0, 0, 0, 235);
    private readonly Brush _glowBrush = MakeFrozenBrush(0, 0, 0, 64);
    private double _pixelsPerDip;
    private Stopwatch _clock;

    /* Per-second stats, published by the render loop for the simulation header. */
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
      if (IsConnectedToPresentationSource())
      {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
      }

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
    }

    /*
     * Adds a hit. Crits divert to the random crit band regardless of lane; other lanes stack upward
     * from their bottom anchor like NAG's flex columns (oldest on top gets recycled on overflow).
     */
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

      var now = _clock.Elapsed.TotalMilliseconds;
      double x, y;

      if (crit)
      {
        lane = FctSimLane.Crit;
        x = w * 0.30 + _rand.NextDouble() * (w * 0.40);
        y = h * 0.10 + _rand.NextDouble() * (h * 0.28);
      }
      else
      {
        switch (lane)
        {
          case FctSimLane.MyDamage:
            x = w * 0.24;
            y = NextStackY(lane, h - 110, 52, 170);
            break;

          case FctSimLane.DamageTaken:
            x = w * 0.63;
            y = NextStackY(lane, h - 110, 50, 170);
            break;

          default: // Healing
            lane = FctSimLane.Healing;
            x = w / 2 + (_rand.NextDouble() * 60 - 30);
            y = NextStackY(lane, h - 44, 36, h * 0.55);
            break;
        }
      }

      var state = NewHitState(lane, value, action, crit, minor, x, y, now);
      _hits.Add(state);
      _dirty = true;
    }

    /*
     * Folds a small hit into the newest live non-crit hit in the lane (NAG's accumulate mode):
     * the visible value counts up instead of spawning a new element, which also exercises the
     * FormattedText re-layout path. Returns false if nothing was eligible to absorb it.
     */
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

      foreach (var hit in _hits)
      {
        var age = now - hit.SpawnMs;
        var opacity = FadeOpacity(age, hit.LifetimeMs, hit.FadeMs);
        if (opacity <= 0)
        {
          continue;
        }

        RefreshValueText(hit, age);
        DrawHit(dc, hit, age, opacity);
      }

      _dirty = false;
    }

    private void OnRendering(object sender, EventArgs e)
    {
      var sw = Stopwatch.StartNew();
      var now = _clock.Elapsed.TotalMilliseconds;

      // remove expired hits before feeding new ones so stacks stay bounded
      for (var i = _hits.Count - 1; i > -1; i--)
      {
        if (now - _hits[i].SpawnMs > _hits[i].LifetimeMs)
        {
          _hits.RemoveAt(i);
          _dirty = true;
        }
      }

      EventsFrame?.Invoke(now);

      LastFrameMs = sw.Elapsed.TotalMilliseconds;

      // invalidate whenever the hit set changed since the last render (including the frame that clears it)
      if (_dirty)
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

    private FctSimHitState NewHitState(FctSimLane lane, double value, string action, bool crit, bool minor, double x, double y, double now)
    {
      var state = new FctSimHitState
      {
        Lane = lane,
        X = x,
        Y = y,
        SpawnMs = now,
        TargetValue = value,
        CountBaseValue = value,
        Action = action,
        // source line is always drawn slightly dimmer — baked into the brush so no per-draw opacity is needed
        SourceBrush = MakeFrozenBrush(0x6E, 0x93, 0xC8, 242),
      };

      switch (lane)
      {
        case FctSimLane.Crit:
          state.Blowout = true;
          state.LifetimeMs = 2800;
          state.FadeMs = 700;
          state.ValueFontSize = CritFontSize;
          state.ValueBrush = MakeFrozenBrush(0xFF, 0xD2, 0x3F);
          break;

        case FctSimLane.Healing:
          state.LifetimeMs = 2400;
          state.FadeMs = 500;
          state.ValueFontSize = HealingFontSize;
          state.ValueBrush = MakeFrozenBrush(0x77, 0xE0, 0x61);
          state.FloatDistance = minor ? 40 + _rand.NextDouble() * 20 : 55 + _rand.NextDouble() * 30;
          state.DriftX = _rand.NextDouble() * 24 - 12;
          break;

        default: // MyDamage / DamageTaken
          state.LifetimeMs = minor ? 1500 : 3600;
          state.FadeMs = 500;
          state.ValueFontSize = minor ? MinorFontSize : (lane == FctSimLane.MyDamage ? MyDamageFontSize : DamageTakenFontSize);
          state.ValueBrush = lane == FctSimLane.MyDamage
            ? MakeFrozenBrush(0xFF, 0xF4, 0xC1)
            : MakeFrozenBrush(0xFF, 0x7A, 0x5C);
          if (!minor)
          {
            state.FloatDistance = 80 + _rand.NextDouble() * 50;
            state.DriftX = (_rand.NextDouble() * 2 - 1) * (30 + _rand.NextDouble() * 40);
          }

          break;
      }

      state.SourceFontSize = Math.Max(SourceFontMin, state.ValueFontSize * 0.42);
      state.LastValueKey = FctText.FormatHitValue(value);
      BuildTexts(state);
      return state;
    }

    /* Bottom-anchored stacking: the newest hit sits rowHeight above the topmost live hit in its lane. */
    private double NextStackY(FctSimLane lane, double baseY, double rowHeight, double minY)
    {
      double? top = null;

      foreach (var hit in _hits)
      {
        if (hit.Lane == lane && (top is null || hit.Y < top))
        {
          top = hit.Y;
        }
      }

      var y = top is null ? baseY : top.Value - rowHeight;

      // keep the lane inside the canvas by recycling its oldest hit
      if (y < minY)
      {
        for (var i = 0; i < _hits.Count; i++)
        {
          if (_hits[i].Lane == lane)
          {
            _hits.RemoveAt(i);
            break;
          }
        }

        y = top is null ? baseY : top.Value - rowHeight;
      }

      return y;
    }

    private double GetDisplayValue(FctSimHitState hit, double ageMs)
    {
      if (hit.CountUpMs <= 0 || hit.TargetValue == hit.CountBaseValue)
      {
        return hit.TargetValue;
      }

      var p = Math.Clamp((ageMs - hit.AgeAtCountStartMs) / hit.CountUpMs, 0.0, 1.0);
      return hit.CountBaseValue + ((hit.TargetValue - hit.CountBaseValue) * p);
    }

    /* Re-lays out only when the displayed value actually changes (i.e. during count-ups). */
    private void RefreshValueText(FctSimHitState hit, double ageMs)
    {
      var key = FctText.FormatHitValue(GetDisplayValue(hit, ageMs));
      if (key == hit.LastValueKey)
      {
        return;
      }

      hit.LastValueKey = key;
      BuildTexts(hit);
    }

    private void BuildTexts(FctSimHitState hit)
    {
      if (_pixelsPerDip is 0 && IsConnectedToPresentationSource())
      {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
      }

      var value = hit.LastValueKey;
      hit.ValueText = MakeText(value, hit.ValueFontSize, _valueTypeface, hit.ValueBrush);
      hit.ValueOutline = MakeText(value, hit.ValueFontSize, _valueTypeface, _outlineBrush);
      // glow is crit-only (NAG's default non-crit groups carry no halo) — one less cached text per normal hit
      hit.ValueGlow = hit.Blowout ? MakeText(value, hit.ValueFontSize, _valueTypeface, _glowBrush) : null;
      var source = $"({hit.Action})";
      hit.SourceText = MakeText(source, hit.SourceFontSize, _sourceTypeface, hit.SourceBrush);
      hit.SourceOutline = MakeText(source, hit.SourceFontSize, _sourceTypeface, _outlineBrush);
    }

    private FormattedText MakeText(string text, double size, Typeface typeface, Brush brush) =>
      new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush, _pixelsPerDip);

    private bool IsConnectedToPresentationSource() => PresentationSource.FromVisual(this) is not null;

    /* All static brushes are frozen so they can be shared without per-frame allocation cost. */
    private static Brush MakeFrozenBrush(byte r, byte g, byte b, byte alpha = 255)
    {
      var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
      brush.Freeze();
      return brush;
    }

    /* One PushOpacity per hit, and only while it is fading in/out (the common full-opacity case pushes nothing). */
    private void DrawHit(DrawingContext dc, FctSimHitState hit, double ageMs, double opacity)
    {
      var fading = opacity < 1.0;
      if (fading)
      {
        dc.PushOpacity(opacity);
      }

      if (hit.Blowout)
      {
        var s = BlowoutScale(ageMs, hit.LifetimeMs);
        var cx = hit.X + hit.ValueText.Width / 2;
        var cy = hit.Y + hit.ValueText.Height / 2;
        dc.PushTransform(new TranslateTransform(cx, cy));
        dc.PushTransform(new ScaleTransform(s, s));
        dc.PushTransform(new TranslateTransform(-cx, -cy));
        DrawValue(dc, hit.ValueOutline, hit.ValueGlow, hit.ValueText, hit.X, hit.Y);
        DrawSource(dc, hit.SourceOutline, hit.SourceText, hit.X, hit.Y + hit.ValueText.Height * 0.95);
        dc.Pop();
        dc.Pop();
        dc.Pop();
      }
      else
      {
        var floatMs = Math.Min(1500, hit.LifetimeMs * 0.55);
        var p = Math.Clamp(ageMs / floatMs, 0.0, 1.0);
        var dy = -hit.FloatDistance * EaseOutCubic(p);
        var dp = ageMs / hit.LifetimeMs;
        var dx = hit.DriftX * (dp * dp);
        DrawValue(dc, hit.ValueOutline, hit.ValueGlow, hit.ValueText, hit.X + dx, hit.Y + dy);
        DrawSource(dc, hit.SourceOutline, hit.SourceText, hit.X + dx, hit.Y + dy + hit.ValueText.Height * 0.95);
      }

      if (fading)
      {
        dc.Pop();
      }
    }

    /* NAG blowout: quick ramp to ~1.45x, hold, then shrink to ~0 while fading. */
    private static double BlowoutScale(double ageMs, double lifetimeMs)
    {
      const double inMs = 90;
      const double outMs = 700;
      if (ageMs < inMs)
      {
        return 1 + (0.45 * (ageMs / inMs));
      }

      if (ageMs < lifetimeMs - outMs)
      {
        return 1.45;
      }

      var p = Math.Clamp((ageMs - (lifetimeMs - outMs)) / outMs, 0.0, 1.0);
      return 1.45 - ((1.45 - 0.06) * p * p);
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

    /* Outline/glow are offset draws of the cached text (NAG's CSS text-shadow stack, in vector form). */
    private void DrawValue(DrawingContext dc, FormattedText outline, FormattedText glow, FormattedText fill, double x, double y)
    {
      DrawOutlined(dc, outline, glow, fill, x, y);
    }

    private void DrawSource(DrawingContext dc, FormattedText outline, FormattedText fill, double x, double y)
    {
      // no glow on the source line
      DrawOutlined(dc, outline, null, fill, x, y);
    }

    /* NAG's exact outline: 0 0 / ±1 ±1 (center + 4 diagonals) — see floating-combat-text styles. */
    private void DrawOutlined(DrawingContext dc, FormattedText outline, FormattedText glow, FormattedText fill, double x, double y)
    {
      // 5-point outline
      DrawOffset(dc, outline, x, y, 0, 0);
      DrawOffset(dc, outline, x, y, -1, -1);
      DrawOffset(dc, outline, x, y, 1, -1);
      DrawOffset(dc, outline, x, y, -1, 1);
      DrawOffset(dc, outline, x, y, 1, 1);

      // radial-ish glow approximation on the diagonals (crits only)
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

    private static double EaseOutCubic(double p) => 1 - Math.Pow(1 - p, 3);
  }
}
