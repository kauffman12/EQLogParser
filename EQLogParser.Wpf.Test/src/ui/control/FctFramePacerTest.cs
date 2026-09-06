using System;

namespace EQLogParser
{
  /*
   * Pacing is where "smooth" is won or lost on hardware nobody here owns. The rule this replaces — paint when 16.7 ms
   * have elapsed since the last paint — beats against the very refresh rate it was tuned for, so these feed synthetic
   * tick streams (a jittered 60 Hz included) and assert whole-tick cadence rather than an elapsed-time threshold.
   */
  [TestClass]
  public sealed class FctFramePacerTest
  {
    /* Drives the pacer at a nominal refresh, optionally wobbling each interval, and counts how many ticks rasters. */
    private static int PaintAt(double refreshMs, int ticks, double jitterMs = 0, FctFramePacer? pacer = null)
    {
      var pace = pacer ?? new FctFramePacer();
      var now = 0.0;
      var paints = 0;

      for (var i = 0; i < ticks; i++)
      {
        // ±jitter alternating, which is what a real frame does around its nominal interval
        now += refreshMs + ((i % 2 == 0) ? jitterMs : -jitterMs);

        if (pace.Tick(now))
        {
          paints++;
          pace.Painted();
        }
      }

      return paints;
    }

    /* The most common display in existence, and the one a time-threshold cap handles worst: every refresh must paint. */
    [TestMethod]
    public void SixtyHzPaintsEveryTick()
    {
      var pacer = new FctFramePacer();

      Assert.AreEqual(601, PaintAt(1000.0 / 60, 601, pacer: pacer), "a 60 Hz display must never have a tick skipped by a 60 Hz cap");
      Assert.AreEqual(60, pacer.DisplayHz, 1.0, $"measured refresh was {pacer.DisplayHz:0.#} Hz");
    }

    /*
     * Same at 60 Hz with the ordinary half-millisecond wobble real frames carry — and here is the regression this
     * class exists for: the elapsed-time rule it replaced skips ticks on exactly that wobble, so paints alternate one
     * and two refresh intervals. That alternation is the judder, so it is asserted to happen under the old rule to
     * stop anyone reintroducing it as a simplification.
     */
    [TestMethod]
    public void JitterDoesNotMakeSixtyHzSkipFrames()
    {
      const double RefreshMs = 1000.0 / 60;
      const int Ticks = 600;

      Assert.AreEqual(Ticks, PaintAt(RefreshMs, Ticks, jitterMs: 0.5), "jittered 60 Hz still paints every refresh");

      // the old rule, reproduced verbatim: paint only once TargetMs have passed since the last paint
      var oldRulePaints = 0;
      var lastPaint = -1000.0;
      var now = 0.0;
      for (var i = 0; i < Ticks; i++)
      {
        now += RefreshMs + ((i % 2 == 0) ? 0.5 : -0.5);
        if (now - lastPaint >= FctFramePacer.TargetMs)
        {
          oldRulePaints++;
          lastPaint = now;
        }
      }

      Assert.IsTrue(oldRulePaints < Ticks, "this test's premise: the time-threshold rule judders at 60 Hz");
    }

    [TestMethod]
    public void HigherRefreshRatesPaintWholeMultiplesOfTicks()
    {
      // 120 Hz: every other tick, which is exactly the 60 Hz cap with no beat pattern
      Assert.AreEqual(300, PaintAt(1000.0 / 120, 600));

      // 144 Hz: two ticks (72 fps) rather than three (48 fps), because 2 is the nearer whole number of the ratio
      var pacer = new FctFramePacer();
      Assert.AreEqual(300, PaintAt(1000.0 / 144, 600, pacer: pacer));
      Assert.AreEqual(72, pacer.PaintHz, 2.0, $"144 Hz should pace at about 72 fps, was {pacer.PaintHz:0.#}");

      // 240 Hz: four ticks
      Assert.AreEqual(150, PaintAt(1000.0 / 240, 600));
    }

    /* A tab-out, a GC pause or a stalled thread must not retune the pacer onto the hitch and then hold it there. */
    [TestMethod]
    public void HitchesAreNotMistakenForRefreshInterval()
    {
      var pacer = new FctFramePacer();
      var now = 0.0;

      for (var i = 0; i < 120; i++)
      {
        now += 1000.0 / 60;
        pacer.Tick(now);
        pacer.Painted();
      }

      Assert.AreEqual(60, pacer.DisplayHz, 1.0, $"steady-state estimate was {pacer.DisplayHz:0.#} Hz");

      now += 3000; // window hidden, process suspended, whatever the cause
      Assert.IsTrue(pacer.Tick(now), "the frame after a stall always paints");
      pacer.Painted();

      Assert.AreEqual(60, pacer.DisplayHz, 1.0, "a 3 s gap must not enter the refresh estimate");
      Assert.AreEqual(120, PaintAt(1000.0 / 60, 120, pacer: pacer), "and pacing continues at every tick afterwards");
    }

    /* First frame after Start(): no estimate exists yet, so nothing may be withheld from the player. */
    [TestMethod]
    public void FirstTickAlwaysPaints()
    {
      var pacer = new FctFramePacer();

      Assert.IsTrue(pacer.Tick(10_000));
      Assert.AreEqual(0.0, pacer.DisplayHz, "a single tick is not an interval");

      pacer.Painted();
      pacer.Reset();
      Assert.IsTrue(pacer.Tick(20_000), "Reset must restore the paint-on-first-tick rule");
    }
  }
}
