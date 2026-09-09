using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  /*
   * The FCT renderer. All hits rasterize into one CPU SKSurface per frame and blit into the WPF tree as a single image, so per-frame cost
   * tracks C++ draw ops (~5 per hit) rather than WPF display-list elements - the measured reason it beat a DrawingContext path roughly 100 fps
   * to 30 under raid spam (docs/NagFctReference.md, and that rival path is now deleted: keeping a loser around meant every pump rule had to be
   * written twice and kept in step, which is exactly how the configure-mode demo ended up animating at two frames a second). Hit policy lives in
   * FctIngest/FctLayout/FctMotion; this file owns Skia resources, the frame pump and the blit.
   *
   * Outline is FillAndStroke (2 passes instead of NAG's 5), glow is a true Gaussian blur baked once per
   * unique crit label into a ref-counted halo sprite. See docs/NagFctReference.md and
   * docs/DesignNotes.md → Floating Combat Text.
   */
  internal class FctSkiaCanvas : FrameworkElement
  {

    // re-read DPI about once a second: per-monitor scaling changes neither the size nor any event we get
    private const double DpiCheckMs = 1000;

    private const float GlowSigma = 5f;

    /* Where "(source)" sits relative to its amount (FctLabelSide): the shipped Below is where this overlay has always
       drawn it. Changing it repaints rather than restarts — placement is read by the draw pass, so the next frame of
       every hit in flight already wears the new arrangement; no number in motion is thrown away by a typography choice. */
    private FctLabelSide _labelSide = FctLabelSide.Below;

    public FctLabelSide LabelSide
    {
      get => _labelSide;
      set
      {
        if (_labelSide != value)
        {
          _labelSide = value;
          _dirty = true;
        }
      }
    }

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

    /* The configure-mode examples, kept apart from _hits so no policy path — folding, eviction, expiry, diagnostics — ever sees them. */
    /* The configure-mode demo (FctDemo): its own ingest and its own list, drawn after the real numbers. Wanted is separate from
       active because the clock may not exist yet when configure mode opens - the pump starts it as soon as there is one. */
    private readonly FctDemo _demo = new();
    private bool _demoWanted;

    private SKTypeface _boldTypeface, _regularTypeface;
    private SKMaskFilter _glowBlur;

    /* Paints are reused and reconfigured per draw: allocating 4-6 of them per hit per frame was the hot path. */
    private SKPaint _outlinePaint, _fillPaint, _blitPaint;

    /* Long-lived measuring paint (3.119.2 MeasureText requires one) and the halo bake's blurred black — built with
       the renderer, not per spawn. See EnsureSkiaResources. */
    private SKPaint _measurePaint, _haloPaint;

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
    /*
     * Which motion new hits get (hold / fountain / pulse / spray); forwarded to ingest, which is what applies it. Hits already in flight keep the
     * style they were born with, which is why changing it mid-fight is a way to compare rather than a way to break something.
     *
     * Configure mode restarts its loop on a change: the loop exists to show what this control does, and waiting up to twelve seconds for the next
     * cycle to reach the part where that is visible is not an effect anybody can see.
     */
    public FctMotionStyle MotionStyle
    {
      get => _ingest.Style;
      set
      {
        if (_ingest.Style == value)
        {
          return;
        }

        _ingest.Style = value;
        RestartDemo();
      }
    }

    /*
     * Which region scheme new hits spawn into (see FctStage): halves, bands, and which side incoming sits on. Forwarded to ingest the way
     * MotionStyle is, with the same contract — in-flight numbers keep the stage they were born under, and configure mode restarts its loop
     * on a change so the choice can be judged on the very next number rather than twelve seconds later.
     */
    public FctLayoutChoice Layout
    {
      get => _ingest.Layout;
      set
      {
        if (_ingest.Layout.Equals(value))
        {
          return;
        }

        _ingest.Layout = value;
        RestartDemo();
      }
    }

    public double Fps { get; private set; }
    public double AvgFrameMs { get; private set; }

    /* Worst frame in the current stats window: average frame time hides the spike that reads as a hitch. */
    public double MaxFrameMs { get; private set; }

    /* What the monitor is actually running at, so a low fps can be told apart from a deliberate pacing cap. */
    public double DisplayHz => _pacer.DisplayHz;
    public double LastFrameMs { get; private set; }
    public double DrawsPerSec { get; private set; }
    public int DroppedCount => _ingest.DroppedCount;

    /* How many numbers the "hide under" filter has taken off screen; shown beside the drop count, because a filter
     * doing its job and a bug swallowing numbers should never look the same from outside. */
    public int HiddenCount => _ingest.HiddenCount;

    /* How many numbers the category switches have taken off screen — its own count beside "hidden" because "you turned
     * it off" and "it was under your threshold" are different answers a working overlay should be able to give. */
    public int FilteredCount => _ingest.FilteredCount;

    /*
     * The category switches, with Threshold's contract: they change what gets through the gate from the next number on
     * (demo included — Advance copies the gates every frame), never what is already in flight, and nothing reaches
     * settings.ini until Save. Restarting the demo on a flip is what makes "heals off" provable on screen instantly
     * rather than after somebody finishes a fight.
     */
    public bool ShowDealt
    {
      get => _ingest.ShowDealt;
      set
      {
        if (_ingest.ShowDealt == value)
        {
          return;
        }

        _ingest.ShowDealt = value;
        RestartDemo();
      }
    }

    public bool ShowTaken
    {
      get => _ingest.ShowTaken;
      set
      {
        if (_ingest.ShowTaken == value)
        {
          return;
        }

        _ingest.ShowTaken = value;
        RestartDemo();
      }
    }

    public bool ShowHeals
    {
      get => _ingest.ShowHeals;
      set
      {
        if (_ingest.ShowHeals == value)
        {
          return;
        }

        _ingest.ShowHeals = value;
        RestartDemo();
      }
    }

    public bool ShowProcs
    {
      get => _ingest.ShowProcs;
      set
      {
        if (_ingest.ShowProcs == value)
        {
          return;
        }

        _ingest.ShowProcs = value;
        RestartDemo();
      }
    }

    /*
     * The display threshold forwarded to ingest with MotionStyle's contract: it changes what gets through the gate,
     * never what is already on screen, and configure mode restarts its demo loop so the effect can be judged on the
     * next number rather than after a Save. The demo runs small heals and big crits precisely so this dial has
     * something to filter.
     */
    public double Threshold
    {
      get => _ingest.Threshold;
      set
      {
        if (Math.Abs(_ingest.Threshold - value) < 0.001)
        {
          return;
        }

        _ingest.Threshold = value;
        RestartDemo();
      }
    }

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
    public void AddHit(FctLane lane, double value, string source, bool crit, bool minor = false, bool periodic = false, string valueText = null, bool proc = false, FctSpecial special = FctSpecial.None)
    {
      if (_clock is null)
      {
        return;
      }

      /* ReleaseHalo is the eviction sink: pulse mode can take a full cell off a hit still on screen, and the surface that
       * blurred its glow has to hear about it or the reference count never comes back down. */
      var hit = _ingest.Accept(_hits, lane, value, source, crit, minor, periodic, valueText, ActualWidth, ActualHeight,
        _clock.Elapsed.TotalMilliseconds, proc, special, ReleaseHalo);
      if (hit is null)
      {
        _dirty = true; // either folded into a live hit or dropped at the cap: either way, repaint
        return;
      }

      RebuildGlyphs(hit);
      _dirty = true;
    }

    /*
     * A resize moves the numbers that are already flying, not just the ones after it: their bands, travel and cells were measured
     * against the old size and motion never revisits them. FctResize owns the mapping; _dirty so a paused canvas repaints the new
     * geometry instead of waiting for the next hit.
     */
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
      base.OnRenderSizeChanged(sizeInfo);
      RefreshDpi();

      /* The stage carries the layout: a number's side comes from its lane at spawn, but its band and territory are fractions of whatever the
         regions are now, and motion never revisits either. */
      var stage = _ingest.Layout.Stage(sizeInfo.NewSize.Width, sizeInfo.NewSize.Height);
      FctResize.Rescale(_hits, sizeInfo.PreviousSize.Width, sizeInfo.PreviousSize.Height, stage);

      /* Demo numbers were laid out against the old size too, and motion never revisits that: mapped like any other live hit. */
      _demo.Rescale(sizeInfo.PreviousSize.Width, sizeInfo.PreviousSize.Height, sizeInfo.NewSize.Width, sizeInfo.NewSize.Height);

      _dirty = true;
    }

    /*
     * Starts / stops the configure-mode demo (FctDemo): a short loop of events - melee, crits, a folding DoT, a proc, heals, a hit on
     * you, the zero-damage words - so a size or style change can be seen moving without a fight.
     *
     * They run through a private FctIngest into a private list, so nothing here touches the overlay's counters or its real numbers:
     * no lane slot taken from play, nothing folded into a live hit, no demo drop counted as lost data. Every one of them owns substrate
     * like any other number - glyph run, and for a crit, a referenced halo - so both paths release through ReleaseHalo.
     */
    public void StartDemo()
    {
      _demoWanted = true;
      _dirty = true;
    }

    public void StopDemo()
    {
      _demoWanted = false;
      _demo.Clear(ReleaseHalo);
      _dirty = true;
    }

    /*
     * Put the configure-mode loop back to its first cue without taking it away. What a control just changed should be visible now rather than when a
     * twelve second cycle next reaches the part where it shows, and this loop is the only thing in configure mode a player can look at. Only demo
     * numbers are cleared: configure mode never touches play, so real numbers finish whatever they were doing.
     */
    public void RestartDemo()
    {
      if (!_demoWanted && !_demo.Active)
      {
        return;
      }

      _demo.Clear(ReleaseHalo);

      if (_clock is not null)
      {
        _demo.Start(_clock.Elapsed.TotalMilliseconds);
      }

      _dirty = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
      var now = _clock?.Elapsed.TotalMilliseconds ?? 0;
      var scale = _pixelsPerDip > 0 ? _pixelsPerDip : 1.0;
      var w = (int)Math.Ceiling(ActualWidth * scale);
      var h = (int)Math.Ceiling(ActualHeight * scale);

      /*
       * Both early-outs clear _dirty, because nothing was drawn and leaving the flag set turns them into a repaint loop:
       * OnRendering invalidates whenever _dirty is set, so an overlay dragged under 50 px — or one whose surface cannot be
       * allocated — would ask WPF to render at display rate forever, arriving here, drawing nothing, and asking again.
       * Clearing costs nothing: WPF renders again when the size comes back, and live hits keep invalidating anyway.
       */
      if (w < 50 || h < 50)
      {
        _dirty = false;
        return;
      }

      if (!EnsureSurface(w, h))
      {
        _dirty = false;
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

      /* The examples last and on top: they are what configure mode is looking at, and they cannot collide with anything because
         nothing about them is real. Their age comes from their phase, not the clock, which is what holds them still. */
      /* The demo last and on top, aged from its own spawn time like any real number: it is animation, not an exhibit. Two passes for
         the same reason the real hits get two - a crit should sit above what was already on screen. */
      for (var pass = 0; pass < 2; pass++)
      {
        foreach (var hit in _demo.Hits)
        {
          if (hit.Blowout == (pass == 1))
          {
            DrawHit(canvas, hit, now - hit.SpawnMs);
          }
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

      /* Idle and nothing to clear: skip the raster entirely. A wanted or running demo is never idle - it is animation, and animation
         that is not pumped stands still, which is the difference between a preview and an empty window. */
      if (_hits.Count == 0 && !_dirty && !_demoWanted && !_demo.Active)
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

      if (_demoWanted && !_demo.Active)
      {
        _demo.Start(now);
      }

      if (_demo.Advance(now, ActualWidth, ActualHeight, _ingest.Style, _ingest.Layout, RebuildGlyphs, ReleaseHalo, _ingest))
      {
        _dirty = true;
      }

      LastFrameMs = sw.Elapsed.TotalMilliseconds;

      /*
       * Animated content: a hit's position is a function of the clock, so any frame with live hits must repaint or the text stands still until the
       * next spawn or expiry. _dirty adds the one extra frame that clears the canvas when the last one goes.
       *
       * "Live" means every list that moves, and the configure-mode demo is one of them. It deliberately does not live in _hits - keeping it out is
       * what protects the counters - so a pump keyed on _hits alone stops asking for frames between cues, repaints twice a second, and the numbers
       * hang in place before jumping: a slideshow. The demo answers that question about itself (Animated) rather than leaving each host to
       * re-derive it, because the quiet tail at the end of a cycle must not cost four seconds of rasters per loop.
       */
      if (_dirty || _hits.Count > 0 || _demo.Animated)
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

      // text is ingest's business (FctMotion.RefreshText), which flags TextDirty when a duplicate folds in
      if (hit.TextDirty)
      {
        RebuildGlyphs(hit);
      }

      var t = FctMotion.Progress(hit, ageMs);
      var s = FctMotion.ScaleOf(hit, ageMs);

      // the scale goes in: right-aligned values keep their right edge on the rail at every frame's width
      var x = FctMotion.ArcedX(hit, t, s);
      var y = FctMotion.RaisedY(hit, t);

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

      // The special event's mark, hung outside the number's left edge inside the same pop transform: it scales and
      // fades as one object with the value, and the odometer never moves for it (FctHitState.IconAllowance).
      if (hit.Special is not FctSpecial.None)
      {
        DrawMark(canvas, hit, x, y, opacity);
      }

      // value: black outline pass, then colored fill pass; its x is the value's own center in every label placement,
      // so amounts keep one spine whether the words hang below them (the shipped line) or inline left/right of them.
      var valueBase = y + (hit.ValueFontSize * 0.82);
      DrawOutlinedText(canvas, hit.DisplayText, (float)x, (float)valueBase, hit.ValueFontSize, true, hit.ValueArgb, opacity);

      if (hit.SourceLabel is not null)
      {
        /* Inline labels share the value's baseline — two font sizes on one line reads as a sentence, which is the whole
           reason somebody picks it — and lean against the measured edge of the number with a word-space between. */
        double labelX = x;
        double labelBase = valueBase;

        if (_labelSide is FctLabelSide.Below)
        {
          labelBase = y + (hit.ValueFontSize * 1.25) + (hit.SourceFontSize * 0.85);
        }
        else
        {
          var gap = hit.SourceFontSize * 0.5;
          var half = hit.ValueWidth / 2.0 + gap + hit.SourceWidth / 2.0;

          // a mark on the left is furniture too: the label leans against the glyph, not through it
          labelX = _labelSide is FctLabelSide.Right ? x + half : x - (half + hit.IconAllowance);
        }

        DrawOutlinedText(canvas, hit.SourceLabel, (float)labelX, (float)labelBase, hit.SourceFontSize, false, hit.SourceArgb, opacity);
      }

      if (s != 1.0)
      {
        canvas.Restore();
      }
    }

    /*
     * The mark, drawn the same way as the text it rides beside: black outline pass under a colored fill, so a glyph
     * over bright lava rock stays as readable as the number next to it. Paths are built once per event (FctMarks);
     * this only places and scales — no allocation in the frame path.
     */
    private void DrawMark(SKCanvas canvas, FctHitState hit, double textCenterX, double top, double opacity)
    {
      var mark = FctMarks.For(hit.Special);
      if (mark.Body.IsEmpty)
      {
        return;
      }

      var size = hit.ValueFontSize * FctStyle.IconSizeFrac;
      var gap = hit.ValueFontSize * FctStyle.IconGapFrac;
      var left = (textCenterX - (hit.ValueWidth / 2.0)) - gap - size;
      var topOfMark = top + ((FctLayout.TextHeight(hit) - size) / 2.0);
      var scale = size / FctMarks.Unit;

      canvas.Save();
      canvas.Translate((float)left, (float)topOfMark);
      canvas.Scale((float)scale, (float)scale);

      Configure(_outlinePaint, ColorOf(BlackArgb, opacity), SKPaintStyle.StrokeAndFill, 2.4f);
      canvas.DrawPath(mark.Body, _outlinePaint);
      Configure(_fillPaint, ColorOf(hit.ValueArgb, opacity), SKPaintStyle.Fill, 0);
      canvas.DrawPath(mark.Body, _fillPaint);

      if (mark.Dark is not null)
      {
        Configure(_fillPaint, ColorOf(BlackArgb, opacity), SKPaintStyle.Fill, 0);
        canvas.DrawPath(mark.Dark, _fillPaint);
      }

      canvas.Restore();
      CountDraw(mark.Dark is null ? 2 : 3);
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

    /* Measures the label and (re)acquires the crit halo: both depend on the text, which changes when a duplicate folds in. */
    private void RebuildGlyphs(FctHitState hit)
    {
      EnsureSkiaResources();
      var measured = TextWidth(hit.DisplayText, hit.ValueFontSize, bold: true);

      /*
       * The measurement is what ArcedX clamps against, so a number that gains a "×3" gets a slightly narrower travel band —
       * correct rather than sticky: the wider text really does need the room. It only ever grows (a count never shrinks), so
       * there is no flicker to guard against now that folded hits stop changing their face value.
       */
      hit.ValueWidth = measured;
      hit.SourceWidth = hit.SourceLabel is null ? 0 : TextWidth(hit.SourceLabel, hit.SourceFontSize, bold: false);
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

          var textWidth = (int)Math.Ceiling(font.MeasureText(hit.DisplayText, _measurePaint));
          var textHeight = (int)Math.Ceiling(hit.ValueFontSize * 1.25);

          using var surf = SKSurface.Create(new SKImageInfo(textWidth + (pad * 2), textHeight + (pad * 2)));
          if (surf is null)
          {
            return;
          }

          surf.Canvas.DrawText(hit.DisplayText, pad, (float)(pad + (hit.ValueFontSize * 0.82)), SKTextAlign.Left, font, _haloPaint);

          entry = new HaloEntry { Image = surf.Snapshot(), Refs = 0, Pad = pad };
          _halos[key] = entry;
        }
      }

      entry.Refs++;
      _haloKey[hit] = key;

      /*
       * Deliberately not counted in DrawsPerSec: this is a sprite being baked, and DrawHit counts the blit that uses it.
       * Counting both billed an op that drew nothing, once per distinct crit string, inflating the one number anyone reads
       * to decide whether the glow is affordable.
       */
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

      /* 3.119.2 MeasureText overloads require a paint argument but only read it for encoding, and halo baking wants one
         paint with the blur attached: both live as long as the renderer instead of being built per spawn — SKPaint is
         not a cheap object, and crits arrive in bursts. */
      _measurePaint = new SKPaint { IsAntialias = true };
      _haloPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true, MaskFilter = _glowBlur };
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

    private float TextWidth(string text, double size, bool bold) => GetFont(bold, size).MeasureText(text, _measurePaint);

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
      _demo.Clear(null); // the halos its numbers referenced were disposed with every other halo above
      ReleaseSurface();

      // the halo paint's MaskFilter is _glowBlur, so it goes first
      _haloPaint?.Dispose();
      _measurePaint?.Dispose();
      _haloPaint = _measurePaint = null;
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