using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  /*
   * by type — halves' two columns with the ownership question swapped: healing owns one column, everything else the
   * other, and each direction's travel setting still steers inside it. This is the view a player asks for ("heals left,
   * damage right, mine up, theirs down") and the one MSBT cannot serve from a single area — his areas scroll one way —
   * so his users assemble it out of Add Scroll Area plus re-mapping the heal events. Here it is one mode because
   * placement scores whole flights: two trains sharing a column is traffic, not a contradiction. What these pin: which
   * column owns what; that opposing directions in one column drop nothing and never leave it; that the rail runs here
   * exactly as it runs in halves; and that the new mode's plumbing left halves' and bands' answers alone.
   */
  [TestClass]
  public sealed class FctByTypeTest
  {
    private const double Width = 1600;
    private const double Height = 900;

    private static FctLayoutChoice Choice(FctRegionSide healSide, bool incomingUp = false, bool outgoingUp = false)
      => new(FctLayoutMode.ByType, FctRegionSide.Left, incomingUp, outgoingUp, healSide);

    private static FctIngest Streaming(FctLayoutChoice choice)
      => new(new Random(17)) { Style = FctMotionStyle.Parabola, Layout = choice };

    /* The ownership swap itself: healing of either direction to its column, damage of either direction to the other,
     * and flipping the setting flips both — nothing else moves. */
    [TestMethod]
    public void HealingOwnsItsColumnAndEverythingElseTheOther()
    {
      foreach (var healSide in new[] { FctRegionSide.Left, FctRegionSide.Right })
      {
        var ingest = Streaming(Choice(healSide));
        var hits = new List<FctHitState>();

        var dealt = ingest.Accept(hits, FctLane.HealingDealt, 1234, "Greater Heal", false, false, false, null, Width, Height, 0);
        var cured = ingest.Accept(hits, FctLane.HealingReceived, 999, "Cure Disease", false, false, false, null, Width, Height, 600);
        var swung = ingest.Accept(hits, FctLane.DamageDealt, 777, "Flurry", false, false, false, null, Width, Height, 1200);
        var bitten = ingest.Accept(hits, FctLane.DamageTaken, 555, "Bite", false, false, false, null, Width, Height, 1800);

        var mid = Width / 2;
        foreach (var (hit, heals, what) in new[]
        {
          (dealt, true, "my healing"), (cured, true, "healing on me"),
          (swung, false, "my damage"), (bitten, false, "damage on me"),
        })
        {
          Assert.IsNotNull(hit, $"{what} belongs on the overlay in by type");
          hits.Remove(hit); // each assert below only cares where THIS number was born

          var onHealSide = healSide is FctRegionSide.Left ? hit.X0 < mid : hit.X0 > mid;
          Assert.IsTrue(onHealSide == heals, $"{what} must sit on the {(heals ? "healing" : "damage")} column (X0={hit.X0:0})");
        }
      }
    }

    /* Travel is each CATEGORY's own setting: healing answers to its dial whatever shares its column, and the two damage
     * streams answer to theirs wherever they were sent. This is what "pick a side AND a direction for each" means —
     * MSBT cannot split these axes at all, since one area carries one direction for everything routed into it. */
    [TestMethod]
    public void EachCategoryTravelsItsOwnWayWhereverItWasSent()
    {
      var choice = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left,
        incomingUp: false, outgoingUp: true, healSide: FctRegionSide.Left, healUp: true,
        incomingDamageSide: FctRegionSide.Left, outgoingDamageSide: FctRegionSide.Left);
      var hits = new List<FctHitState>();

      var ingest = Streaming(choice);
      var healed = ingest.Accept(hits, FctLane.HealingReceived, 800, "Word of Healing", false, false, false, null, Width, Height, 0);
      var bitten = ingest.Accept(hits, FctLane.DamageTaken, 500, "Bite", false, false, false, null, Width, Height, 300);
      var swung = ingest.Accept(hits, FctLane.DamageDealt, 700, "Flurry", false, false, false, null, Width, Height, 600);

      Assert.IsNotNull(healed);
      Assert.IsTrue(healed.Rise > 0, "healing rises on the heals dial while damage on the same column sinks on its own");
      Assert.IsNotNull(bitten);
      Assert.IsTrue(bitten.Rise < 0, "the incoming stream obeys its dial, not its neighbour's");
      Assert.IsNotNull(swung);
      Assert.IsTrue(swung.Rise > 0, "and the outgoing stream obeys its");

      var mid = Width / 2;
      Assert.IsTrue(healed.X0 < mid && bitten.X0 < mid && swung.X0 < mid, "all three share one column by request — three trains, one rail space");
      Assert.AreEqual(0, ingest.DroppedCount, "which is a placement problem, never a reason to lose a number");
    }

    /*
     * The risk the mode introduces: two trains passing on one rail. Numbers moving toward each other in one column are
     * traffic that placement must resolve — never by dropping (the no-loss law) and never by leaving the column (the
     * territory law). A same-beat burst of alternating directions tries it at both spacings.
     */
    [TestMethod]
    public void OpposingTrainsPassWithoutLossAndWithoutLeavingTheirColumn()
    {
      foreach (var gap in new[] { 0.0, 120.0, 480.0 })
      {
        var ingest = Streaming(Choice(FctRegionSide.Left, incomingUp: false, outgoingUp: true));
        var hits = new List<FctHitState>();

        for (var i = 0; i < 10; i++)
        {
          var lane = i % 2 is 0 ? FctLane.HealingDealt : FctLane.HealingReceived;
          var hit = ingest.Accept(hits, lane, 500 + i * 37, "Radiant Cure", false, false, false, null, Width, Height, i * gap);
          Assert.IsNotNull(hit, $"by type never loses a number to traffic, not even head-on (gap {gap}, i {i})");
        }

        Assert.AreEqual(0, ingest.DroppedCount, "the burst was resolved, not pruned");

        foreach (var hit in hits)
        {
          var half = hit.ValueWidth * (hit.Blowout ? FctMotion.CritPeakScale : 1.0) / 2.0;
          for (var t = 0.0; t <= 1.0; t += 0.1)
          {
            var x = FctMotion.ArcedX(hit, t);
            Assert.IsTrue(x - half >= 0 && x + half <= Width / 2,
              $"a healing row crossed the seam at t={t:F1} (x={x:0})");
          }
        }
      }
    }

    /* by type rides the rail, not the scatter: one beat for the side, and no more than three columns ever in view —
     * the same discipline halves' stream test pins, proving the mode reuses that engine rather than excusing itself out of it. */
    [TestMethod]
    public void TheRailRunsInByTypeExactlyAsItDoesInHalves()
    {
      var ingest = Streaming(Choice(FctRegionSide.Right));
      var hits = new List<FctHitState>();

      for (var i = 0; i < 12; i++)
      {
        var hit = ingest.Accept(hits, FctLane.DamageDealt, 100 + i * 13, "Flurry", false, false, false, null, Width, Height, i * 750);
        Assert.IsNotNull(hit);
      }

      Assert.AreEqual(1, hits.Select(h => h.MotionMs).Distinct().Count(), "one region, one beat");

      var columns = hits.Select(h => Math.Round(h.X0 / 10.0)).Distinct().Count();
      Assert.IsTrue(columns <= 6, $"the rail keeps a steady stream to its columns, saw {columns}");
    }

    /* Legality plumbing: by type is a side scheme, so the genre's shape is its default too — bands alone keeps hold. */
    [TestMethod]
    public void ByTypeShipsWithTheParabolaLikeEverySideScheme()
    {
      Assert.AreEqual(FctMotionStyle.Parabola, FctStage.DefaultMotion(FctLayoutMode.ByType));
      Assert.AreEqual(FctMotionStyle.Hold, FctStage.DefaultMotion(FctLayoutMode.Bands));
    }

    /* The stage answers must not leak: a by-type question answered on a halves stage is still the halves answer —
     * the new heal bit is inert unless the mode is by type. */
    [TestMethod]
    public void TheHealBitIsInertOutsideItsOwnMode()
    {
      var halves = FctStage.Halves(FctRegionSide.Left, false, false, Width, Height);
      var stage = FctStage.ByType(FctRegionSide.Right, false, false, Width, Height);

      var incomingHeal = new FctHitState { Lane = FctLane.HealingReceived, Incoming = true, Heal = true };
      var outgoingHeal = new FctHitState { Lane = FctLane.HealingDealt, Incoming = false, Heal = true };

      Assert.AreEqual(halves.RegionFor(true).X, halves.RegionFor(incomingHeal).X, "halves answers by direction whatever the heal flag says");
      Assert.AreEqual(halves.RegionFor(false).X, halves.RegionFor(outgoingHeal).X, "halves again, from the other direction");

      Assert.AreEqual(stage.RegionFor(true, true).X, stage.RegionFor(false, true).X, "by type ignores direction entirely");

      /* Per-hit regions now answer in lanes (FctRailLane); the bool overload still answers in halves because the
       * cell grid — its only other caller — does not know categories. Both questions describe the same column, so
       * the healing lane must sit inside the healing half, and every healing number must land in it. */
      var healColumn = stage.RegionFor(incomingHeal);
      Assert.AreEqual(healColumn.X, stage.RegionFor(outgoingHeal).X, "every healing number gets the category column whatever its direction");
      Assert.AreEqual(Width - Width / 4, healColumn.X, "a lane-less choice falls back to its side's outer column");
      Assert.IsTrue(healColumn.Width <= Width / 2 && healColumn.X >= Width / 2, "the healing lane sits inside the healing half");
    }

    /* The four lanes are what the panel offers, so they are what the geometry answers: four quarters, disjoint by
     * construction — two categories in different lanes can never be woven into each other's pixels. */
    [TestMethod]
    public void TheFourLanesAreDisjointQuarters()
    {
      var stage = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left,
        healLane: FctRailLane.Right1, incomingDamageLane: FctRailLane.Right2, outgoingDamageLane: FctRailLane.Left1)
        .Stage(800, 560);

      var dealt = stage.RegionFor(new FctHitState { Lane = FctLane.DamageDealt });
      var taken = stage.RegionFor(new FctHitState { Lane = FctLane.DamageTaken, Incoming = true });
      var heal = stage.RegionFor(new FctHitState { Lane = FctLane.HealingReceived, Incoming = true, Heal = true });

      Assert.AreEqual(0, (int)dealt.X, "left 1 is the far-left quarter");
      Assert.AreEqual(400, (int)heal.X, "right 1 is the inner-right quarter");
      Assert.AreEqual(600, (int)taken.X, "right 2 is the far-right quarter");
      Assert.IsTrue(dealt.X + dealt.Width <= taken.X && heal.X + heal.Width <= taken.X,
        "different lanes can never share pixels — that was the whole point");

      /* Same lane means one stream: two categories naming one column get identical regions. */
      var shared = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left,
        healLane: FctRailLane.Left2, incomingDamageLane: FctRailLane.Left2, outgoingDamageLane: FctRailLane.Left2)
        .Stage(800, 560);
      Assert.AreEqual(shared.RegionFor(new FctHitState { Lane = FctLane.DamageDealt }).X,
        shared.RegionFor(new FctHitState { Lane = FctLane.HealingReceived, Incoming = true, Heal = true }).X,
        "categories that name one lane share it as one stream");
    }
  }
}