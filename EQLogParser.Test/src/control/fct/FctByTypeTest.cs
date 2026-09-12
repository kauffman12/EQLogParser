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

      /* Per-hit regions answer in lanes (FctRailLane): columns owned outright by whoever booked them, and the booking says nothing about
       * who sent the number. This choice books one lane per half, so each claimant tiles its whole half. */
      var healColumn = stage.RegionFor(incomingHeal);
      Assert.AreEqual(healColumn.X, stage.RegionFor(outgoingHeal).X, "every healing number gets the category column whatever its direction");
      Assert.AreEqual(Width / 2, healColumn.X, "a lane-less choice falls back to its side's outer column");
      Assert.AreEqual(Width / 2, healColumn.Width, "one claimant per half tiles the half outright");
    }

    /* The four lanes are what the panel offers, so they are what the geometry answers: the lanes a category books tile their
     * half — two claimants in quarters in screen order, one claimant the whole half — and no two lanes share pixels, which
     * was the point of naming them. */
    [TestMethod]
    public void LanesTileTheirHalvesAndNeverSharePixels()
    {
      var stage = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left,
        healLane: FctRailLane.Right1, incomingDamageLane: FctRailLane.Right2, outgoingDamageLane: FctRailLane.Left1)
        .Stage(800, 560);

      var dealt = stage.RegionFor(new FctHitState { Lane = FctLane.DamageDealt });
      var taken = stage.RegionFor(new FctHitState { Lane = FctLane.DamageTaken, Incoming = true });
      var heal = stage.RegionFor(new FctHitState { Lane = FctLane.HealingReceived, Incoming = true, Heal = true });

      Assert.AreEqual(0, (int)dealt.X, "left 1 alone in its half owns it from the far left");
      Assert.AreEqual(400, (int)dealt.Width, "one claimant per half tiles the half outright");
      Assert.AreEqual(400, (int)heal.X, "right 1 is the inner-right quarter");
      Assert.AreEqual(200, (int)heal.Width, "two claimants in a half take it in quarters");
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

    /* A claim on a half, not a fixed quarter: two claims take it in quarters in screen order, one claim takes all of it. This is
     * where split's label budget used to die - the shipped default books one lane per half, so each of those lanes is a HALF now.
     * (Measured at 980 px against the user's report: a straight rail's inline-right label went from about a hundred pixels beside
     * its numbers - eleven letters at label size - to about two hundred and thirty, and a label below gets the width of both sides.) */
    [TestMethod]
    public void AClaimOnAHalfIsNotAFixedQuarter()
    {
      var hit = (FctLane lane, bool incoming, bool heal) => new FctHitState { Lane = lane, Incoming = incoming, Heal = heal };

      // the shipped default: heals alone on the left, both damage streams sharing right 2 - one claim per half
      var stage = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left).Stage(Width, Height);
      Assert.AreEqual(0.0, stage.RegionFor(hit(FctLane.HealingDealt, false, true)).X, "heals own their half from the far left");
      Assert.AreEqual(Width / 2, stage.RegionFor(hit(FctLane.DamageDealt, false, false)).Width, "one claim per half tiles the whole half");

      // two claims on the left: back to quarters in screen order; the shared right stays a half
      var split = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left,
        healLane: FctRailLane.Left1, incomingDamageLane: FctRailLane.Left2).Stage(Width, Height);
      Assert.AreEqual((0.0, Width / 4), (split.RegionFor(hit(FctLane.HealingDealt, false, true)).X, split.RegionFor(hit(FctLane.HealingDealt, false, true)).Width), "left 1 is the far-left quarter");
      Assert.AreEqual((Width / 4, Width / 4), (split.RegionFor(hit(FctLane.DamageTaken, true, false)).X, split.RegionFor(hit(FctLane.DamageTaken, true, false)).Width), "left 2 is the inner-left quarter");
      Assert.AreEqual(Width / 2, split.RegionFor(hit(FctLane.DamageDealt, false, false)).Width, "the shared right lane still owns its half");

      // a shared lane is ONE claim: two categories naming left 1 free the rest of the half
      var shared = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left,
        healLane: FctRailLane.Left1, incomingDamageLane: FctRailLane.Left1, outgoingDamageLane: FctRailLane.Right2).Stage(Width, Height);
      Assert.AreEqual(Width / 2, shared.RegionFor(hit(FctLane.HealingDealt, false, true)).Width, "a shared lane counts once");

      // a solo INNER lane takes its half too - the space belongs to the claimant, not to the wall position
      var inner = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left, healLane: FctRailLane.Left2).Stage(Width, Height);
      Assert.AreEqual((0.0, Width / 2), (inner.RegionFor(hit(FctLane.HealingDealt, false, true)).X, inner.RegionFor(hit(FctLane.HealingDealt, false, true)).Width), "a lone left 2 still owns the whole left half");

      // and the shipped spread itself (FctConfigState's lanes): right 1 alone with right 2 empty tiles centre line to
      // the window's right edge - the case a player asks about when they free right 2 in setup mode
      var shipped = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left,
        healLane: FctRailLane.Left1, incomingDamageLane: FctRailLane.Left2, outgoingDamageLane: FctRailLane.Right1).Stage(Width, Height);
      Assert.AreEqual((Width / 2, Width / 2), (shipped.RegionFor(hit(FctLane.DamageDealt, false, false)).X, shipped.RegionFor(hit(FctLane.DamageDealt, false, false)).Width), "a lone right 1 owns the whole right half");
    }

    /* What tiling frees is space, not position. A lane whose neighbour went none takes the half as REGION - labels, sway and walls
       all measure it, asserted above - but its SPINE keeps the centre of the slot it would hold in a full house. Players centre the
       overlay on their crosshair, so right 1 books next to the centre line to sit near the fight: freeing right 2 must widen its
       labels, never slide its numbers from 1000 out to 1200 at this width. Reported from the shipped spread, where exactly that
       slide made the damage column leave the NPC behind. */
    [TestMethod]
    public void ASpineHoldsItsSlotWhenItsNeighbourFreesTheHalf()
    {
      var hit = (FctLane lane, bool incoming, bool heal) => new FctHitState { Lane = lane, Incoming = incoming, Heal = heal };

      // the shipped spread: right 1 alone with right 2 free — region is the whole half, spine stays at the inner slot's middle
      var shipped = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left,
        healLane: FctRailLane.Left1, incomingDamageLane: FctRailLane.Left2, outgoingDamageLane: FctRailLane.Right1).Stage(Width, Height);
      Assert.AreEqual(Width * 5 / 8, shipped.SpineFor(hit(FctLane.DamageDealt, false, false)), "a lone right 1 parks beside the seam it was booked next to");

      // book the neighbour back and nothing about either spine moves - only the region shrinks back to quarters
      var pair = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left,
        healLane: FctRailLane.Left1, incomingDamageLane: FctRailLane.Right1, outgoingDamageLane: FctRailLane.Right2).Stage(Width, Height);
      Assert.AreEqual(Width * 5 / 8, pair.SpineFor(hit(FctLane.DamageTaken, true, false)), "booking the neighbour changes no spine");
      Assert.AreEqual(Width * 7 / 8, pair.SpineFor(hit(FctLane.DamageDealt, false, false)), "right 2's slot is the outer quarter's middle");

      // the rule is uniform: a lone right 2 holds that same 7/8 mark it held in the full house
      var loneOuter = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left,
        healLane: FctRailLane.Left1, incomingDamageLane: FctRailLane.Left2, outgoingDamageLane: FctRailLane.Right2).Stage(Width, Height);
      Assert.AreEqual(Width * 7 / 8, loneOuter.SpineFor(hit(FctLane.DamageDealt, false, false)), "a lone right 2 holds its outer slot too");

      // and mirrored inside: healing booked alone on the INNER-left column parks at 3/16, not at the half's centre
      var inner = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left, healLane: FctRailLane.Left2).Stage(Width, Height);
      Assert.AreEqual(Width * 3 / 8, inner.SpineFor(hit(FctLane.HealingDealt, false, true)), "a lone left 2 parks at its slot's middle");

      // the region still grew underneath all that standing still - spine fixed, budget doubled (asserted by AClaimOnAHalfIsNotAFixedQuarter)
      Assert.AreEqual(Width / 2, shipped.RegionFor(hit(FctLane.DamageDealt, false, false)).Width, "the freed air is still handed to the region");
    }

    /* The configure-mode guide's own call sequence (FctSkiaCanvas.DrawLaneGuide): a config state, BuildLayout, Stage,
       LaneRect - traced for the two arrangements whose outlines are disputed. The shipped spread books one category per
       lane and leaves right 2 free, so its solo right 1 outlines centre line to right edge while the left pair takes the
       quarters; taking healing back to "none" grows the left outline the same way. A box that disagrees with its numbers
       cannot survive this test and the row-side assertions above at the same time, because both read the one LaneRect. */
    [TestMethod]
    public void AGuideOutlineIsItsRowsOwnBox()
    {
      var spread = new FctConfigState { Fountain = false };
      Assert.AreEqual(0, spread.ResolveLaneConflicts(), "the shipped spread books no conflicts and gets no moves");
      var stage = spread.BuildLayout().Stage(Width, Height);
      Assert.AreEqual((0.0, Width / 4), (stage.LaneRect(FctRailLane.Left1).X, stage.LaneRect(FctRailLane.Left1).Width), "left 1 outlines the far-left quarter");
      Assert.AreEqual((Width / 4, Width / 4), (stage.LaneRect(FctRailLane.Left2).X, stage.LaneRect(FctRailLane.Left2).Width), "left 2 outlines the inner-left quarter");
      Assert.AreEqual((Width / 2, Width / 2), (stage.LaneRect(FctRailLane.Right1).X, stage.LaneRect(FctRailLane.Right1).Width), "solo right 1 outlines its whole half - right 2 free is air, not a wall");

      var freed = new FctConfigState { Fountain = false, HealLane = FctRailLane.None };
      freed.ResolveLaneConflicts();
      var bare = freed.BuildLayout().Stage(Width, Height);
      Assert.AreEqual((0.0, Width / 2), (bare.LaneRect(FctRailLane.Left2).X, bare.LaneRect(FctRailLane.Left2).Width), "with healing off the rail, left 2 outlines the whole left half");
      Assert.AreEqual((Width / 2, Width / 2), (bare.LaneRect(FctRailLane.Right1).X, bare.LaneRect(FctRailLane.Right1).Width), "and right 1 still owns its half the same way");
    }

    /* A row still in flight when its category hands its column back keeps the walls it was born under until it scrolls out: the gate
     * stops new numbers of that category (FctIngest.Show*), but the old ones must not be left dangling in a lane that no longer exists.
     * And None must not couple directions against itself on the way through. */
    [TestMethod]
    public void ARowWhoseLaneIsReclaimedKeepsItsBirthWalls()
    {
      var hits = new List<FctHitState>();
      var born = new FctIngest(new Random(5)) { Style = FctMotionStyle.Straight, Layout = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left) };
      var heal = born.Accept(hits, FctLane.HealingDealt, 100.0, null, false, false, false, null, Width, Height, 0);
      Assert.IsNotNull(heal);

      // the settings hand heals' lane back; a new stage says None where it used to say left 1
      var reclaimed = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left, healLane: FctRailLane.None).Stage(Width, Height);
      var region = reclaimed.RegionFor(heal!);

      Assert.AreEqual(heal!.SideMin, region.X, "the old row keeps the left wall it was born under");
      Assert.AreEqual(Math.Max(1.0, heal.SideMax - heal.SideMin), region.Width, "and the right one");
    }
  }
}