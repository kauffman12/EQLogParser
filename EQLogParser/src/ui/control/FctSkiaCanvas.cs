using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using SkiaSharp;
using SkiaSharp.Views.WPF;

namespace EQLogParser
{
  /* Per-hit render state for the SkiaSharp FCT backend. Plain data — no WPF text objects, so the
   * per-frame cost is C++-side shaping/drawing on one SKCanvas instead of a WPF display list. */
  internal sealed class FctSkiaHit
  {
    public FctSimLane Lane;
    public double X0, Y0;                      // spawn position (logical px)
    public double Rise, Arc;                   // total upward travel / sideways arc amplitude
    public double SideMin, SideMax;            // half-canvas clamp for value left edge + width
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
    private const double CritFontSize = 42;
    private const double MinorFontSize = 19;
    private const double SourceFontMin = 12;
    private const float GlowSigma = 5f;

    /* Ref-counted blur-glow sprite: rendered once per unique (value, size), disposed when the last
     * hit using it expires. NAG's glow is a black wide radial text-shadow, so color is irrelevant. */
    private sealed class HaloEntry
    {
      public SKImage Image;
      public int Refs, Pad;
    }

    private readonly Random _rand = new(1234);
    private readonly List<FctSkiaHit> _hits = [];
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

      // home band: center of the lane's half, jittered; crits spread wider across their half
      var cx = lane switch
      {
        FctSimLane.DamageTaken => w * 0.30,
        FctSimLane.HealingReceived => w * 0.42,
        FctSimLane.Crit => leftSide ? w * 0.36 : w * 0.70,
        FctSimLane.HealingDealt => w * 0.78,
        _ => w * 0.60, // DamageDealt
      };

      var hit = NewHitState(lane, value, action, minor,
        x: cx + ((_rand.NextDouble() * 2 - 1) * (lane == FctSimLane.Crit ? w * 0.17 : w * 0.09)),
        y: h * (0.68 + _rand.NextDouble() * 0.17), // bottom third
        now: _clock.Elapsed.TotalMilliseconds);

      hit.Rise = h * (0.34 + _rand.NextDouble() * 0.12) * (lane is FctSimLane.HealingDealt or FctSimLane.HealingReceived ? 0.8 : 1.0);
      hit.Arc = (_rand.NextDouble() * 2 - 1) * w * (lane == FctSimLane.Crit ? 0.15 : 0.12);

      hit.SideMin = leftSide ? 8 : w / 2 + 4;
      hit.SideMax = leftSide ? w / 2 - 4 : w - 8;

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

      using var image = _surface.Snapshot();
      dc.DrawImage(image.ToWriteableBitmap(), new Rect(0, 0, ActualWidth, ActualHeight));
      _dirty = false;
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
        case FctSimLane.Crit:
          hit.Blowout = true;
          hit.LifetimeMs = 2800;
          hit.FadeMs = 700;
          hit.ValueFontSize = CritFontSize;
          hit.ValueColor = new SKColor(0xFF, 0xA3, 0x2E); // orange
          break;

        case FctSimLane.HealingDealt or FctSimLane.HealingReceived:
          hit.LifetimeMs = 2400;
          hit.FadeMs = 500;
          hit.ValueFontSize = HealingFontSize;
          hit.ValueColor = new SKColor(0x7F, 0xE0, 0x61); // green
          break;

        case FctSimLane.DamageDealt:
          hit.LifetimeMs = minor ? 1500 : 3400;
          hit.FadeMs = 500;
          hit.ValueFontSize = minor ? MinorFontSize : DamageDealtFontSize;
          hit.ValueColor = new SKColor(0xFF, 0xD7, 0x5E); // yellow
          break;

        default: // DamageTaken
          hit.LifetimeMs = 3400;
          hit.FadeMs = 500;
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

      // rise over the whole lifetime; arc grows as the hit gets high, clamped to its half
      var p = Math.Clamp(ageMs / hit.LifetimeMs, 0.0, 1.0);
      var x = ArcedX(hit, p);
      var y = RaisedY(hit, p);

      if (hit.Blowout)
      {
        var s = (float)BlowoutScale(ageMs, hit.LifetimeMs);
        var cx = x + (hit.ValueWidth / 2.0);
        var cy = y + (hit.ValueFontSize * 0.45);
        canvas.Save();
        canvas.Translate((float)cx, (float)cy);
        canvas.Scale(s, s);
        canvas.Translate(-(float)cx, -(float)cy);
      }

      var valueBase = y + (hit.ValueFontSize * 0.82);

      // glow underlay (pre-blurred sprite, crits only); white modulation keeps the black halo, alpha fades it
      if (hit.HaloKey is not null && _halos.TryGetValue(hit.HaloKey, out var halo))
      {
        var blit = MakePaint(SKColors.White, alpha, SKPaintStyle.Fill, 0);
        canvas.DrawImage(halo.Image, (float)(x - hit.HaloPad), (float)(y - hit.HaloPad), blit);
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
      canvas.DrawText(text, x, baselineY, SKTextAlign.Left, font, outline);
      outline.Dispose();

      var fill = MakePaint(color, alpha, SKPaintStyle.Fill, 0);
      canvas.DrawText(text, x, baselineY, SKTextAlign.Left, font, fill);
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

    /* Rise: fast at first (ease-out), over the whole lifetime. */
    private static double RaisedY(FctSkiaHit hit, double p) => hit.Y0 - (hit.Rise * EaseOutCubic(p));

    /* Arc: quadratic growth, so horizontal travel mostly happens as the hit gets high; clamped to
     * the hit's half of the canvas including its text width. */
    private static double ArcedX(FctSkiaHit hit, double p) =>
      Math.Clamp(hit.X0 + (hit.Arc * p * p), hit.SideMin, hit.SideMax - hit.ValueWidth);

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

    private static double EaseOutCubic(double p) => 1 - Math.Pow(1 - p, 3);

    private void CountDraw(int n) => _statDrawsTotal += n;
  }
}
