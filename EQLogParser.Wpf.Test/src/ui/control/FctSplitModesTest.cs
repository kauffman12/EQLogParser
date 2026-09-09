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

    /* Values are an odometer (right-aligned, the hidden default): two rows of different widths sharing a column keep
     * the SAME right edge for the whole flight — ones digits line up under each other — while their centres sit
     * wherever their own widths put them. A centre-anchored column jogs 950 out from under 12,040; the rail carries
     * right edges. Real text widths here, because zero-ish synthetic ones hide exactly this. */
    [TestMethod]
    public void ValuesInAColumnShareTheirRightEdge()
    {
      var ingest = new FctIngest(new Random(31)) { Style = FctMotionStyle.Straight };
      var hits = new List<FctHitState>();

      // values whose ABBREVIATED text differs by three glyphs ("900" vs "987.7m"; the format abbreviates hard)
      var narrow = ingest.Accept(hits, FctLane.DamageDealt, 900.0, null, false, false, false, null, Width, Height, 0);
      var wide = ingest.Accept(hits, FctLane.DamageDealt, 987_654_321.0, null, false, false, false, null, Width, Height, 0);
      Assert.IsNotNull(narrow);
      Assert.IsNotNull(wide);
      Assert.IsTrue(wide.ValueWidth > narrow.ValueWidth + 5.0,
        $"the widths need to differ for this to mean anything ({narrow.ValueWidth:0.#} vs {wide.ValueWidth:0.#})");

      for (var t = 0.0; t <= 1.0001; t += 0.05)
      {
        var rightNarrow = FctMotion.ArcedX(narrow, t) + (narrow.ValueWidth / 2.0);
        var rightWide = FctMotion.ArcedX(wide, t) + (wide.ValueWidth / 2.0);
        Assert.AreEqual(rightNarrow, rightWide, 1e-9, $"the ones digit slipped out of line at t={t:F2}");
      }

      Assert.AreEqual(0.0, narrow.X0 - wide.X0, 1e-9, "one rail; only the drawn boxes hang differently");
    }

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
          // the RAIL (right edge of the drawn value) is what must not move; the centre hangs half a width off it
          Assert.AreEqual(hit.X0, FctMotion.ArcedX(hit, t) + (hit.ValueWidth / 2.0), 1e-9,
            $"a straight row drifted sideways at t={t:F2}");
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

    /* The promise split makes to the eye: ONE scroll rate for every kind of number, whatever its size, direction or
     * text. Damage, a proc, a miss word, a resist word, damage taken and healing — the sizes differ (procs and words
     * are drawn smaller, each reserving less road) and the directions differ, and none of that may show as speed. */
    [TestMethod]
    public void SplitMovesEveryCategoryAtOneRate()
    {
      var ingest = new FctIngest(new Random(11))
      {
        Style = FctMotionStyle.Parabola,
        Layout = new FctLayoutChoice(FctLayoutMode.ByType, FctRegionSide.Left, incomingUp: true, outgoingUp: false, healUp: true),
      };

      var hits = new List<FctHitState>();
      double now = 0, rate = 0;
      var rows = new (FctLane Lane, double Value, bool Proc, string? Text)[]
      {
        (FctLane.DamageDealt, 1500, false, null),
        (FctLane.DamageDealt, 350, true, null),          // proc: smaller, same rate
        (FctLane.Missed, 0, false, Labels.Miss),         // words: smallest, same rate
        (FctLane.Defensive, 0, false, Labels.Resist),
        (FctLane.DamageTaken, 800, false, null),         // the other direction, same rate
        (FctLane.HealingReceived, 900, false, null),
      };

      foreach (var (lane, value, proc, text) in rows)
      {
        var hit = ingest.Accept(hits, lane, value, "Test", false, false, false, text, Width, Height, now += 500, proc);
        Assert.IsNotNull(hit, $"{lane} belongs on the screen");

        var pxPerSecond = Math.Abs(hit.Rise) / hit.MotionMs;
        if (rate == 0)
        {
          rate = pxPerSecond;
          continue;
        }

        Assert.AreEqual(rate, pxPerSecond, 1e-9, $"{lane} crosses at the stream's one rate, not its own");
      }
    }

    /* A line never parks a whole category beside itself. The old sideways braid pushed the steady stream of misses and
     * parries into permanent offset mini-columns every fight, while resists -- rare enough to always find the mouth
     * clear -- sat centre, which is exactly what players read as miss/parry being "offset". Crowding now steps INTO
     * the travel first, so at any rate a real fight sustains every value holds its lane's centre X, words included,
     * and a same-frame burst still never prints two rows into one entrance. Overflow columns survive underneath for
     * past-throughput storms (no number is ever dropped): that valve belongs to bursts, not to the commute. */
    [TestMethod]
    public void CrowdedLinesStepAlongThemselvesBeforeSideways()
    {
      var ingest = new FctIngest(new Random(5)) { Style = FctMotionStyle.Straight };
      var hits = new List<FctHitState>();

      for (var round = 0; round < 12; round++)
      {
        // fighting pace: a swing a second, each answered by damage or a word, sometimes on the same frame
        var now = round * 1000;
        Assert.IsNotNull(ingest.Accept(hits, FctLane.DamageDealt, 300 + round, "Flurry", false, false, false, null, Width, Height, now));
        Assert.IsNotNull(ingest.Accept(hits, FctLane.Missed, 0, "Flurry", false, false, false,
          round % 2 == 0 ? Labels.Miss : Labels.Parry, Width, Height, now + 30));
      }

      var centre = hits[0].X0;
      foreach (var hit in hits)
      {
        Assert.AreEqual(centre, hit.X0, 1e-9, "at fighting speed every row holds the line: crowding steps along travel, not sideways");
      }

      // and a same-frame triple on top of it all still finds three separate entrances
      var before = hits.Count;
      // crits for the burst: they neither absorb nor fold (their policy is pinned elsewhere), so all three really do
      // demand entrances in one instant — which is exactly the placement question this asserts
      Assert.IsNotNull(ingest.Accept(hits, FctLane.DamageDealt, 999, "Flurry", true, false, false, null, Width, Height, 12000));
      Assert.IsNotNull(ingest.Accept(hits, FctLane.DamageDealt, 888, "Flurry", true, false, false, null, Width, Height, 12000));
      Assert.IsNotNull(ingest.Accept(hits, FctLane.DamageDealt, 777, "Flurry", true, false, false, null, Width, Height, 12000));

      var burst = hits.Skip(before).ToList();
      foreach (var a in burst)
      {
        foreach (var b in burst)
        {
          if (ReferenceEquals(a, b))
          {
            continue;
          }

          Assert.IsTrue(Math.Abs(a.Y0 - b.Y0) > 1.0 || Math.Abs(a.X0 - b.X0) > 1.0, "two numbers never share an entrance");
        }
      }
    }
  }
}