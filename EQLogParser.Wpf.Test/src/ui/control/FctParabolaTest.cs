using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The parabola (FctMotionStyle.Parabola): a constant-speed vertical scroll arcing out to a vertex at half height and
   * back — MSBT's geometry as written, x = y²/4a measured from the rail's mid-point. What these pin down: that the arc
   * leaves its column, reaches its widest away from the seam at half height, and returns to the column as it fades; that
   * every value shares one path and one speed, crits and procs included — the chain of numbers following each other;
   * that it stays inside its half at every speed the dial can give; that bands ingest degrades it to hold instead of
   * letting a number cross the strip; and that resize maps the bow the same way it maps everything else.
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

      /* Spawned the way the stream spawns it: one pass to read the bands, then the column and edge pinned like FctStream
       * does — so these asserts measure the arc itself, not the origin jitter an unstreamed throw would add to it. */
      FctLayout.Spawn(hit, stage, rand);
      var region = stage.RegionFor(incoming);
      var edgeY = stage.UpFor(incoming) > 0 ? hit.BandMaxY : hit.BandMinY;
      FctLayout.Spawn(hit, stage, rand, (region.X + (region.Width / 2.0), edgeY));
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

    /* The shape's second clause — MSBT's formula as written (x = y²/4a from the rail's mid-point): the value leaves its
     * column, bows out to a vertex at half height pointing away from the seam, and is back on the column when it fades. */
    [TestMethod]
    public void TheArcBowsToAVertexAtHalfHeightAndReturnsToItsColumn()
    {
      foreach (var side in new[] { FctRegionSide.Left, FctRegionSide.Right })
      {
        var stage = Halves(side, false, false);
        var hit = Spawn(stage, incoming: true, new Random(21));
        var region = stage.RegionFor(true);
        var away = side is FctRegionSide.Left ? -1.0 : 1.0;

        Assert.AreEqual(away * stage.TerritoryFor(true) * FctLayout.ParabolaBowFrac, hit.Bow, 1e-9,
          $"the vertex must sit outward of the column on {side} half's stream");

        var x0 = FctMotion.ArcedX(hit, 0.0);
        Assert.AreEqual(0.0, FctMotion.ArcedX(hit, 1.0) - x0, 1e-9, "ends — and begins — on its own column");
        Assert.AreEqual(hit.Bow, FctMotion.ArcedX(hit, 0.5) - x0, 1e-9, "half height is the vertex");
        Assert.AreEqual(0.75, (FctMotion.ArcedX(hit, 0.25) - x0) / hit.Bow, 1e-9,
          "4·t(1−t): a quarter of the way in is three quarters of the bow");

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
     * MSBT's chain law in one test: every value on the rail — plain, crit or proc — enters at the same edge on the same
     * beat, because the hair of difference per row is what sheared the train apart before this: adaptive lifetimes made
     * rows overtake their neighbours, travel jitter ended them in different places, and a tempo computed before the
     * stream pinned the geometry gave three rows on one rail three private speeds. Same beat means same-size values
     * also share speed and endpoint exactly; a crit's endpoint is its own because the band reserves room for how tall
     * the text is — one rail per text size, which is as it should be: the big number stops where it stays inside.
     */
    [TestMethod]
    public void EveryValueOnTheRailSharesItsPathAndSpeed()
    {
      var ingest = new FctIngest(new Random(71)) { Style = FctMotionStyle.Parabola, Layout = FctLayoutChoice.Shipped };
      var hits = new List<FctHitState>();

      // value, crit, proc: the rail does not care, and neither do these asserts
      var spawns = new[] { (value: 1001.0, crit: false, proc: false), (value: 9001.0, crit: true, proc: false), (value: 604.0, crit: false, proc: true) };
      double entry = 0, endpoint = 0, rate = 0, size = 0;

      for (var i = 0; i < spawns.Length; i++)
      {
        var (value, crit, proc) = spawns[i];
        var hit = ingest.Accept(hits, FctLane.DamageDealt, value, "Flurry", crit, false, false, null,
          Width, Height, i * 900.0, proc);

        Assert.IsNotNull(hit, $"value {value} belongs on the rail too");
        Assert.AreEqual(FctMotionStyle.Parabola, hit.Style, "crits and procs ride the rail, they do not reroute off it");

        if (i == 0)
        {
          entry = hit.Y0;
          endpoint = FctMotion.RaisedY(hit, 1.0);
          rate = Math.Abs(hit.Rise) / hit.MotionMs;
          size = hit.ValueFontSize;
        }

        Assert.AreEqual(entry, hit.Y0, 1e-9, $"one entrance: value {value} starts where the first one started");

        /* One SCROLL RATE, not one duration: each row's time is its own travel over the shared px-per-second, so a
         * crit (bigger text, less road left between the reserves) crosses at exactly the speed a miss word does.
         * A shared DURATION across different travels was the per-category speed difference players saw. */
        Assert.AreEqual(rate, Math.Abs(hit.Rise) / hit.MotionMs, 1e-9,
          $"one rate: value {value} cannot run to a private tempo");

        if (hit.ValueFontSize == size)
        {
          // same size, entrance and beat leave nothing else free: identical endpoint means identical rate
          Assert.AreEqual(endpoint, FctMotion.RaisedY(hit, 1.0), 1e-9,
            $"one path at one rate: value {value} ends exactly where the first one ended");
        }

        Assert.AreEqual(0.0, FctMotion.ArcedX(hit, 1.0) - FctMotion.ArcedX(hit, 0.0), 1e-9,
          "and every value returns to the column on its way out");
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