using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * The two dials on the configure row. What matters here is not arithmetic but three decisions that are invisible if somebody changes them: a text
   * scale of zero draws nothing and a negative one puts numbers outside the canvas (both reachable by hand-editing settings.ini); the speed dial has to
   * reach further toward fast than toward slow, because the slow end at half tempo is unusable in a fight; and the middle of that dial is a shipped
   * tempo of 1.15 rather than 1.0, which is a preference about how the feature feels and wants pinning as loudly as any constant.
   */
  [TestClass]
  public class FctScaleTest
  {
    [TestMethod]
    public void ClampSize_HoldsTheRangeAndRescuesJunk()
    {
      Assert.AreEqual(1.0, FctScale.ClampSize(1.0), "the default survives");

      /* Half to one and a half, or it is not plus or minus 50 %. Measured through the dial's own conversion rather than against the constants, so this
         says something about the pair of them rather than restating a definition. */
      var bottom = FctScale.SizeFromPercent(-50);
      var top = FctScale.SizeFromPercent(50);
      Assert.AreEqual(FctScale.SizeMin, bottom, 0.0001);
      Assert.AreEqual(FctScale.SizeMax, top, 0.0001);

      // a value saved while the dial only went to 30 % is still legal here: raising a ceiling must not invalidate anybody's setting
      foreach (var saved in new[] { 0.7, 0.85, 1.15, 1.3 })
      {
        Assert.AreEqual(saved, FctScale.ClampSize(saved), 0.0001, $"{saved} was rejected although it is inside the range");
      }

      Assert.AreEqual(FctScale.SizeMin, FctScale.ClampSize(0.49));
      Assert.AreEqual(FctScale.SizeMax, FctScale.ClampSize(1.51));
      Assert.AreEqual(FctScale.SizeMin, FctScale.ClampSize(0.0001), "zero text would be invisible text");
      Assert.AreEqual(FctScale.SizeMin, FctScale.ClampSize(-4));
      Assert.AreEqual(FctScale.SizeMax, FctScale.ClampSize(9000));
      Assert.AreEqual(FctScale.SizeDefault, FctScale.ClampSize(double.NaN), "a hand-edited junk value lands on the default");
    }

    /*
     * The shape of the speed dial, which is the thing a player noticed: half speed (twice as long on screen) is unusable while playing, and half again
     * as fast was not enough. So the ends are -30 % and +90 % of the shipped tempo - it stops only a little past the pace that used to be neutral, and
     * reaches roughly twice as fast as that pace. Both ends are asserted in duration as well as speed, because duration is what a number actually gets:
     * 1.24x at the slow end and 0.46x at the fast one.
     */
    [TestMethod]
    public void SpeedRange_ReachesFurtherUpThanDown()
    {
      // the middle of the track, in both units: 15 % quicker than the tempo everything was choreographed at, and the durations that implies
      var shippedSpeed = FctScale.SpeedFromPercent(FctScale.SpeedPercentDefault);
      var shippedTime = FctScale.TimeFromSpeed(shippedSpeed);
      Assert.AreEqual(1.15, shippedSpeed, 0.0001, "the middle of the dial is the measured tempo plus 15 %, not 1.0");
      Assert.AreEqual(0.87, shippedTime, 0.01, $"and a number at that setting lives {shippedTime:0.00} times as long as the baseline");

      var slowest = FctScale.SpeedFromPercent(FctScale.SpeedPercentMin);
      var fastest = FctScale.SpeedFromPercent(FctScale.SpeedPercentMax);
      Assert.AreEqual(0.805, slowest, 0.0001, "-30 % of that tempo is the slow end");
      Assert.AreEqual(2.185, fastest, 0.0001, "+90 % is the fast end");

      var slowTime = FctScale.TimeFromSpeed(slowest);
      var fastTime = FctScale.TimeFromSpeed(fastest);

      Assert.IsTrue(slowTime < 1.3, $"the slow end should not be a slideshow (was {slowTime:0.00}x the measured time)");
      Assert.IsTrue(fastTime < 0.5, $"the fast end should be well past the old two-thirds limit (was {fastTime:0.00}x)");

      // and the extra reach is on the side people asked for
      Assert.IsTrue(fastest - shippedSpeed > shippedSpeed - slowest, "the dial runs further toward fast than toward slow");
    }

    /*
     * Speed is what the dial measures and a duration is what a number gets, so the conversion runs through a division: 100 % faster would be half the
     * time on screen. Two things worth asserting - that moving right never makes anything slower, and that every snapped position on the track comes
     * back as the percent it showed. Both directions are also how an older build's stored duration is read back.
     */
    [TestMethod]
    public void Speed_ReversesIntoDurationWithoutLosingTheSign()
    {
      Assert.AreEqual(1 / 1.15, FctScale.TimeFromSpeed(FctScale.SpeedDefault), 0.0001, "the shipped position converts to a duration, not to nothing");

      var previousDuration = double.MaxValue;

      for (var percent = FctScale.SpeedPercentMin; percent <= FctScale.SpeedPercentMax; percent += 5)
      {
        var speed = FctScale.SpeedFromPercent(percent);
        var duration = FctScale.TimeFromSpeed(speed);

        Assert.IsTrue(duration < previousDuration, $"{percent:+0;-0;0}% was not shorter than the slower setting before it");
        Assert.AreEqual(percent, FctScale.PercentOfSpeed(speed), "the readout does not say where the track is");
        Assert.AreEqual(speed, FctScale.SpeedFromTime(duration), 0.001, "the conversion does not come back the way it went");

        previousDuration = duration;
      }

      /* Junk from settings.ini: a zero duration is not "infinitely fast", and a negative one is not slow. Neither has a direction, so both land on
         the default rather than being honoured literally into an overlay that draws nothing. */
      Assert.AreEqual(FctScale.SpeedDefault, FctScale.SpeedFromTime(0));
      Assert.AreEqual(FctScale.SpeedDefault, FctScale.SpeedFromTime(-2));
      Assert.AreEqual(FctScale.SpeedDefault, FctScale.SpeedFromTime(double.NaN));
      Assert.AreEqual(FctScale.SpeedDefault, FctScale.ClampSpeed(double.PositiveInfinity), "and infinity has no end to clamp toward");
    }

    /*
     * Speed is applied where a hit is built, which is the whole promise: shortening the overlay must never tug at text already in flight. Each
     * measurement gets its own ingest because the adaptive lifetime responds to arrival rate - a second number through the same ingest is shorter
     * because the load estimator says so, which would be measuring the wrong thing here (FctIngestTest covers that behaviour). The last check is the
     * floor: at the fastest setting, on the shortest kind of number, a tick still has to be readable rather than a flicker.
     */
    [TestMethod]
    public void Speed_ChangesNewNumbersOnlyAndNeverBelowAFlicker()
    {
      var previousTime = FctScale.Time;
      try
      {
        FctScale.Time = 1;
        var baseLife = LifetimeOf(FctLane.DamageDealt, 500, false);

        FctScale.Time = FctScale.TimeFromSpeed(FctScale.SpeedDefault);
        Assert.IsTrue(LifetimeOf(FctLane.DamageDealt, 500, false) < baseLife, "the shipped speed did not shorten a new number against the measured baseline");

        FctScale.Time = FctScale.TimeFromSpeed(FctScale.SpeedMax);
        var quick = LifetimeOf(FctLane.DamageDealt, 500, false);
        Assert.IsTrue(quick < baseLife * 0.6, $"the fast end barely reached (was {quick:0} against {baseLife:0})");

        FctScale.Time = FctScale.TimeFromSpeed(FctScale.SpeedMin);
        var slow = LifetimeOf(FctLane.DamageDealt, 500, false);
        Assert.IsTrue(slow > baseLife, "the slow end of the dial did not make a new number last longer");

        // floors underneath: the fastest setting on the shortest kind of number is quick, not a blink
        FctScale.Time = FctScale.TimeFromSpeed(FctScale.SpeedMax);
        var tickHits = new List<FctHitState>();
        var tick = new FctIngest().Accept(tickHits, FctLane.DamageTaken, 100, "Bite", false, false, true, null, 800, 560, 0);
        Assert.IsNotNull(tick);
        Assert.IsTrue(tick.LifetimeMs >= 900, $"a tick at the fastest setting fell to {tick.LifetimeMs:0} ms");
        Assert.IsTrue(tick.FadeMs >= 200, "a fade under 200 ms is a blink, not a fade");

        /* The promise the whole design rests on: a number already on screen keeps what it was born with, however the dial is moved afterwards. */
        var heldHits = new List<FctHitState>();
        var first = new FctIngest().Accept(heldHits, FctLane.DamageDealt, 400, "Flurry", false, false, false, null, 800, 560, 0);
        Assert.IsNotNull(first);
        var bornLife = first.LifetimeMs;

        FctScale.Time = FctScale.TimeFromSpeed(FctScale.SpeedMin);
        Assert.AreEqual(bornLife, first.LifetimeMs, "a number on screen changed underneath the player");
      }
      finally
      {
        FctScale.Time = previousTime;
      }
    }

    /* One hit through a fresh ingest, so the adaptive lifetime is not part of what a speed comparison means. */
    private static double LifetimeOf(FctLane lane, int value, bool periodic)
    {
      var hits = new List<FctHitState>();
      var hit = new FctIngest().Accept(hits, lane, value, "Flurry", false, false, periodic, null, 800, 560, 0);

      return hit?.LifetimeMs ?? double.NaN;
    }

    [TestMethod]
    public void PercentOfSize_RoundTripsThroughTheSlider()
    {
      Assert.AreEqual(0, FctScale.PercentOfSize(1.0));
      Assert.AreEqual(-50, FctScale.PercentOfSize(0.5));
      Assert.AreEqual(50, FctScale.PercentOfSize(1.5));
      Assert.AreEqual(10, FctScale.PercentOfSize(FctScale.SizeFromPercent(10)));

      // the slider snaps to 5 % steps: every step must survive the round trip exactly, or the readout lies about where you are
      for (var percent = -50; percent <= 50; percent += 5)
      {
        Assert.AreEqual(percent, FctScale.PercentOfSize(FctScale.SizeFromPercent(percent)), $"{percent}% round trip");
      }
    }
  }
}
