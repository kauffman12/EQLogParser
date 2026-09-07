using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * The two dials on the configure row. What matters here is not arithmetic but the three decisions that are invisible if somebody changes them: a text
   * scale of zero draws nothing and a negative one puts numbers outside the canvas (both reachable by hand-editing settings.ini); the speed dial is
   * measured in percent of the time a number spends on screen, so +50 % has to mean half as long and not two thirds; and its middle is 0.877 of the
   * measured time rather than 1.0, which is a preference about how the feature feels and wants pinning as loudly as any constant.
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
     * The shape of the speed dial. It is centred — plus or minus 50 % of the time a number is on screen, with the middle meaning nothing — and where
     * that middle sits is a decision: it is the midpoint of the band that playing with the previous asymmetric dial produced, about 0.877 of the time
     * everything was choreographed at. Half the time on screen at one end (0.44x), half again as long at the other (1.32x), and neither end in the
     * twice-as-long territory nobody can play under.
     */
    [TestMethod]
    public void SpeedDial_IsCentredAndMeasuredInTimeOnScreen()
    {
      var middle = FctScale.TimeFromPercent(FctScale.SpeedPercentDefault);
      Assert.AreEqual(0.877, middle, 0.0001, "the middle is the midpoint of the band people actually used, not 1.0");

      /* The ends are ±50 of the same unit — symmetry is what makes the middle parkable by feel, and the loop in
         Speed_ReversesIntoDurationWithoutLosingTheSign walks every snapped position between them. */

      var fastest = FctScale.TimeFromPercent(FctScale.SpeedPercentMax);
      var slowest = FctScale.TimeFromPercent(FctScale.SpeedPercentMin);

      Assert.AreEqual(middle * 0.5, fastest, 0.0001, "+50 % of speed is half the time on screen, not two thirds of it");
      Assert.AreEqual(middle * 1.5, slowest, 0.0001, "and -50 % is half again as long");
      Assert.IsTrue(slowest < 1.35, $"the slow end must stay playable (was {slowest:0.00}x the measured time)");
      Assert.IsTrue(fastest > 0.4, $"the fast end must stay readable (was {fastest:0.00}x)");

      // the same positions as a rate, which is what settings.ini holds and what an older build stored: about twice the measured tempo one way and
      // three quarters of it the other, so the reach is where playing with the feature put it rather than where a symmetric rate dial would have
      var fastRate = FctScale.SpeedFromPercent(FctScale.SpeedPercentMax);
      var slowRate = FctScale.SpeedFromPercent(FctScale.SpeedPercentMin);
      Assert.AreEqual(2.28, fastRate, 0.01, $"the fast end reaches past where the old dial stopped (was {fastRate:0.00}x tempo)");
      Assert.AreEqual(0.76, slowRate, 0.01, $"and the slow end stops well short of twice as long up (was {slowRate:0.00}x tempo)");
    }

    /*
     * Speed is what the dial shows and a duration is what a number gets, so every position has to survive the trip out and back: monotonic in the
     * direction the player drags (right never slower), and the readout saying exactly where the thumb is. Both directions are also how a stored rate
     * from an older build is read back, so junk there lands on the default rather than being honoured literally.
     */
    [TestMethod]
    public void Speed_ReversesIntoDurationWithoutLosingTheSign()
    {
      var previousDuration = double.MaxValue;

      for (var percent = FctScale.SpeedPercentMin; percent <= FctScale.SpeedPercentMax; percent += 5)
      {
        var time = FctScale.TimeFromPercent(percent);
        var speed = FctScale.SpeedFromPercent(percent);

        Assert.IsTrue(time < previousDuration, $"{percent:+0;-0;0}% was not shorter than the slower setting before it");
        Assert.AreEqual(percent, FctScale.PercentOfTime(time), "the readout does not say where the track is");
        Assert.AreEqual(percent, FctScale.PercentOfSpeed(speed), "the stored rate does not come back as the position it came from");
        Assert.AreEqual(speed, FctScale.SpeedFromTime(time), 0.001, "rate and duration do not agree with each other");

        previousDuration = time;
      }

      /* Junk from settings.ini: a zero duration is not "infinitely fast", and a negative one is not slow. */
      Assert.AreEqual(FctScale.SpeedDefault, FctScale.SpeedFromTime(0));
      Assert.AreEqual(FctScale.SpeedDefault, FctScale.SpeedFromTime(-2));
      Assert.AreEqual(FctScale.SpeedDefault, FctScale.SpeedFromTime(double.NaN));
      Assert.AreEqual(FctScale.SpeedDefault, FctScale.ClampSpeed(double.PositiveInfinity), "and infinity has no end to clamp toward");
      // a hand-edited number is clamped toward the end it was reaching for; only a value with no direction at all falls back to the middle
      Assert.AreEqual(FctScale.TimeMin, FctScale.ClampTime(0.0001), "a duration of almost nothing means the fastest setting");
      Assert.AreEqual(FctScale.TimeMax, FctScale.ClampTime(40));
      Assert.AreEqual(FctScale.TimeDefault, FctScale.ClampTime(double.NaN));
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

        FctScale.Time = FctScale.TimeFromPercent(FctScale.SpeedPercentDefault);
        Assert.IsTrue(LifetimeOf(FctLane.DamageDealt, 500, false) < baseLife, "the middle of the dial did not shorten a new number against the measured baseline");

        FctScale.Time = FctScale.TimeFromPercent(FctScale.SpeedPercentMax);
        var quick = LifetimeOf(FctLane.DamageDealt, 500, false);
        Assert.IsTrue(quick < baseLife * 0.6, $"the fast end barely reached (was {quick:0} against {baseLife:0})");

        FctScale.Time = FctScale.TimeFromPercent(FctScale.SpeedPercentMin);
        var slow = LifetimeOf(FctLane.DamageDealt, 500, false);
        Assert.IsTrue(slow > baseLife, "the slow end of the dial did not make a new number last longer");

        // floors underneath: the fastest setting on the shortest kind of number is quick, not a blink
        FctScale.Time = FctScale.TimeFromPercent(FctScale.SpeedPercentMax);
        var tickHits = new List<FctHitState>();
        var tick = new FctIngest { Layout = FctLayoutChoice.Bands }.Accept(tickHits, FctLane.DamageTaken, 100, "Bite", false, false, true, null, 800, 560, 0);
        Assert.IsNotNull(tick);
        Assert.IsTrue(tick.LifetimeMs >= 900, $"a tick at the fastest setting fell to {tick.LifetimeMs:0} ms");
        Assert.IsTrue(tick.FadeMs >= 200, "a fade under 200 ms is a blink, not a fade");

        /* The promise the whole design rests on: a number already on screen keeps what it was born with, however the dial is moved afterwards. */
        var heldHits = new List<FctHitState>();
        var first = new FctIngest { Layout = FctLayoutChoice.Bands }.Accept(heldHits, FctLane.DamageDealt, 400, "Flurry", false, false, false, null, 800, 560, 0);
        Assert.IsNotNull(first);
        var bornLife = first.LifetimeMs;

        FctScale.Time = FctScale.TimeFromPercent(FctScale.SpeedPercentMin);
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
      var hit = new FctIngest { Layout = FctLayoutChoice.Bands }.Accept(hits, lane, value, "Flurry", false, false, periodic, null, 800, 560, 0);

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
