using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  /*
   * The pieces the two simple modes are built on. "Split" (the player-facing name for side columns) now assigns each
   * of three categories — healing, damage on me, my damage — its own column and its own direction; "fountain" keeps
   * bands' geometry but its dials must actually steer, so bands reads directions instead of hard-coding the strip
   * invariant (its omitted defaults still ARE that invariant, which is every pre-existing test). And the split shapes:
   * straight is the parabola's rail with the bow taken out — same train, same beat, no arc — while bands degrades both
   * rail styles to hold exactly as it always degraded the parabola.
   */
  [TestClass]
  public sealed class FctSplitModesTest
  {
    private const double Width = 1600;
    private const double Height = 900;

    /* Straight runs the train: shared entrance, one beat, and zero lateral travel — ArcedX must pin every value to its
     * column for the whole flight, or "straight" is secretly a scatter. */
    [TestMethod]
    public void StraightRunsTheSameRailWithTheArcTakenOut()
    {
      var ingest = new FctIngest(new Random(29)) { Style = FctMotionStyle.Straight };
      var hits = new List<FctHitState>();

      for (var i = 0; i < 4; i++)
      {
        Assert.IsNotNull(ingest.Accept(hits, FctLane.DamageDealt, 100 + i, "Flurry", false, false, false, null, Width, Height, i * 800));
      }

      Assert.AreEqual(1, hits.Select(h => h.Y0).Distinct().Count(), "one entrance, shared with every other value");
      Assert.AreEqual(1, hits.Select(h => h.MotionMs).Distinct().Count(), "one beat per region — straight inherits it whole");

      foreach (var hit in hits)
      {
        Assert.AreEqual(0.0, hit.Bow, 1e-9, "straight is bow zero, not a separate choreography");
        for (var t = 0.0; t <= 1.0; t += 0.05)
        {
          Assert.AreEqual(hit.X0, FctMotion.ArcedX(hit, t), 1e-9, $"a straight row drifted sideways at t={t:F2}");
        }
      }
    }

    /* Bands still refuses to scroll across itself: both rail styles degrade to hold there, hand-written settings.ini included. */
    [TestMethod]
    public void BandsDegradesEveryRailStyleToHold()
    {
      foreach (var rail in new[] { FctMotionStyle.Parabola, FctMotionStyle.Straight })
      {
        var ingest = new FctIngest(new Random(7)) { Style = rail, Layout = FctLayoutChoice.Bands };
        var hits = new List<FctHitState>();

        var hit = ingest.Accept(hits, FctLane.DamageDealt, 500, "Flurry", false, false, false, null, Width, Height, 0);

        Assert.IsNotNull(hit);
        Assert.AreEqual(FctMotionStyle.Hold, hit.Style, $"{rail} cannot scroll across a scheme whose region is the whole canvas");
      }
    }

    /* The fountain mode's two dials: bands obeys them now. Omitted directions keep drawing the old outward scheme —
     * that is what every other bands test rides — but a stated direction wins, and stating both is the UI's job. */
    [TestMethod]
    public void BandsSteersByItsDirectionsWhenSomebodyStatesThem()
    {
      var upward = new FctIngest(new Random(13))
      {
        Layout = new FctLayoutChoice(FctLayoutMode.Bands, FctRegionSide.Left, incomingUp: true, outgoingUp: false),
      };
      var hits = new List<FctHitState>();

      var incoming = upward.Accept(hits, FctLane.DamageTaken, 500, "Bite", false, false, false, null, Width, Height, 0);
      var outgoing = upward.Accept(hits, FctLane.DamageDealt, 500, "Flurry", false, false, false, null, Width, Height, 10);

      Assert.IsNotNull(incoming);
      Assert.IsTrue(incoming.Rise > 0, "the incoming dial says up, so incoming rises");
      Assert.IsNotNull(outgoing);
      Assert.IsTrue(outgoing.Rise < 0, "the outgoing dial says down, so outgoing sinks");

      var defaulted = new FctIngest(new Random(13)) { Layout = FctLayoutChoice.Bands };
      var legacy = defaulted.Accept(new List<FctHitState>(), FctLane.DamageDealt, 500, "Flurry", false, false, false, null, Width, Height, 20);

      Assert.IsTrue(legacy.Rise > 0, "omitted directions still ship the strip invariant (out rises)");
    }

    /* Three categories, three sides, both damage streams joining the heals on one side — the columns follow the
     * assignment whatever it is, and the untouched schemes (halves, bands) never consult these bits. */
    [TestMethod]
    public void AllThreeCategoriesMayShareOneColumnByRequest()
    {
      var choice = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left,
        incomingUp: true, outgoingUp: true, healSide: FctRegionSide.Right, healUp: true,
        incomingDamageSide: FctRegionSide.Right, outgoingDamageSide: FctRegionSide.Right);
      var ingest = new FctIngest(new Random(3)) { Style = FctMotionStyle.Straight, Layout = choice };
      var hits = new List<FctHitState>();

      var healed = ingest.Accept(hits, FctLane.HealingDealt, 800, "Heal", false, false, false, null, Width, Height, 0);
      var bitten = ingest.Accept(hits, FctLane.DamageTaken, 500, "Bite", false, false, false, null, Width, Height, 400);
      var swung = ingest.Accept(hits, FctLane.DamageDealt, 600, "Flurry", false, false, false, null, Width, Height, 800);

      foreach (var hit in new[] { healed, bitten, swung })
      {
        Assert.IsNotNull(hit);
        Assert.IsTrue(hit.X0 > Width / 2, "every category was assigned the right column");
      }
    }
  }
}