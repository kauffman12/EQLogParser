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

    [TestInitialize]
    public void ResetAmbient() => FctAmbient.Reset();
    private const double Width = 1600;
    private const double Height = 900;

    /* Split with the shipped assignment: heals on the left, both damage streams on the opposite side. Every rail test
     * here needs the scheme where a rail EXISTS — bands degrades both shapes to hold (BandsDegradesEveryRailStyleToHold
     * pins that), and a default-constructed ingest gets bands, so "split" has to be said out loud. */
    private static FctLayoutChoice Split => new(FctLayoutMode.ByType, FctRegionSide.Left);

    /* Run the lane's clock the way a host does. A conveyor is stamped from FctIngest.PruneExpired once a FRAME and one call
     * may only credit MaxStepMs of it (FctConveyor), so a test that leaps a second at a time runs every column several times
     * slow, its queue never drains, and starts refusing arrivals no overlay would refuse. Anything here that claims to be at
     * "a rate a fight can sustain" has to wind the clock in frame steps first. */
    private static void Wind(FctIngest ingest, List<FctHitState> hits, double from, double to)
    {
      for (var t = Math.Max(0, from) + FctConveyor.FrameMs; t <= to; t += FctConveyor.FrameMs)
      {
        ingest.PruneExpired(hits, t);
      }
    }

    /* Values are an odometer (right-aligned, the hidden default): two rows of different widths sharing a column keep
     * the SAME right edge for the whole flight — ones digits line up under each other — while their centres sit
     * wherever their own widths put them. A centre-anchored column jogs 950 out from under 12,040; the rail carries
     * right edges. Real text widths here, because zero-ish synthetic ones hide exactly this. */
    [TestMethod]
    public void ValuesInAColumnShareTheirRightEdge()
    {
      var ingest = new FctIngest(new Random(31)) { Style = FctMotionStyle.Straight, Layout = Split };
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
      var ingest = new FctIngest(new Random(29)) { Style = FctMotionStyle.Straight, Layout = Split };
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

    /* A fountain is bands wearing spray, and its heals dial speaks for itself: heals may rise while every damage
     * stream sprays down — the genre's classic look — instead of being silently chained to the damage-in direction
     * just because a band has no column to give them. The panel shows the dial in both modes now; this pins the
     * engine honouring it there too. */
    [TestMethod]
    public void AFountainRunsHealingItsOwnWay()
    {
      var layout = new FctLayoutChoice(FctLayoutMode.Bands, FctRegionSide.Left, incomingUp: false, outgoingUp: false, healUp: true);
      var stage = layout.Stage(Width, Height);

      Assert.AreEqual(1.0, stage.UpFor(new FctHitState { Heal = true, Incoming = true }), "heals rise on their own dial");
      Assert.AreEqual(-1.0, stage.UpFor(new FctHitState { Heal = false, Incoming = true }), "damage taken stays where its own dial put it");

      // and the whole flight agrees with the sign, not just the answer: spawn at the band's far edge, travel across it
      var ingest = new FctIngest(new Random(5)) { Style = FctMotionStyle.Spray, Layout = layout };
      var healed = ingest.Accept(new List<FctHitState>(), FctLane.HealingReceived, 800, "Complete Heal", false, false, false, null, Width, Height, 0);
      var bitten = ingest.Accept(new List<FctHitState>(), FctLane.DamageTaken, 500, "Bite", false, false, false, null, Width, Height, 100);

      Assert.IsNotNull(healed);
      Assert.IsNotNull(bitten);
      Assert.IsTrue(healed.Rise > 0, "the heal sprays up on its dial");
      Assert.IsTrue(bitten.Rise < 0, "the bite sinks on its own");
      Assert.IsTrue(healed.Y0 > Height / 2 && bitten.Y0 > Height / 2, "both still live in the incoming band; only travel differs");
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
     * live shorter, each clearing its stretch of road sooner) and the directions differ, and none of that may show as speed. */
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
        (FctLane.DamageDealt, 350, true, null),          // proc: same size, same rate
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
      var ingest = new FctIngest(new Random(5)) { Style = FctMotionStyle.Straight, Layout = Split };
      var hits = new List<FctHitState>();

      for (var round = 0; round < 12; round++)
      {
        // fighting pace: a swing a second, each answered by damage or a word, sometimes on the same frame
        var now = round * 1000;
        Wind(ingest, hits, from: round * 1000 - 1000, to: now);
        Assert.IsNotNull(ingest.Accept(hits, FctLane.DamageDealt, 300 + round, "Flurry", false, false, false, null, Width, Height, now));
        Assert.IsNotNull(ingest.Accept(hits, FctLane.Missed, 0, "Flurry", false, false, false,
          round % 2 == 0 ? Labels.Miss : Labels.Parry, Width, Height, now + 30));
      }

      var centre = hits[0].X0;
      foreach (var hit in hits)
      {
        Assert.AreEqual(centre, hit.X0, 1e-9, "at fighting speed every row holds the line: crowding steps along travel, not sideways");
      }

      // and a same-frame triple on top of it all still gets three rows that never print into each other
      var before = hits.Count;
      Wind(ingest, hits, from: 11000, to: 12000);

      // crits for the burst: they neither absorb nor fold (their policy is pinned elsewhere), so all three really do
      // demand an entrance in one instant — which is exactly the capacity question this asserts
      Assert.IsNotNull(ingest.Accept(hits, FctLane.DamageDealt, 999, "Flurry", true, false, false, null, Width, Height, 12000));
      Assert.IsNotNull(ingest.Accept(hits, FctLane.DamageDealt, 888, "Flurry", true, false, false, null, Width, Height, 12000));
      Assert.IsNotNull(ingest.Accept(hits, FctLane.DamageDealt, 777, "Flurry", true, false, false, null, Width, Height, 12000));

      /* The burst is where the column stops being a place and becomes a queue. Three rows arriving in one instant share
         ONE entrance — the same mouth, the same spine, which is the point of holding the line above — so "they never
         overlap" can no longer mean "they were thrown to different spots". It means the queue priced them apart: each
         pair sits at least a row's height apart along the rail from entry (FctConveyor.Enrol pays for the taller of the
         two), and being a difference of one shared phase, that gap is kept for the whole flight rather than closed by
         anybody overtaking. Row heights, not positions: the rows themselves are still behind the mouth at this instant. */
      var burst = hits.Skip(before).ToList();
      foreach (var a in burst)
      {
        foreach (var b in burst)
        {
          if (ReferenceEquals(a, b))
          {
            continue;
          }

          var room = Math.Min(FctLayout.TextHeight(a), FctLayout.TextHeight(b));
          Assert.IsTrue(Math.Abs(a.ConveyorQ - b.ConveyorQ) >= room - 1e-9,
            $"two rows of a burst were queued {(Math.Abs(a.ConveyorQ - b.ConveyorQ)):0.#} px apart, under the " +
            $"{room:0.#} px one of them needs");
        }
      }
    }
  }
}