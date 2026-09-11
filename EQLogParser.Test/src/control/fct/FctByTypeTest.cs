using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  /*
   * by type — split: columns across the overlay whose side carries WHAT a number is. Healing owns a column, each damage stream owns
   * a column, and every category's direction dial still steers inside it. This is the view a player asks for ("heals left, damage
   * right, mine up, theirs down") and the one MSBT cannot serve from a single area — his areas scroll one way — so his users assemble
   * it out of Add Scroll Area plus re-mapping the heal events. Here it is one mode, four lanes (FctRailLane), and nothing else.
   *
   * Since the rails became queues (FctConveyor) this mode is where the ordering law lives, because owning a column is what makes one
   * train per lane provable: which column owns what; that categories sharing a lane share its queue and its direction; that opposing
   * trains in their own lanes drop nothing and never leave home; and that bands still answers for itself. The
   * ordering itself — one rate, spacing kept, no row ever drawn through another — is pinned by FctConveyorTest.
   */
  [TestClass]
  public sealed class FctByTypeTest
  {

    [TestInitialize]
    public void ResetAmbient() => FctAmbient.Reset();
    private const double Width = 1600;
    private const double Height = 900;

    private static FctLayoutChoice Choice(FctRegionSide healSide, bool incomingUp = false, bool outgoingUp = false)
      => new(FctLayoutMode.ByType, FctRegionSide.Left, incomingUp, outgoingUp, healSide);

    private static FctIngest Streaming(FctLayoutChoice choice)
      => new(new Random(17)) { Style = FctMotionStyle.Arc, Layout = choice };

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

    /*
     * Travel is each CATEGORY's own setting: three dials, three answers, whatever side each was sent to. The lanes are what make that
     * independence physical — a column is one queue (FctConveyor), so two categories sharing one travel together, and the stage
     * follows whoever owns the column. Three categories in three columns is therefore the arrangement that keeps "pick a side AND a
     * direction for each", which is still more than MSBT can say: his areas scroll one way for everything routed into them, and his
     * users buy a second area to get the second answer.
     */
    [TestMethod]
    public void EachCategoryTravelsItsOwnWayWhereverItWasSent()
    {
      var choice = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left,
        incomingUp: false, outgoingUp: true, healSide: FctRegionSide.Right, healUp: true,
        outgoingDamageLane: FctRailLane.Left1, incomingDamageLane: FctRailLane.Left2, healLane: FctRailLane.Right1);
      var hits = new List<FctHitState>();

      var ingest = Streaming(choice);
      var healed = ingest.Accept(hits, FctLane.HealingReceived, 800, "Word of Healing", false, false, false, null, Width, Height, 0);
      var bitten = ingest.Accept(hits, FctLane.DamageTaken, 500, "Bite", false, false, false, null, Width, Height, 300);
      var swung = ingest.Accept(hits, FctLane.DamageDealt, 700, "Flurry", false, false, false, null, Width, Height, 600);

      Assert.IsNotNull(healed);
      Assert.IsTrue(healed.Rise > 0, "healing rises on its own dial");
      Assert.IsNotNull(bitten);
      Assert.IsTrue(bitten.Rise < 0, "the incoming stream obeys its dial, not its neighbour's");
      Assert.IsNotNull(swung);
      Assert.IsTrue(swung.Rise > 0, "and the outgoing stream obeys its");

      var columns = hits.Select(h => Math.Round(h.X0)).Distinct().Count();
      Assert.AreEqual(3, columns, "three categories, three columns, one queue each");
      Assert.AreEqual(0, ingest.DroppedCount, "which costs nothing on the way either");
    }

    /*
     * The risk the mode introduces: trains passing in the night — my damage rising its column while what lands on me sinks the next.
     * Ordered traffic never drops a number (the no-loss law) and never leaves its own lane (the territory law), and a same-beat burst
     * of alternating directions tries that at three spacings including all-at-once.
     *
     * Note what this is NOT about any more: two categories pointed at ONE column in opposite directions. A column is one queue
     * (FctConveyor), so that arrangement cannot exist — the stage follows whoever owns the lane (FctStage) and the panel never offers
     * the choice (FctConfigState.ResolveLaneConflicts re-homes a hand-written settings.ini). That is what turned "nothing steps on
     * anything else" from a scored hope into something provable; FctConveyorTest.CategoriesSharingAColumnShareItsQueue pins it.
     */
    [TestMethod]
    public void OpposingTrainsPassWithoutLossAndWithoutLeavingTheirColumn()
    {
      var choice = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left, incomingUp: false, outgoingUp: true,
        healSide: FctRegionSide.Right, outgoingDamageLane: FctRailLane.Left1, incomingDamageLane: FctRailLane.Right1);

      foreach (var gap in new[] { 0.0, 120.0, 480.0 })
      {
        var ingest = Streaming(choice);
        var hits = new List<FctHitState>();

        for (var i = 0; i < 10; i++)
        {
          var lane = i % 2 is 0 ? FctLane.DamageDealt : FctLane.DamageTaken;
          Assert.IsNotNull(ingest.Accept(hits, lane, 500 + (i * 37), "Radiant Cure", false, false, false, null, Width, Height, i * gap),
            $"split never loses a number to traffic, not even a burst arriving in one frame (gap {gap}, row {i})");
          ingest.PruneExpired(hits, (i * gap) + 10);
        }

        Assert.AreEqual(0, ingest.DroppedCount, "the burst was held and delivered, not pruned");

        var stage = choice.Stage(Width, Height);
        foreach (var hit in hits)
        {
          Assert.IsTrue(hit.OnConveyor);
          var region = stage.RegionFor(hit);
          var half = hit.ValueWidth / 2.0;
          for (var t = 0.0; t <= 1.0; t += 0.1)
          {
            var x = FctMotion.ArcedX(hit, t);
            Assert.IsTrue(x - half >= region.X - 1 && x + half <= region.X + region.Width + 1,
              $"a {(hit.Heal ? "healing" : hit.Incoming ? "incoming" : "outgoing")} row left its lane at t={t:F1} (x={x:0})");
          }
        }
      }
    }

    /*
     * Split does not scatter, it queues (FctConveyor). Twelve rows at a beat enter at one mouth of one column, share one flight and
     * one rate, and none of them is nudged sideways to find room — so the discipline the conveyor tests ("one region, one
     * beat") is asserted here with the placement search taken out of the loop, which makes it stricter rather than looser: not "no
     * more than a handful of columns" but exactly one rail for the whole run, and one flight length per lane because that number is
     * what the lane's capacity is made of.
     */
    [TestMethod]
    public void SplitQueuesItsRailInsteadOfScatteringIt()
    {
      var ingest = Streaming(Choice(FctRegionSide.Right));
      var hits = new List<FctHitState>();

      for (var i = 0; i < 12; i++)
      {
        Assert.IsNotNull(ingest.Accept(hits, FctLane.DamageDealt, 100 + (i * 13), "Flurry", false, false, false, null, Width, Height, i * 750));
        ingest.PruneExpired(hits, (i * 750) + 375);   // the frame the overlay would have painted half a beat later
      }

      Assert.IsTrue(hits.Count > 1, "a run at a fighting beat should leave a column of numbers on screen");
      foreach (var hit in hits)
      {
        Assert.IsTrue(hit.OnConveyor, "split's rails ride a lane clock, whatever shape the dial was left on");
      }

      Assert.AreEqual(1, hits.Select(h => Math.Round(h.X0)).Distinct().Count(), "one rail, not a search's handful of them");
      Assert.AreEqual(1, hits.Select(h => Math.Round(h.ConveyorTravel)).Distinct().Count(), "and one flight length for the whole lane");
    }

    /* Legality plumbing: by type is a side scheme, so the genre's shape is its default too — bands alone keeps freeze. */
    [TestMethod]
    public void ByTypeShipsWithTheArcLikeEverySideScheme()
    {
      Assert.AreEqual(FctMotionStyle.Arc, FctStage.DefaultMotion(FctLayoutMode.ByType));
      Assert.AreEqual(FctMotionStyle.Freeze, FctStage.DefaultMotion(FctLayoutMode.Bands));
    }

    /* The category column must not leak: a by-type question asked of a bands stage is still the bands answer —
     * which column a category booked is inert in a scheme that draws no columns at all. */
    [TestMethod]
    public void TheHealBitIsInertOutsideItsOwnMode()
    {
      var bands = FctStage.Bands(Width, Height);
      var stage = FctStage.ByType(FctRegionSide.Right, false, false, Width, Height);

      var incomingHeal = new FctHitState { Lane = FctLane.HealingReceived, Incoming = true, Heal = true };
      var outgoingHeal = new FctHitState { Lane = FctLane.HealingDealt, Incoming = false, Heal = true };

      /* Bands owns one rect and every number lives in it: neither a category nor a direction moves a boundary there. */
      Assert.AreEqual(0.0, bands.RegionFor(incomingHeal).X, "bands answers the whole canvas whatever the heal flag says");
      Assert.AreEqual(Width, bands.RegionFor(outgoingHeal).Width, "bands again, from the other direction");

      /* Per-hit regions answer in lanes (FctRailLane): four columns, each owned outright by whoever booked it, and the
       * booking says nothing about who sent the number. */
      var healColumn = stage.RegionFor(incomingHeal);
      Assert.AreEqual(healColumn.X, stage.RegionFor(outgoingHeal).X, "every healing number gets the category column whatever its direction");
      Assert.AreEqual(Width - Width / 4, healColumn.X, "a lane-less choice falls back to its side's outer column");
      Assert.IsTrue(healColumn.Width <= Width / 2 && healColumn.X >= Width / 2, "a quarter column sits inside the half its side owns");
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