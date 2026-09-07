using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * The two dials on the configure row, ±50 %. What matters here is not arithmetic but what an out-of-range value can do: a text scale of zero draws
   * nothing and a negative one puts numbers outside the canvas, and both are reachable by hand-editing settings.ini. The other thing worth pinning is
   * the direction of the second dial, because it is stored as speed and applied as a duration - a sign error there is invisible in the arithmetic and
   * obvious on screen, as "faster" that slows everything down.
   */
  [TestClass]
  public class FctScaleTest
  {
    [TestMethod]
    public void Clamp_HoldsTheRangeAndRescuesJunk()
    {
      Assert.AreEqual(1.0, FctScale.Clamp(1.0), "the default survives");
      Assert.AreEqual(0.5, FctScale.Min, "the dials are half to one and a half, or they are not plus or minus 50 %");
      Assert.AreEqual(1.5, FctScale.Max);
      // a value saved while the dial only went to 30 % is still legal here: raising the ceiling must not invalidate anybody's setting
      foreach (var saved in new[] { 0.7, 0.85, 1.15, 1.3 })
      {
        Assert.AreEqual(saved, FctScale.Clamp(saved), 0.0001, $"{saved} was rejected although it is inside the new range");
      }
      Assert.AreEqual(FctScale.Min, FctScale.Clamp(0.49), "half and one-and-a-half are the ends, so that plus or minus 50 % means what it says");
      Assert.AreEqual(FctScale.Max, FctScale.Clamp(1.51));
      Assert.AreEqual(FctScale.Min, FctScale.Clamp(0.0001), "zero text would be invisible text");
      Assert.AreEqual(FctScale.Min, FctScale.Clamp(-4));
      Assert.AreEqual(FctScale.Max, FctScale.Clamp(9000));
      Assert.AreEqual(FctScale.Default, FctScale.Clamp(double.NaN), "a hand-edited junk value lands on the default");
      Assert.AreEqual(FctScale.Default, FctScale.ClampSpeed(double.PositiveInfinity), "and infinity has no end to clamp toward");
    }

    /*
     * Speed is what the dial measures and a duration is what a number gets, so the conversion runs through a division: 50 % faster means two thirds
     * of the time on screen, and half speed means twice as long. Two things worth asserting explicitly - that the ends are those numbers, and that
     * moving the dial right never makes anything slower. Both directions are also reversible, because an older build stored the duration and reading
     * it back goes through this door.
     */
    [TestMethod]
    public void Speed_ReversesIntoDurationWithoutLosingTheSign()
    {
      Assert.AreEqual(FctScale.Default, FctScale.TimeFromSpeed(FctScale.Default), 0.0001, "one times one is one: the shipped default is still in the middle");

      Assert.AreEqual(2.0 / 3.0, FctScale.TimeFromSpeed(1.5), 0.0001, "+50 % speed should leave a number two thirds as long");
      Assert.AreEqual(2.0, FctScale.TimeFromSpeed(0.5), 0.0001, "half speed should double the time on screen");

      // monotonic in the direction the player is dragging: right is faster, and faster is shorter
      double previousDuration = double.MaxValue;

      for (var speed = FctScale.Min; speed <= FctScale.Max + 0.0001; speed += 0.05)
      {
        var duration = FctScale.TimeFromSpeed(speed);

        Assert.IsTrue(duration < previousDuration, $"{speed:0.00} speed was not shorter than the slower setting before it");
        Assert.AreEqual(speed, FctScale.SpeedFromTime(duration), 0.001, "the conversion does not come back the way it went");
        previousDuration = duration;
      }

      /* Junk from settings.ini: a zero duration is not "infinitely fast", and a negative one is not slow. Neither has a direction, so both land on
         the default rather than being honoured literally into an overlay that draws nothing. */
      Assert.AreEqual(FctScale.Default, FctScale.SpeedFromTime(0));
      Assert.AreEqual(FctScale.Default, FctScale.SpeedFromTime(-2));
      Assert.AreEqual(FctScale.Default, FctScale.SpeedFromTime(double.NaN));
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
      var previous = FctScale.Time;
      try
      {
        FctScale.Time = FctScale.Default;
        var normalLife = LifeOf(FctLane.DamageDealt, 500, false);

        FctScale.Time = FctScale.TimeFromSpeed(FctScale.Max);
        var quick = LifetimeOf(FctLane.DamageDealt, 500, false);
        Assert.IsTrue(quick < normalLife, "the speed dial did not shorten a new number");

        FctScale.Time = FctScale.TimeFromSpeed(FctScale.Min);
        var slow = LifetimeOf(FctLane.DamageDealt, 500, false);
        Assert.IsTrue(slow > normalLife, "the slow end of the dial did not make a new number last longer");

        // floors underneath: the fastest setting on the shortest kind of number is quick, not a blink
        FctScale.Time = FctScale.TimeFromSpeed(FctScale.Max);
        var tick = new FctIngest();
        var tickHits = new List<FctHitState>();
        var tickHit = tick.Accept(tickHits, FctLane.DamageTaken, 100, "Bite", false, false, true, null, 800, 560, 0);
        Assert.IsNotNull(tickHit);
        Assert.IsTrue(tickHit.LifetimeMs >= 900, $"a tick at the fastest setting fell to {tickHit.LifetimeMs} ms");
        Assert.IsTrue(tickHit.FadeMs >= 200, "a fade under 200 ms is a blink, not a fade");

        /* The promise the whole design rests on: a number already on screen keeps what it was born with, however the dial is moved afterwards. */
        FctScale.Time = FctScale.TimeFromSpeed(FctScale.Max);
        var stayed = new FctIngest();
        var stayedHits = new List<FctHitState>();
        var first = stayed.Accept(stayedHits, FctLane.DamageDealt, 400, "Flurry", false, false, false, null, 800, 560, 0);
        Assert.IsNotNull(first);
        var bornLife = first.LifetimeMs;

        FctScale.Time = FctScale.TimeFromSpeed(FctScale.Min);
        Assert.AreEqual(bornLife, first.LifetimeMs, "a number on screen changed underneath the player");
      }
      finally
      {
        FctScale.Time = previous;
      }
    }

    /* One hit through a fresh ingest, so the adaptive lifetime is not part of what a speed comparison means. */
    private static double LifeOf(FctLane lane, int value, bool periodic)
    {
      var hits = new List<FctHitState>();
      var hit = new FctIngest().Accept(hits, lane, value, "Flurry", false, false, periodic, null, 800, 560, 0);

      return hit?.LifetimeMs ?? double.NaN;
    }

    private static double LifetimeOf(FctLane lane, int value, bool periodic) => LifeOf(lane, value, periodic);

    [TestMethod]
    public void Percent_RoundTripsThroughTheSlider()
    {
      Assert.AreEqual(0, FctScale.Percent(1.0));
      Assert.AreEqual(-50, FctScale.Percent(0.5));
      Assert.AreEqual(50, FctScale.Percent(1.5));
      Assert.AreEqual(10, FctScale.Percent(FctScale.FromPercent(10)));

      // the slider snaps to 5 % steps: every step must survive the round trip exactly, or the readout lies about where you are
      for (var percent = -50; percent <= 50; percent += 5)
      {
        Assert.AreEqual(percent, FctScale.Percent(FctScale.FromPercent(percent)), $"{percent}% round trip");
      }
    }
  }
}
