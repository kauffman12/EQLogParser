using System;

namespace EQLogParser
{
  /*
   * Frame pacing for the CompositionTarget.Rendering handler in FctSkiaCanvas: the one place that decides which render ticks get rastered,
   * kept out of the canvas so the rule is testable against synthetic tick streams
   * (EQLogParser.Wpf.Test/src/ui/control/FctFramePacerTest.cs). Rationale in docs/DesignNotes.md → Floating Combat Text.
   */
  internal sealed class FctFramePacer
  {
    /*
     * Ceiling on raster work, not on the animation: a hit's geometry is an analytic function of the canvas clock, so
     * skipping paints never changes where text ends up, only how often that position gets sampled onto pixels. Raster
     * costs surface area (memset + glyph passes + snapshot/blit), which at 1440p and 144 Hz is gigabytes a second.
     */
    public const double TargetMs = 1000.0 / 60;

    /* A tick gap outside this range is not a refresh interval: first sample, tab-out, hitch, or a stalled thread. */
    private const double MinSampleMs = 0.5;
    private const double MaxSampleMs = 200;

    private double _lastTickMs = -1;
    private double _refreshMs;
    private int _ticksSincePaint;

    /* Measured display refresh, from the ticks themselves — WPF gives no other honest answer to that question. */
    public double RefreshMs => _refreshMs;
    public double DisplayHz => _refreshMs > 0 ? 1000.0 / _refreshMs : 0;

    /* Paints per second this pacer would allow on the currently measured display. */
    public double PaintHz => _refreshMs > 0 ? DisplayHz / SkipEveryTicks() : 0;

    /*
     * Call on every render tick, before any early-out, with the canvas clock. Returns true when this tick should
     * raster; the caller answers by calling Painted(). Measuring on every tick is what keeps the refresh estimate
     * honest while the canvas sits idle, so a hit arriving after a lull is paced correctly on its first frame.
     */
    public bool Tick(double nowMs)
    {
      if (_lastTickMs < 0)
      {
        _lastTickMs = nowMs;
        return true; // nothing to compare against yet; the first frame is never skipped
      }

      var delta = nowMs - _lastTickMs;
      _lastTickMs = nowMs;

      if (delta is > MinSampleMs and < MaxSampleMs)
      {
        // light smoothing: one slow frame out of a 60 Hz stream is a GC pause, not a monitor change
        _refreshMs = _refreshMs <= 0 ? delta : (_refreshMs * 0.9) + (delta * 0.1);
      }

      _ticksSincePaint++;
      return _ticksSincePaint >= SkipEveryTicks();
    }

    /* Bookkeeping for a tick that actually rasters; skipping it would drift the cadence. */
    public void Painted() => _ticksSincePaint = 0;

    public void Reset()
    {
      _lastTickMs = -1;
      _refreshMs = 0;
      _ticksSincePaint = 0;
    }

    /*
     * Tick counts rather than an elapsed-time threshold, and that is the whole reason this class exists. The previous
     * rule — "paint if at least 16.67 ms since the last paint" — beats against the refresh rate it was tuned for:
     * ordinary tick jitter makes some frames land a microsecond early and get skipped, so paints alternate one and two
     * refresh intervals and animated text judders at exactly 60 Hz, which is most of the machines in question.
     * Choosing a whole number of ticks per paint is exact instead: 60 Hz paints every tick, 120 Hz every second (60),
     * 144 Hz every second (72), 240 Hz every fourth — never faster than the display, never a beat pattern, and it
     * re-derives itself when the window lands on a different monitor mid-fight.
     */
    private int SkipEveryTicks() => _refreshMs > 0 ? Math.Max(1, (int)Math.Round(TargetMs / _refreshMs)) : 1;
  }
}