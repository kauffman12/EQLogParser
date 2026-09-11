using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The arc (FctMotionStyle.Arc): a constant-speed vertical scroll arcing out to a vertex at half height and
   * back — MSBT's geometry as written, x = y²/4a measured from the rail's mid-point. Split ships this shape, so these are
   * the pins on the geometry a player actually sees: that the arc leaves its column, reaches its widest away from the
   * canvas centre at half height and returns to the column as it fades; that every value shares one path and one speed,
   * crits and procs included — the chain of numbers following each other up a lane; that it stays inside the lane that
   * owns it at every speed the dial can give; and that resize maps the bow the same way it maps everything else. What a
   * rail costs in bands (degraded to freeze, because a straight scroll across the canvas walks through the protected
   * strip) is pinned at the end, where the degradation is the subject rather than the exception.
   */
  [TestClass]
  public sealed class FctArcTest
  {

    [TestInitialize]
    public void ResetAmbient() => FctAmbient.Reset();
    private const double Width = 980;
    private const double Height = 640;

    /* Split with one assignment throughout: heals on `healSide`, both damage streams on the opposite side, each category
     * taking its side's outer lane (FctRailLanes.OfSide). A damage number therefore owns a quarter of the canvas whose
     * spine sits an eighth of the width from the near edge, and its outward wall is the edge of the overlay. */
    private static FctStage Split(bool incomingUp, bool outgoingUp, double w = Width, double h = Height,
      FctRegionSide healSide = FctRegionSide.Left)
      => FctStage.ByType(healSide, incomingUp, outgoingUp, w, h);

    /* A number the way a rail spawns it: one pass to read the lane's band, then the lane's spine and the spawn edge pinned
     * like the conveyor does (FctPlacement.Pin), so these asserts measure the arc itself and not the origin jitter an
     * unstreamed throw would add to it. Both questions are asked of the HIT — a lane is per category, not per side. */
    private static FctHitState Spawn(FctStage stage, FctHitState hit, Random rand)
    {
      FctStyle.ApplyTo(hit, hit.Lane, minor: false);
      hit.ValueWidth = FctLayout.EstimateTextWidth("1,234", hit.ValueFontSize); // what FctIngest.Accept does after style

      FctLayout.Spawn(hit, stage, rand);
      var region = stage.RegionFor(hit);
      var edgeY = stage.UpFor(hit) > 0 ? hit.BandMaxY : hit.BandMinY;
      FctLayout.Spawn(hit, stage, rand, (region.X + (region.Width / 2.0), edgeY));
      return hit;
    }

    private static FctHitState Damage(bool incoming) => new()
    {
      Lane = incoming ? FctLane.DamageTaken : FctLane.DamageDealt,
      Incoming = incoming,
      Style = FctMotionStyle.Arc,
      Source = "Spinning Attack",
      Value = 1234,
    };

    private static FctHitState Heal(bool incoming) => new()
    {
      Lane = incoming ? FctLane.HealingReceived : FctLane.HealingDealt,
      Incoming = incoming,
      Heal = true,
      Style = FctMotionStyle.Arc,
      Source = "Healing Wind",
      Value = 1234,
    };

    /* The shape's first clause: with y linear in t, equal time steps cover equal distances. Eased motion does not. */
    [TestMethod]
    public void TheVerticalScrollRunsAtConstantSpeed()
    {
      var stage = Split(incomingUp: false, outgoingUp: false);
      var hit = Spawn(stage, Damage(incoming: false), new Random(11));
      Assert.AreNotEqual(0.0, Math.Abs(hit.Rise), "an arc with no vertical travel is a freeze in a costume");

      var y1 = FctMotion.RaisedY(hit, 0.25);
      var y2 = FctMotion.RaisedY(hit, 0.50);
      var y3 = FctMotion.RaisedY(hit, 0.75);
      Assert.AreEqual(y1 - y2, y2 - y3, 1e-9, "equal time steps must cover equal distances at constant speed");
    }

    /* The shape's second clause — MSBT's formula as written (x = y²/4a from the rail's mid-point): the value leaves its
     * column, bows out to a vertex at half height pointing away from the middle of the overlay, and is back on the column
     * when it fades. Asked of a damage lane and a healing lane, which bow in opposite directions by construction: the
     * outward direction is whichever way leaves this column, never whichever side the number belongs to. */
    [TestMethod]
    public void TheArcBowsToAVertexAtHalfHeightAndReturnsToItsColumn()
    {
      foreach (var caseName in new[] { "damage", "heal" })
      {
        // 4000 wide so the formula's vertex never meets the right-align room cap (FctLayout.AssignTravel): this test
        // pins MSBT's pure geometry, and the cap's honesty has its own containment test below. A lane is a quarter of
        // the canvas, so the headroom the formula needs takes an overlay wider than any real one — which is the point
        // of separating the two questions.
        var stage = Split(incomingUp: false, outgoingUp: false, w: 4000);
        var hit = Spawn(stage, caseName is "heal" ? Heal(incoming: false) : Damage(incoming: true), new Random(21));
        var region = stage.RegionFor(hit);

        // outward is away from the middle of the overlay, which for a lane is whichever wall it is nearer
        var away = region.X + (region.Width / 2) < stage.W / 2 ? -1.0 : 1.0;

        Assert.AreEqual(away * stage.TerritoryFor(hit) * FctLayout.ArcBowFrac, hit.Bow, 1e-9,
          $"the vertex must sit outward of the column on the {caseName} lane");

        var x0 = FctMotion.ArcedX(hit, 0.0);
        Assert.AreEqual(0.0, FctMotion.ArcedX(hit, 1.0) - x0, 1e-9, "ends — and begins — on its own column");
        Assert.AreEqual(hit.Bow, FctMotion.ArcedX(hit, 0.5) - x0, 1e-9, "half height is the vertex");
        Assert.AreEqual(0.75, (FctMotion.ArcedX(hit, 0.25) - x0) / hit.Bow, 1e-9,
          "4·t(1−t): a quarter of the way in is three quarters of the bow");

        // and for the whole life, wherever it is, it is in the lane that owns it
        for (var t = 0.0; t <= 1.0; t += 0.1)
        {
          var x = FctMotion.ArcedX(hit, t);
          Assert.IsTrue(x >= region.X && x <= region.X + region.Width,
            $"t={t:0.0}: x {x:0.#} left the {caseName} lane [{region.X:0.#}..{region.X + region.Width:0.#}]");
        }
      }
    }

    /* Right-align and a narrow lane: the drawn box hangs its whole width left of the rail, so a full-formula bow toward
     * the inward wall would clip — and a clipped vertex scores one flight while drawing another. The layout trims the bow
     * instead; this pins that the WHOLE box never leaves the lane at any t. A quarter of a 700 px canvas is a column this
     * text genuinely does not fit comfortably, which is what makes it the cap's test rather than another shape test. */
    [TestMethod]
    public void ANarrowLaneTrimsTheBowToKeepTheWholeBoxIn()
    {
      var sawTrim = false;

      /* The lane that can run out of room is the one against the near edge: its numbers hang their WHOLE width left of
         the rail (right-align, FctMotion.ArcedX) and the rail itself is placed to hold "999,999" at crit size plus a mark,
         so the outward wall has already given ground before the curve asks for any. The sweep runs several widths because how
         narrow that is in practice depends on the font dials, and the claim below needs the cap to have had work to do at
         least once (asserted at the end) while every width owes the same containment. */
      foreach (var w in new[] { 980, 700, 520, 420 })
      {
        var stage = Split(incomingUp: false, outgoingUp: false, w: w, healSide: FctRegionSide.Right);
        var hit = Spawn(stage, Damage(incoming: true), new Random(21));
        var region = stage.RegionFor(hit);
        var formula = FctLayout.ArcBowFrac * stage.TerritoryFor(hit);

        Assert.IsTrue(Math.Abs(hit.Bow) <= formula + 1e-9, $"{w} px: the cap only ever trims the formula, never widens it");
        sawTrim |= Math.Abs(hit.Bow) < formula - 1e-9;

        for (var t = 0.0; t <= 1.0001; t += 0.05)
        {
          var x = FctMotion.ArcedX(hit, t);
          Assert.IsTrue((x - (hit.ValueWidth / 2.0)) >= region.X && ((x + (hit.ValueWidth / 2.0)) <= (region.X + region.Width)),
            $"{w} px, t={t:0.##}: the drawn box left its lane");
        }
      }

      Assert.IsTrue(sawTrim, "none of these lanes was too narrow for the full bow, so nothing here tested the cap at all");

      /* And the asymmetry is the near wall's, not a kill switch: the healing lane on the same canvas bows its full share at
         the widths where the damage lane lost its curve entirely, because what it ran out of was room against the edge, not
         permission to arc. */
      var wideLane = Split(incomingUp: false, outgoingUp: false, w: 700, healSide: FctRegionSide.Right);
      var bowsStill = Spawn(wideLane, Heal(incoming: false), new Random(21));
      Assert.AreEqual(FctLayout.ArcBowFrac * wideLane.TerritoryFor(bowsStill), Math.Abs(bowsStill.Bow), 1e-9,
        "the lane whose outward wall is the middle of the overlay keeps the formula at a width that takes it away from the other");
    }

    /*
     * MSBT's chain law in one test: every value on the rail — plain, crit or proc — enters at the same edge on the same
     * beat, because the hair of difference per row is what sheared the train apart before this: adaptive lifetimes made
     * rows overtake their neighbours, travel jitter ended them in different places, and a tempo computed before the rail
     * pinned the geometry gave three rows on one rail three private speeds. Same beat means same-size values also share
     * speed and endpoint exactly; a crit's endpoint is its own because the band reserves room for how tall the text is —
     * one rail per text size, which is as it should be: the big number stops where it stays inside.
     */
    [TestMethod]
    public void EveryValueOnTheRailSharesItsPathAndSpeed()
    {
      var ingest = new FctIngest(new Random(71))
      {
        Style = FctMotionStyle.Arc,
        Layout = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left)
      };
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
        Assert.AreEqual(FctMotionStyle.Arc, hit.Style, "crits and procs ride the rail, they do not reroute off it");

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
     * The tempo system's promise for this style: FctScale.Time stretches or squeezes how long a number scrolls, and nothing
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
          var ingest = new FctIngest(new Random(31))
          {
            Style = FctMotionStyle.Arc,
            Layout = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left)
          };
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
     * is kept: the hit comes back a freeze, and a freeze by construction cannot cross the protected strip, so the sweep below
     * stays inside its band at every instant instead of measuring how bad the violation would have been.
     */
    [TestMethod]
    public void BandsIngestDegradesAnArcToFreezeInsteadOfCrossingTheStrip()
    {
      var ingest = new FctIngest(new Random(41)) { Style = FctMotionStyle.Arc, Layout = FctLayoutChoice.Bands };
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
        Assert.AreEqual(FctMotionStyle.Freeze, hit.Style, "ingest must not run an arc in bands");

        for (var t = 0.0; t <= 1.0; t += 0.05)
        {
          var y = FctMotion.RaisedY(hit, t);
          Assert.IsTrue(y >= hit.BandMinY - 1e-9 && y <= hit.BandMaxY + 1e-9,
            $"t={t:0.##}: y {y:0.#} left its band [{hit.BandMinY:0.#}..{hit.BandMaxY:0.#}]");
        }
      }

      Assert.IsTrue(checkedAny, "nothing was spawned to check");
    }

    /* The bow is a territory share exactly like Sway: resize maps it by the x factor, and the number still ends in its lane. */
    [TestMethod]
    public void ResizeMapsTheBowAndTheNumberStillEndsInItsLane()
    {
      var stage = Split(incomingUp: false, outgoingUp: false);
      var hit = Spawn(stage, Damage(incoming: true), new Random(51));
      var bowBefore = hit.Bow;
      Assert.AreNotEqual(0.0, bowBefore);

      var newStage = FctStage.ByType(FctRegionSide.Left, false, false, 1200, 700);
      FctResize.Rescale(new List<FctHitState> { hit }, Width, Height, newStage);

      Assert.AreEqual(bowBefore * (1200.0 / Width), hit.Bow, 1e-9, "the bow scales with the territory, like the arc");

      var region = newStage.RegionFor(hit);
      for (var t = 0.0; t <= 1.0; t += 0.1)
      {
        var x = FctMotion.ArcedX(hit, t);
        Assert.IsTrue(x >= region.X && x <= region.X + region.Width,
          $"t={t:0.0}: x {x:0.#} left its resized lane");
      }
    }



    /* A zero-length motion (the life shorter than the travel) is finished, not NaN — same contract the other styles owe. */
    [TestMethod]
    public void AZeroLengthMotionFinishesAtTheEndOfTheTravel()
    {
      var stage = Split(incomingUp: false, outgoingUp: false);
      var hit = Spawn(stage, Damage(incoming: false), new Random(61));
      hit.MotionMs = 0;

      var x = FctMotion.ArcedX(hit, 1.0);
      var y = FctMotion.RaisedY(hit, 1.0);

      Assert.IsTrue(double.IsFinite(x) && double.IsFinite(y), "t=1 of a zero-length motion must be the endpoint, not NaN");
    }
  }
}