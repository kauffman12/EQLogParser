using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * The two dials on the configure row. What matters here is not arithmetic but what an out-of-range value can do: a text scale of
   * zero draws nothing and a negative one puts numbers outside the canvas, and both are reachable by hand-editing settings.ini.
   * Everything else — that sizes actually move, that floors still hold — lives in FctStyleTest/FctLayoutTest with the constants.
   */
  [TestClass]
  public class FctScaleTest
  {
    [TestMethod]
    public void Clamp_HoldsTheRangeAndRescuesJunk()
    {
      Assert.AreEqual(1.0, FctScale.Clamp(1.0), "the default survives");
      Assert.AreEqual(FctScale.Min, FctScale.Clamp(0.7));
      Assert.AreEqual(FctScale.Max, FctScale.Clamp(1.3));
      Assert.AreEqual(FctScale.Min, FctScale.Clamp(0.0001), "zero text would be invisible text");
      Assert.AreEqual(FctScale.Min, FctScale.Clamp(-4));
      Assert.AreEqual(FctScale.Max, FctScale.Clamp(9000));
      Assert.AreEqual(FctScale.Default, FctScale.Clamp(double.NaN), "a hand-edited junk value lands on the default");
    }

    /*
     * Tempo is applied where a hit is built, which is the whole promise: shortening the overlay must never tug at text already in
     * flight. The second half is the floor — a tempo scale that could drive a lifetime below a second would make numbers flicker,
     * and the compounding of the life controller on top makes that easier to reach than it sounds.
     */
    [TestMethod]
    public void TimeScale_ChangesNewNumbersOnlyAndNeverBelowAFlicker()
    {
      var previous = FctScale.Time;
      try
      {
        var ingest = new FctIngest();
        var hits = new List<FctHitState>();

        FctScale.Time = FctScale.Default;
        var normal = ingest.Accept(hits, FctLane.DamageDealt, 500, "Flurry", false, false, false, null, 800, 560, 0);
        Assert.IsNotNull(normal);
        var normalLife = normal.LifetimeMs;

        FctScale.Time = FctScale.Min;
        var quick = ingest.Accept(hits, FctLane.DamageDealt, 600, "Flurry", false, false, false, null, 800, 560, 10);
        Assert.IsNotNull(quick);

        Assert.IsTrue(quick.LifetimeMs < normalLife, "the time dial did not shorten a new number");
        Assert.AreEqual(normalLife, normal.LifetimeMs, "a number already on screen changed underneath the player");

        // the smallest scale applied to the shortest kind of number: floors hold, so it is quick rather than flickering
        var tick = ingest.Accept(hits, FctLane.DamageTaken, 100, "Crotbite", false, false, true, null, 800, 560, 20);
        Assert.IsNotNull(tick);
        Assert.IsTrue(tick.LifetimeMs >= 900, $"a tick at the slowest-setting-shortest-number fell to {tick.LifetimeMs} ms");
        Assert.IsTrue(tick.FadeMs >= 200, "a fade under 200 ms is a blink, not a fade");
      }
      finally
      {
        FctScale.Time = previous;
      }
    }

    [TestMethod]
    public void Percent_RoundTripsThroughTheSlider()
    {
      Assert.AreEqual(0, FctScale.Percent(1.0));
      Assert.AreEqual(-30, FctScale.Percent(0.7));
      Assert.AreEqual(30, FctScale.Percent(1.3));
      Assert.AreEqual(10, FctScale.Percent(FctScale.FromPercent(10)));

      // the slider snaps to 5 % steps: every step must survive the round trip exactly, or the readout lies about where you are
      for (var percent = -30; percent <= 30; percent += 5)
      {
        Assert.AreEqual(percent, FctScale.Percent(FctScale.FromPercent(percent)), $"{percent}% round trip");
      }
    }
  }
}
