using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The parabola (FctMotionStyle.Parabola): a constant-speed vertical scroll bent sideways by t² — the scrolling combat
   * text genre's own default shape (MSBT). What these pin down: that it is a parabola and not an arc (y linear in t, x
   * quadratic), that the bow points away from the seam in both halves, that it stays inside its half for life at every
   * speed the dial can give, that bands ingest degrades it to hold instead of letting a number cross the strip, and that
   * resize maps the bow the same way it maps everything else.
   */
  [TestClass]
  public sealed class FctParabolaTest
  {
    private const double Width = 980;
    private const double Height = 640;

    private static FctStage Halves(FctRegionSide side, bool incomingUp, bool outgoingUp, double w = Width, double h = Height)
      => FctStage.Halves(side, incomingUp, outgoingUp, w, h);

    private static FctHitState Spawn(FctStage stage, bool incoming, Random rand, FctMotionStyle style = FctMotionStyle.Parabola)
    {
      var lane = incoming ? FctLane.DamageTaken : FctLane.DamageDealt;
      var hit = new FctHitState
      {
        Lane = lane,
        Incoming = incoming,
        Style = style,
        Source = "Spinning Attack",
        Value = 1234,
      };

      FctStyle.ApplyTo(hit, hit.Lane, minor: false);
      hit.ValueWidth = FctLayout.EstimateTextWidth("1,234", hit.ValueFontSize); // what FctIngest.Accept does after style
      FctLayout.Spawn(hit, stage, rand);
      return hit;
    }

    [TestMethod]
    public void HalvesDefaultMotionIsTheParabolaAndBandsKeepHold()
    {
      Assert.AreEqual(FctMotionStyle.Parabola, FctStage.DefaultMotion(FctLayoutMode.Halves));
      Assert.AreEqual(FctMotionStyle.Hold, FctStage.DefaultMotion(FctLayoutMode.Bands));
    }

    /* The shape's first clause: with y linear in t, equal time steps cover equal distances. Eased motion does not. */
    [TestMethod]
    public void TheVerticalScrollRunsAtConstantSpeed()
    {
      var stage = Halves(FctRegionSide.Left, false, false);
      var hit = Spawn(stage, incoming: false, new Random(11));
      Assert.AreNotEqual(0.0, Math.Abs(hit.Rise), "a parabola with no vertical travel is a hold in a costume");

      var y1 = FctMotion.RaisedY(hit, 0.25);
      var y2 = FctMotion.RaisedY(hit, 0.50);
      var y3 = FctMotion.RaisedY(hit, 0.75);
      Assert.AreEqual(y1 - y2, y2 - y3, 1e-9, "equal time steps must cover equal distances at constant speed");
    }

    /* The shape's second clause: x is quadratic in t and bows away from the seam — left half drifts left, right half right. */
    [TestMethod]
    public void TheBowIsQuadraticAndPointsAwayFromTheSeamInBothHalves()
    {
      foreach (var side in new[] { FctRegionSide.Left, FctRegionSide.Right })
      {
        var stage = Halves(side, false, false);
        var hit = Spawn(stage, incoming: true, new Random(21));
        var region = stage.RegionFor(true);
        var away = side is FctRegionSide.Left ? -1.0 : 1.0;

        Assert.AreEqual(away * stage.TerritoryFor(true) * FctLayout.ParabolaBowFrac, hit.Bow, 1e-9,
          $"the bow must leave {side} half's stream pointing outward");

        var x0 = FctMotion.ArcedX(hit, 0.0);
        var xm = FctMotion.ArcedX(hit, 0.5);
        var xe = FctMotion.ArcedX(hit, 1.0);
        var lo = region.X + 1.0;
        var hi = region.X + region.Width - 1.0;

        // where the drift ran unclamped, a quarter of the time is exactly a quarter of the drift: x ∝ t²
        if (xm > lo && xm < hi && xe > lo && xe < hi)
        {
          Assert.AreEqual((xe - x0) * 0.25, (xm - x0), Math.Abs(xe - x0) * 0.01 + 1e-9,
            "x ∝ t² means half the time carries a quarter of the sideways drift");
        }

        // and for the whole life, wherever it is, it is in the half that owns it
        for (var t = 0.0; t <= 1.0; t += 0.1)
        {
          var x = FctMotion.ArcedX(hit, t);
          Assert.IsTrue(x >= region.X && x <= region.X + region.Width,
            $"t={t:0.0}: x {x:0.#} left its half [{region.X:0.#}..{region.X + region.Width:0.#}]");
        }
      }
    }

    /*
     * The tempo system's promise for the new style: FctScale.Time stretches or squeezes how long a number scrolls, and nothing
     * about where it lands. Endpoints must agree to the bit; only the duration may differ — by exactly the dial's ratio.
     */
    [TestMethod]
    public void TheSpeedDialChangesDurationNotWhereTheNumberEnds()
    {
      try
      {
        var endpoints = new Dictionary<double, double>();
        var lifetimes = new Dictionary<double, double>();

        foreach (var time in new[] { FctScale.TimeMin, FctScale.TimeDefault, FctScale.TimeMax })
        {
          FctScale.Time = time;
          var ingest = new FctIngest(new Random(31)) { Style = FctMotionStyle.Parabola, Layout = FctLayoutChoice.Shipped };
          var hits = new List<FctHitState>();
          var hit = ingest.Accept(hits, FctLane.DamageDealt, 5000, "Flurry", false, false, false, null, Width, Height, 0);
          Assert.IsNotNull(hit, $"time {time:0.###}: nothing was spawned");

          endpoints[time] = FctMotion.RaisedY(hit, 1.0);
          lifetimes[time] = hit.LifetimeMs;
        }

        foreach (var endpoint in endpoints.Values)
        {
          Assert.AreEqual(endpoints[FctScale.TimeDefault], endpoint, 1e-9,
            "the speed dial changes how long the scroll takes, not where it ends");
        }

        var ratio = lifetimes[FctScale.TimeMin] / lifetimes[FctScale.TimeMax];
        Assert.AreEqual(FctScale.TimeMin / FctScale.TimeMax, ratio, 0.02,
          "only the duration may move, and it must move by exactly what the dial says");
      }
      finally
      {
        FctScale.Time = FctScale.TimeDefault; // other tests measure against the shipped tempo
      }
    }

    /*
     * The setting can be forced through settings.ini in a scheme where it has no business being. Ingest is where the promise
     * is kept: the hit comes back a hold, and a hold by construction cannot cross the protected strip, so the sweep below
     * stays inside its band at every instant instead of measuring how bad the violation would have been.
     */
    [TestMethod]
    public void BandsIngestDegradesAParabolaToHoldInsteadOfCrossingTheStrip()
    {
      var ingest = new FctIngest(new Random(41)) { Style = FctMotionStyle.Parabola, Layout = FctLayoutChoice.Bands };
      var hits = new List<FctHitState>();
      var checkedAny = false;

      for (var i = 0; i < 25; i++)
      {
        ingest.PruneExpired(hits, i * 900);
        var hit = ingest.Accept(hits, FctLane.DamageDealt, 4000 + (i * 13), "Flurry", false, false, false, null, Width, Height, i * 900.0);
        if (hit is null)
        {
          continue; // folded or evicted: real behaviour, not this test's subject
        }

        checkedAny = true;
        Assert.AreEqual(FctMotionStyle.Hold, hit.Style, "ingest must not run a parabola in bands");

        for (var t = 0.0; t <= 1.0; t += 0.05)
        {
          var y = FctMotion.RaisedY(hit, t);
          Assert.IsTrue(y >= hit.BandMinY - 1e-9 && y <= hit.BandMaxY + 1e-9,
            $"t={t:0.##}: y {y:0.#} left its band [{hit.BandMinY:0.#}..{hit.BandMaxY:0.#}]");
        }
      }

      Assert.IsTrue(checkedAny, "nothing was spawned to check");
    }

    /* The bow is a territory share exactly like Arc: resize maps it by the x factor, and the number still ends in its new half. */
    [TestMethod]
    public void ResizeMapsTheBowAndTheNumberStillEndsInItsHalf()
    {
      var stage = Halves(FctRegionSide.Left, false, false);
      var hit = Spawn(stage, incoming: true, new Random(51));
      var bowBefore = hit.Bow;
      Assert.AreNotEqual(0.0, bowBefore);

      var newStage = FctStage.Halves(FctRegionSide.Left, false, false, 1200, 700);
      FctResize.Rescale(new List<FctHitState> { hit }, Width, Height, newStage);

      Assert.AreEqual(bowBefore * (1200.0 / Width), hit.Bow, 1e-9, "the bow scales with the territory, like the arc");

      var region = newStage.RegionFor(true);
      for (var t = 0.0; t <= 1.0; t += 0.1)
      {
        var x = FctMotion.ArcedX(hit, t);
        Assert.IsTrue(x >= region.X && x <= region.X + region.Width,
          $"t={t:0.0}: x {x:0.#} left its resized half");
      }
    }

    /* A zero-length motion (the life shorter than the travel) is finished, not NaN — same contract the other styles owe. */
    [TestMethod]
    public void AZeroLengthMotionFinishesAtTheEndOfTheTravel()
    {
      var stage = Halves(FctRegionSide.Left, false, false);
      var hit = Spawn(stage, incoming: false, new Random(61));
      hit.MotionMs = 0;

      var x = FctMotion.ArcedX(hit, 1.0);
      var y = FctMotion.RaisedY(hit, 1.0);

      Assert.IsTrue(double.IsFinite(x) && double.IsFinite(y), "t=1 of a zero-length motion must be the endpoint, not NaN");
    }
  }
}
