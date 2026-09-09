using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * Ingest decides what happens to an incoming hit: fold it, spawn it, or lose it to the lane cap. That is
   * the policy that keeps a raid pull readable, so it is pinned here — repeated hits collapse into one number
   * that says how many it stands for, no number ever shows an amount some single hit did not land for, and drops
   * are counted rather than silent.
   */
  [TestClass]
  public sealed class FctIngestTest
  {
    /*
     * Several assertions below are absolute choreography values — a fountain's life is the motion window, a proc is 0.7 of it — and those are the
     * numbers measured at the base tempo. The player's speed dial is applied on top of them (FctIngest.ApplyPlayerTempo) and its shipped position is not
     * 1.0 any more, so every test here starts from a flat scale or it measures whichever dial another test happened to leave parked. The dial's own
     * behaviour is FctScaleTest's job.
     */
    [TestInitialize]
    public void ResetPlayerScale()
    {
      FctScale.Text = FctScale.SizeDefault;
      FctScale.Time = 1;
    }

    private const double Width = 980;
    private const double Height = 640;

    private readonly List<FctHitState> _hits = [];

    /*
     * These tests pin the bands scheme — the strip and its directions are what most of them assert about — so they say so
     * instead of inheriting whatever the overlay ships with.
     */
    private static FctIngest NewIngest() => new(new Random(20_260_714)) { Layout = FctLayoutChoice.Bands };

    [TestMethod]
    public void FirstHitSpawnsOnItsSide()
    {
      var ingest = NewIngest();

      var hit = ingest.Accept(_hits, FctLane.DamageDealt, 500, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0);

      Assert.IsNotNull(hit);
      Assert.AreEqual(1, _hits.Count);
      Assert.AreEqual(FctLane.DamageDealt, hit.Lane);
      Assert.IsFalse(hit.Incoming);
      Assert.AreEqual("Flurry", hit.Source);
      Assert.AreEqual("500", hit.DisplayText);
      Assert.IsTrue(hit.ValueWidth > 0, "spawn must seed a width estimate for the center clamp");
    }

    [TestMethod]
    public void CritsPoolOntoTheCritLaneButKeepTheirSide()
    {
      var ingest = NewIngest();

      var taken = ingest.Accept(_hits, FctLane.DamageTaken, 900, "Bites", crit: true, minor: false, periodic: false, fixedText: null, Width, Height, 0);
      var dealt = ingest.Accept(_hits, FctLane.DamageDealt, 900, "Flurry", crit: true, minor: false, periodic: false, fixedText: null, Width, Height, 10);

      // a crit I take must still appear on the incoming side, or the region scheme stops meaning anything
      Assert.AreEqual(FctLane.Crit, taken.Lane);
      Assert.IsTrue(taken.Incoming);
      Assert.AreEqual(FctLane.Crit, dealt.Lane);
      Assert.IsFalse(dealt.Incoming);
      Assert.IsTrue(taken.Blowout && dealt.Blowout);
    }

    /*
     * Size is the ONE lever the crit lane does not own outright: a big event is written at its OWN KIND's font times the crit dial, where that kind is
     * recovered from the side flags (the pooled lane has stopped saying heal or taken). That relativity is what makes the dial's floor honest - at 0 % a
     * crit is exactly the size the same hit would have been and stands out only by its pop, halo and draw order - and it means a taken crit, a heal crit and
     * a dealt crit each stay near their own lane's scale instead of all being dragged to one "crit font" above everything. Both are pinned here, since nothing
     * else in the file cares about the number's magnitude.
     */
    [TestMethod]
    public void ABigEventTakesItsKindsSizeTimesTheCritDial()
    {
      var ingest = NewIngest();
      var crit = FctScale.Crit;
      try
      {
        FctScale.Crit = FctScale.CritSizeDefault; // start from the shipped position, not whatever another test parked
        var plainTaken = ingest.Accept(_hits, FctLane.DamageTaken, 900, "Bites", false, false, false, null, Width, Height, 0);
        var takenCrit = ingest.Accept(_hits, FctLane.DamageTaken, 901, "Bites", true, false, false, null, Width, Height, 10);
        FctScale.Crit = 1.0; // the dial's floor: emphasis dialed all the way off
        var parityCrit = ingest.Accept(_hits, FctLane.DamageTaken, 902, "Bites", true, false, false, null, Width, Height, 20);

        Assert.IsTrue(takenCrit.Blowout, "still a big event, whatever its font says");
        Assert.AreEqual(plainTaken.ValueFontSize, parityCrit.ValueFontSize, 0.0001,
          "at 0 % the crit is the size the hit would have been - that is what the dial's floor promises");

        FctScale.Crit = FctScale.CritSizeDefault;
        Assert.AreEqual(takenCrit.ValueFontSize, plainTaken.ValueFontSize * FctScale.CritSizeDefault, 0.0001,
          "the shipped +10 % is ten percent over the hit's own size, not over some tier");

        var healCrit = ingest.Accept(_hits, FctLane.HealingReceived, 4_000, "Complete Heal", true, false, false, null, Width, Height, 30);
        Assert.AreEqual(FctStyle.DamageTakenFontSize * FctScale.CritSizeDefault, takenCrit.ValueFontSize, 0.0001,
          "the kind is damage-taken, so it rides the damage-taken tier - the pooled crit lane contributes colour and draw order, not a font");
        Assert.AreEqual(FctStyle.HealingFontSize * FctScale.CritSizeDefault, healCrit.ValueFontSize, 0.0001,
          "and a heal crit rides the healing tier, smaller than the taken crit beside it - each near its own stream's scale");
      }
      finally
      {
        FctScale.Crit = crit;
      }
    }

    [TestMethod]
    public void LabelsSpawnAsTheirOwnText()
    {
      var ingest = NewIngest();

      var hit = ingest.Accept(_hits, FctLane.Missed, 0, "Backstab", crit: false, minor: true, periodic: false, fixedText: Labels.Dodge, Width, Height, 0);

      Assert.IsNotNull(hit);
      Assert.AreEqual(Labels.Dodge, hit.DisplayText);
      Assert.AreEqual(FctLane.Missed, hit.Lane);

      // a label must never be folded into (or absorb into) a numeric hit
      Assert.IsFalse(ingest.Accept(_hits, FctLane.Missed, 0, "Backstab", crit: false, minor: true, periodic: false, fixedText: Labels.Dodge, Width, Height, 50) is null);
    }

    /* Identical ticks are the case folding exists for: one number, still reading as one tick's worth, saying how many. */
    [TestMethod]
    public void IdenticalTicksBecomeOneNumberWithACount()
    {
      var ingest = NewIngest();

      ingest.Accept(_hits, FctLane.DamageDealt, 200, "Immolation", crit: false, minor: true, periodic: true, fixedText: null, Width, Height, 0);

      for (var now = 200.0; now < 1200; now += 200)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 200, "Immolation", crit: false, minor: true, periodic: true, fixedText: null, Width, Height, now);
      }

      // one spawn plus five folded ticks
      Assert.AreEqual(1, _hits.Count);
      Assert.AreEqual(6, _hits[0].MergeCount, "six ticks, one number");
      Assert.AreEqual(200, _hits[0].Value, 0.001, "and it still reads as one tick, not as 1,200 of something");
      Assert.AreEqual("200 ×6", _hits[0].DisplayText);
    }

    [TestMethod]
    public void HealingStaysOneNumberPerCast()
    {
      var ingest = NewIngest();

      for (var now = 0.0; now < 3000; now += 500)
      {
        ingest.Accept(_hits, FctLane.HealingDealt, 1500, "Healing Word", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, now);
      }

      // players read heals individually; folding them hides who got patched and for how much. These six are the same size
      // on purpose: identical is not close enough, a heal never joins another heal's number
      Assert.AreEqual(6, _hits.Count);
      foreach (var hit in _hits)
      {
        Assert.AreEqual(1, hit.MergeCount, "six casts of 1,500 stay six numbers");
        Assert.AreEqual("+1,500", hit.DisplayText, "a heal reads as a plus, whatever band it floats in");
      }
    }

    [TestMethod]
    public void HealingNumbersKeepThePlusEvenWhenPooledAsCrits()
    {
      var ingest = NewIngest();

      var heal = ingest.Accept(_hits, FctLane.HealingReceived, 9409, "Healing Word", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0);
      var critHeal = ingest.Accept(_hits, FctLane.HealingDealt, 3200, "Chain Lightning", crit: true, minor: false, periodic: false, fixedText: null, Width, Height, 100);

      Assert.AreEqual("+9,409", heal.DisplayText);
      Assert.AreEqual(FctLane.Crit, critHeal.Lane, "a heal crit pools with the other crits");
      Assert.AreEqual("+3,200", critHeal.DisplayText, "the pooled lane no longer says it is a heal; the sign still does");
    }

    /*
     * What folding is not for. A small hit used to be poured into any live number of its lane because it looked like routine
     * noise against the lane's running median, which put 60 inside a 600 and left a 660 on screen that no hit ever landed.
     * Only an identical number may absorb one now, so different amounts stay visible as themselves.
     */
    [TestMethod]
    public void ASmallHitIsNotPouredIntoABiggerOneOfTheSameAbility()
    {
      var ingest = NewIngest();

      for (var now = 0.0; now < 900; now += 100)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 600, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, now);
      }

      var before = _hits.Count;
      var small = ingest.Accept(_hits, FctLane.DamageDealt, 60, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 1000);

      Assert.IsNotNull(small, "there is no median gate any more, and nothing lands inside another number");
      Assert.AreEqual(before + 1, _hits.Count);
      Assert.AreEqual(60, small.Value, 0.001);
      Assert.AreEqual(1, small.MergeCount, "and it is one hit, which is what it is");
    }

    /*
     * The overload contract has two halves, and the tests for them are separate on purpose: normal numbers must
     * never be lost while a hit of their lane is still alive to take them, and the case where nothing can take
     * them (crits, which refuse to absorb) has to be counted instead of silently vanishing.
     */
    [TestMethod]
    public void SustainedOverloadMergesInsteadOfLosingDamage()
    {
      var ingest = NewIngest();
      var now = 0.0;

      // 60 ms apart is faster than any lane can display, and it goes on long enough that every early hit ages
      // past the normal absorb window: a merge policy that only accepts young targets would start dropping here
      for (var i = 0; i < 40; i++, now += 60)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 900, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, now);
      }

      Assert.IsTrue(_hits.Count <= 12, $"lane cap was not enforced ({_hits.Count} live)");
      Assert.AreEqual(0, ingest.DroppedCount, "while a hit of the lane is alive, nothing may be lost");

      // every hit after the first is a count on a number that is still on screen: all 40 are accounted for
      Assert.AreEqual(40, Represented(_hits), "the counts have to add up to what actually landed");
      foreach (var hit in _hits)
      {
        Assert.AreEqual(900, hit.Value, 0.001, "and none of them got any bigger than the hit it stands for");
      }
    }

    [TestMethod]
    public void CritOverloadIsCountedBecauseCritsNeverAbsorb()
    {
      var ingest = NewIngest();
      var now = 0.0;

      for (var i = 0; i < 20; i++, now += 60)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 900, "Flurry", crit: true, minor: false, periodic: false, fixedText: null, Width, Height, now);
      }

      Assert.AreEqual(12, _hits.Count, "the crit lane is capped and nothing merged into a crit");
      Assert.AreEqual(8, ingest.DroppedCount, "lost crits have to show up in the header, not silently disappear");
    }

    /*
     * The fold key. Lane alone was what this matched on, which let one number accumulate totals that were not its own and
     * then say something untrue about where they came from — an Immolation tick growing a number labelled "Spinning
     * Attack", or two DoTs taken collapsing into whichever landed first with the other one vanishing from the log entirely.
     * NAG folds only into a component with identical flags for exactly this reason, and Mik's Scrolling Battle Text merges
     * only on matching event type *and* skill name. Pinned per-kind because each pair is a real mistake a reader could be
     * left with, not a theoretical one.
     */
    [TestMethod]
    public void TicksOfTwoDifferentDoTsNeverShareANumber()
    {
      var ingest = NewIngest();

      ingest.Accept(_hits, FctLane.DamageTaken, 400, "Immolation", crit: false, minor: true, periodic: true, fixedText: null, Width, Height, 0);
      ingest.Accept(_hits, FctLane.DamageTaken, 350, "Burn", crit: false, minor: true, periodic: true, fixedText: null, Width, Height, 100);
      ingest.Accept(_hits, FctLane.DamageTaken, 400, "Immolation", crit: false, minor: true, periodic: true, fixedText: null, Width, Height, 200);

      Assert.AreEqual(2, _hits.Count, "each ability keeps its own number");
      Assert.AreEqual(400, OnlyWith("Immolation").Value, 0.001, "counting its own ticks over itself...");
      Assert.AreEqual(2, OnlyWith("Immolation").MergeCount, "...which is two of them, not a total of 800");
      Assert.AreEqual(350, OnlyWith("Burn").Value, 0.001, "and the other DoT stays on screen at all");
    }

    [TestMethod]
    public void AProcNeverInflatesTheSwingItLandedOn()
    {
      var ingest = NewIngest();

      // ordinary swings first, so 40 is "routine" for the lane — the size test alone would happily fold it
      for (var now = 0.0; now < 900; now += 100)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 1500, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, now);
      }

      var swing = _hits[^1];
      var proc = ingest.Accept(_hits, FctLane.DamageDealt, 40, "Zealot's Fury", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 950, proc: true);

      Assert.IsNotNull(proc, "a proc is a different ability: it gets its own number even when it is small");
      Assert.AreEqual(40, proc.Value, 0.001);
      Assert.AreEqual(1500, swing.Value, 0.001, "and the swing beside it stays what it was");
    }

    /* A tick must not fold into a direct hit either, or the melee number becomes the sum of a swing and a DoT. */
    [TestMethod]
    public void ADoTTickDoesNotGrowAMeleeNumber()
    {
      var ingest = NewIngest();

      var swing = ingest.Accept(_hits, FctLane.DamageDealt, 1500, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0);
      var tick = ingest.Accept(_hits, FctLane.DamageDealt, 900, "Immolation", crit: false, minor: true, periodic: true, fixedText: null, Width, Height, 100);

      Assert.IsNotNull(tick);
      Assert.AreEqual(1500, swing.Value, 0.001, $"the swing still reads {swing.Value:0}");
      Assert.AreEqual(1, swing.MergeCount);
      Assert.AreEqual("Immolation", tick.Source);
    }

    /*
     * The cap fight, which is the reason this policy exists: at LaneCap the old code folded whatever arrived into whatever
     * was newest, so a 45,000 cast disappeared into a 300 swing that then read as the biggest number on screen. Slotting is
     * now a comparison, so the important number is the one that stays visible.
     */
    [TestMethod]
    public void ABigCastIsNotHiddenBehindTwelveRoutineSwings()
    {
      var ingest = NewIngest();

      for (var i = 0; i < 12; i++)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 300 + i, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, i * 30);
      }

      var big = ingest.Accept(_hits, FctLane.DamageDealt, 45_000, "Meteor Storm", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 600);

      Assert.IsNotNull(big, "the biggest number of the fight has to be on screen");
      Assert.AreEqual(45_000, big.Value, 0.001, "as its own number, not inside somebody else's");
      Assert.AreEqual("Meteor Storm", big.Source);
      Assert.AreEqual(0, ingest.DroppedCount, "it took a slot rather than being dropped");
      Assert.AreEqual(12, _hits.Count, "and the lane is still capped");

      foreach (var hit in _hits)
      {
        if (hit != big)
        {
          Assert.IsTrue(hit.Value < 1000, $"a routine swing came back reading {hit.Value:0}");
        }
      }
    }

    [TestMethod]
    public void AnEvictedHitReleasesWhatTheBackendKeptForIt()
    {
      var ingest = NewIngest();

      for (var i = 0; i < 12; i++)
      {
        ingest.Accept(_hits, FctLane.HealingDealt, 900 + (i * 10), "Healing Word", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, i * 40);
      }

      var smallest = _hits[0];
      var released = new List<FctHitState>();
      var big = ingest.Accept(_hits, FctLane.HealingDealt, 8000, "Prayer of Fealty", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 600, proc: false, evicting: released.Add);

      Assert.IsNotNull(big, "a heal nobody would choose to miss is worth a slot");
      Assert.AreEqual(1, released.Count, "the hit that gave it up has to be reported so glyphs and sprites go with it");
      Assert.AreSame(smallest, released[0], "and the cheapest slot is the least significant number on screen");
    }

    /*
     * Healing is the exception that proves the key: heals never fold, at any occupancy, because a player reads them one
     * cast at a time — who got patched, for how much. Which means a thirteenth heal either takes a slot or is counted as
     * dropped; what it must never do is grow a heal that already on screen.
     */
    [TestMethod]
    public void AThirteenthHealNeverGrowsSomebodyElsesNumber()
    {
      var ingest = NewIngest();

      for (var i = 0; i < 12; i++)
      {
        ingest.Accept(_hits, FctLane.HealingDealt, 900 + (i * 10), "Healing Word", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, i * 40);
      }

      var before = TotalOf(_hits);
      ingest.Accept(_hits, FctLane.HealingDealt, 8000, "Prayer of Fealty", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 600);

      Assert.AreEqual(11, CountWith("Healing Word"), "one small heal made room");
      Assert.AreEqual(before - 900 + 8000, TotalOf(_hits), 0.001, "and the visible total moved by exactly what landed and what left");
    }

    /*
     * A stream of identical hits cannot fill a lane at all: the fold is tried before the cap and does not care about
     * occupancy, so thirteen swings of the same amount are one number counting up in place, and the twelve slots stay free
     * for the different numbers that actually need somewhere to go.
     */
    [TestMethod]
    public void AStreamOfIdenticalHitsNeverReachesTheLaneCap()
    {
      var ingest = NewIngest();

      for (var i = 0; i < 13; i++)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 900, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, i * 30);
      }

      Assert.AreEqual(1, _hits.Count, "one number for thirteen identical swings");
      Assert.AreEqual(13, _hits[0].MergeCount);
      Assert.AreEqual(0, ingest.DroppedCount, "and nothing lost on the way");
      Assert.AreEqual("900 ×13", _hits[0].DisplayText);
    }

    /* ...and taking a slot is not a licence to shuffle: an occupant nearly as good as the newcomer keeps it. */
    [TestMethod]
    public void AFullLaneDoesNotShuffleForAMarginalNumber()
    {
      var ingest = NewIngest();

      // twelve different amounts, because a lane of identical ones is one number and would not be full at all
      for (var i = 0; i < 12; i++)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 900 + i, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, i * 30);
      }

      // a different ability, so folding is out; 1.3× the weakest occupant is not the clear win eviction asks for
      var marginal = ingest.Accept(_hits, FctLane.DamageDealt, 1200, "Backstab", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 600);

      Assert.IsNull(marginal, "this is the case the drop counter exists for");
      Assert.AreEqual(1, ingest.DroppedCount);
      Assert.AreEqual(12, CountWith("Flurry"), "nothing already on screen moved to make room");
    }

    /*
     * Direction is the whole point of the layout, so it is pinned here rather than left to the renderer: incoming lives
     * below the protected strip and travels down, outgoing above it and up. A regression turns into "which way was mine?"
     * in game while staying invisible to any test of the renderers themselves.
     */
    [TestMethod]
    public void BandsModePutsIncomingBelowAndOutgoingAboveTheStrip()
    {
      var ingest = NewIngest();

      var outgoing = ingest.Accept(_hits, FctLane.DamageDealt, 500, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0);
      var incoming = ingest.Accept(_hits, FctLane.DamageTaken, 500, "Bites", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 10);

      Assert.IsTrue(outgoing.Rise > 0, $"outgoing text must rise (Rise {outgoing.Rise})");
      Assert.IsTrue(outgoing.BandMaxY <= (Height * FctLayout.GapTopFrac) + 0.001, "outgoing band must stop at the protected strip");
      Assert.IsTrue(incoming.Rise < 0, $"incoming text must sink (Rise {incoming.Rise})");
      Assert.IsTrue(incoming.BandMinY >= (Height * FctLayout.GapBottomFrac) - 0.001, "incoming band must start below the protected strip");
    }


    /*
     * Fountain motion on the incoming band is mirrored, not dropped: the fall pulls back up toward the gap instead of
     * down into the bottom of the overlay, where a literal gravity tail would park the number for half its life.
     * Three things have to hold at once — it still gets the choreography (a real fall distance), it never leaves its
     * band, and it never approaches the protected strip above it.
     */
    [TestMethod]
    public void FountainOnTheIncomingBandMirrorsUpwardAndStaysInsideIt()
    {
      var ingest = NewIngest();
      ingest.Style = FctMotionStyle.Fountain;
      var gapBottom = Height * FctLayout.GapBottomFrac;

      var incoming = ingest.Accept(_hits, FctLane.DamageTaken, 500, "Bites", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0);

      Assert.IsTrue(incoming.FallDist < 0, $"incoming fall must run back up toward the gap, was {incoming.FallDist:0.#}");
      Assert.AreEqual(FctMotion.MotionWindowMs, incoming.LifetimeMs, "fountain life is the choreography, not an adaptive hold");

      var lowest = incoming.Y0 - incoming.Rise;   // Rise is negative: this is below the spawn point
      for (var t = 0.0; t <= 1.0; t += 0.05)
      {
        var y = FctMotion.RaisedY(incoming, t);
        Assert.IsTrue(y >= gapBottom - 0.001, $"incoming hit climbed into the protected strip at t={t:0.00} (y {y:0.#}, gap {gapBottom:0.#})");
        Assert.IsTrue(y <= lowest + 0.001, $"incoming hit sank past its own deepest point at t={t:0.00}");
      }
    }

    /* The outgoing band keeps the literal fall: up, over, and down toward the strip (the clamp, not luck, is what
     * keeps it out of the gap). Both sides of the mirror are pinned so a "cleanup" cannot silently delete one. */
    [TestMethod]
    public void FountainOnTheOutgoingBandFallsDownward()
    {
      var ingest = NewIngest();
      ingest.Style = FctMotionStyle.Fountain;
      var gapTop = Height * FctLayout.GapTopFrac;

      var outgoing = ingest.Accept(_hits, FctLane.DamageDealt, 900, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0);

      Assert.AreEqual(Height * 0.28, outgoing.FallDist, 0.001, "the outgoing band keeps its literal gravity tail");
      Assert.IsTrue(FctMotion.RaisedY(outgoing, 1.0) > (outgoing.Y0 - outgoing.Rise), "outgoing fountain text comes back down");

      for (var t = 0.0; t <= 1.0; t += 0.05)
      {
        var y = FctMotion.RaisedY(outgoing, t);
        Assert.IsTrue(y + FctLayout.TextReserve(outgoing) <= gapTop + 0.001,
          $"outgoing hit fell into the protected strip at t={t:0.00} (bottom {y + FctLayout.TextReserve(outgoing):0.#}, gap top {gapTop:0.#})");
      }
    }

    /*
     * Pulse is the static style, and static text lives or dies by not overlapping, so every number gets a cell of its own:
     * distinct slots, no two numbers resting in the same place, nothing left moving once it has arrived. Asserted through
     * the ingest because allocation is what makes the style safe; the raw geometry has its own file (FctCellGridTest) and
     * the swell is pinned in FctMotionTest.
     */
    [TestMethod]
    public void PulseStyleGivesEveryNumberItsOwnCellAndThenStops()
    {
      var ingest = NewIngest();
      ingest.Style = FctMotionStyle.Pulse;
      var resting = new List<(double X, double Y)>();

      for (var i = 0; i < 6; i++)
      {
        var hit = ingest.Accept(_hits, FctLane.DamageDealt, 5000 - (i * 40), "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, i * 60);

        Assert.IsNotNull(hit, "distinct values must not fold into each other");
        Assert.IsTrue(hit.Cell >= 0, "pulse mode allocates a cell per number");
        Assert.AreEqual(0.0, hit.FallDist, "there is nothing to fall from");
        Assert.AreEqual(FctMotion.PulseSlideMs, hit.MotionMs, 0.001, "the slide into the cell is the whole animation");

        var rest = (X: FctMotion.ArcedX(hit, 1), Y: FctMotion.RaisedY(hit, 1));
        Assert.IsFalse(resting.Contains(rest), $"number {i} rests on top of an earlier one");
        resting.Add(rest);

        Assert.AreEqual(rest.Y, FctMotion.RaisedY(hit, FctMotion.Progress(hit, hit.LifetimeMs)), 0.001, "a number being read must not creep");
      }
    }

    /*
     * Spray exists to fan repeated hits out instead of stacking them into one unreadable column, and it does that by flying
     * sideways, which is what is measured here — every style gets a gentle sway, so total coverage is not the question. It used
     * to be asserted as coverage against hold, and that stopped meaning anything once FctPlacement started spreading held
     * numbers into whatever room the band had: two mechanisms, similar footprints, one measurement that could not tell them
     * apart. What has to differ is the flight — spray's cone several times wider than the sway.
     */
    [TestMethod]
    public void SprayFansWiderThanHold()
    {
      var held = Sweep(FctMotionStyle.Hold);
      var sprayed = Sweep(FctMotionStyle.Spray);

      Assert.IsTrue(sprayed.Travel > held.Travel * 2,
        $"spray is supposed to fan out: hold sways {held.Travel:0} px sideways on average, spray travels {sprayed.Travel:0} px");
      Assert.IsTrue(sprayed.Width > Width * 0.55,
        $"the cone should reach most of the overlay width, covered {sprayed.Width:0} px");
    }

    /*
     * Every style owes the same two promises; sweep asserts them for all of them alike. The parabola is in on purpose: in bands
     * ingest degrades it to hold (FctIngest), and this is the test that proves the degradation keeps the strip clear rather
     * than merely being present.
     */
    [TestMethod]
    public void EveryMotionStyleKeepsTheStripClearAndTheWindowInside()
    {
      foreach (var style in new[] { FctMotionStyle.Hold, FctMotionStyle.Fountain, FctMotionStyle.Pulse, FctMotionStyle.Spray, FctMotionStyle.Parabola })
      {
        var coverage = Sweep(style);

        Assert.IsTrue(coverage.MinX >= FctLayout.EdgePad - 0.001, $"{style} drew past the left edge ({coverage.MinX:0.#})");
        Assert.IsTrue(coverage.MaxX <= Width - FctLayout.EdgePad + 0.001, $"{style} drew past the right edge ({coverage.MaxX:0.#})");
      }
    }

    /* Spray on the lower band mirrors like fountain does: it fans downward and its gravity pulls back up. */
    [TestMethod]
    public void SprayOnTheIncomingBandFansDownwardAndFallsBackUp()
    {
      var ingest = NewIngest();
      ingest.Style = FctMotionStyle.Spray;
      var gapBottom = Height * FctLayout.GapBottomFrac;

      for (var i = 0; i < 8; i++)
      {
        var hit = ingest.Accept(_hits, FctLane.DamageTaken, 5000 - (i * 40), "Bites", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, i * 60);

        Assert.IsNotNull(hit);
        Assert.IsTrue(hit.Rise < 0, $"incoming spray must travel away from the gap, downward (Rise {hit.Rise:0.#})");
        Assert.IsTrue(hit.FallDist < 0, "and its fall must mirror back up toward the gap, not down to the window edge");

        // the invariant that makes the mirror safe: it can never fall further than it sank
        Assert.IsTrue(Math.Abs(hit.FallDist) < Math.Abs(hit.Rise), $"fell {hit.FallDist:0.#} after sinking {hit.Rise:0.#}");

        var lowest = hit.Y0 - hit.Rise;
        for (var t = 0.0; t <= 1.0; t += 0.05)
        {
          var y = FctMotion.RaisedY(hit, t);
          Assert.IsTrue(y >= gapBottom - 0.001, $"incoming spray climbed into the protected strip at t={t:0.00}");
          Assert.IsTrue(y <= lowest + 0.001, $"incoming spray sank past its own deepest point at t={t:0.00}");
        }
      }
    }

    /*
     * The two choreographed styles share one timing shape — life equals the motion window, fade spans exactly the fall —
     * while the two that stop and hold keep the adaptive lifetime. Getting this wrong is invisible in maths and obvious
     * on screen: a sprayed number whose life outlasts its travel sits still for a second in mid-air.
     */
    [TestMethod]
    public void ChoreographySetsTheLifeAndHoldStylesDoNot()
    {
      foreach (var (style, window) in new[] { (FctMotionStyle.Fountain, FctMotion.MotionWindowMs), (FctMotionStyle.Spray, FctMotion.SprayMotionWindowMs) })
      {
        var ingest = NewIngest();
        ingest.Style = style;

        var hit = ingest.Accept(new List<FctHitState>(), FctLane.DamageDealt, 900, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0);

        Assert.AreEqual(window, hit.LifetimeMs, $"{style} life must be its choreography");
        Assert.AreEqual(window, hit.MotionMs, $"{style} must not hold mid-flight");
        Assert.AreEqual(window * FctMotion.FallPhaseFrac, hit.FadeMs, 0.001, $"{style} fades over its fall");
      }

      foreach (var style in new[] { FctMotionStyle.Hold, FctMotionStyle.Pulse })
      {
        var ingest = NewIngest();
        ingest.Style = style;

        var hit = ingest.Accept(new List<FctHitState>(), FctLane.DamageDealt, 900, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0);

        Assert.IsTrue(hit.LifetimeMs > FctMotion.MotionWindowMs, $"{style} keeps the adaptive life (was {hit.LifetimeMs:0})");
      }

      // hold spends the window travelling and then holds; pulse spends a fraction of it sliding into its cell
      var hold = NewIngest();
      hold.Style = FctMotionStyle.Hold;
      var heldHit = hold.Accept(new List<FctHitState>(), FctLane.DamageDealt, 900, "Flurry", false, false, false, null, Width, Height, 0);
      Assert.AreEqual(FctMotion.MotionWindowMs, heldHit.MotionMs, 0.001, "hold travels inside the motion window, then holds");
    }

    /*
     * A proc lands on top of the swing the player aimed, and items fire them constantly — so it runs a shorter life.
     * It does NOT run a smaller font: shrinking procs below their lane read as two weights of information rather than
     * two urgencies, and the tempo tier carries the subordination on its own. The size half of this test is pinned on
     * purpose too — it is the half that was reversed.
     */
    [TestMethod]
    public void ProcsAreQuickerNotSmallerThanTheHitThatProvokedThem()
    {
      var plain = NewIngest().Accept(new List<FctHitState>(), FctLane.DamageDealt, 1000, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0);
      var proc = NewIngest().Accept(new List<FctHitState>(), FctLane.DamageDealt, 1000, "Soul Strike", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0, proc: true);

      Assert.AreEqual(FctStyle.DamageDealtFontSize * FctScale.Text, proc.ValueFontSize, 0.001);
      Assert.AreEqual(plain.ValueFontSize, proc.ValueFontSize, 0.001, "a proc is its lane's full size; tempo alone subordinates it");
      Assert.AreEqual(plain.SourceFontSize, proc.SourceFontSize, 0.001, "and the ability line matches");
      Assert.IsTrue(proc.LifetimeMs < plain.LifetimeMs, $"a proc clears sooner (was {proc.LifetimeMs:0} vs {plain.LifetimeMs:0})");
      Assert.IsTrue(proc.MotionMs < plain.MotionMs, "the whole tempo shortens, not just the tail");
      Assert.IsTrue(proc.FadeMs < plain.FadeMs, "and it fades proportionally, or it lingers while shrinking");
    }

    /*
     * A crit proc keeps everything a crit gets. Two separate rules quietly reducing emphasis would otherwise make the
     * biggest single number in the log - a proc crit - the quietest thing on screen, which is the opposite of what the
     * pop is for.
     */
    [TestMethod]
    public void ProcCritsKeepTheirSizeAndTheirLife()
    {
      var crit = NewIngest().Accept(new List<FctHitState>(), FctLane.DamageDealt, 3000, "Flurry", crit: true, minor: false, periodic: false, fixedText: null, Width, Height, 0);
      var procCrit = NewIngest().Accept(new List<FctHitState>(), FctLane.DamageDealt, 3000, "Soul Strike", crit: true, minor: false, periodic: false, fixedText: null, Width, Height, 0, proc: true);

      Assert.AreEqual(FctLane.Crit, procCrit.Lane);
      Assert.AreEqual(crit.ValueFontSize, procCrit.ValueFontSize, 0.001, "crit size is not reduced for anybody");
      Assert.AreEqual(crit.LifetimeMs, procCrit.LifetimeMs, 0.001, "and neither is its life");
      Assert.AreEqual(crit.MotionMs, procCrit.MotionMs, 0.001);
    }

    /* A choreographed style scales as a unit: travel and fall share one lifetime, so shortening the life without the
     * motion would leave a proc holding its parabola in slow motion. */
    [TestMethod]
    public void ProcTempoShortensTheWholeChoreography()
    {
      var ingest = NewIngest();
      ingest.Style = FctMotionStyle.Fountain;

      var proc = ingest.Accept(new List<FctHitState>(), FctLane.DamageDealt, 1000, "Soul Strike", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0, proc: true);

      Assert.AreEqual(FctMotion.MotionWindowMs * FctMotion.ProcTimeFrac, proc.LifetimeMs, 0.001);
      Assert.AreEqual(proc.LifetimeMs, proc.MotionMs, 0.001, "still one continuous motion, just quicker");
      Assert.AreEqual(FctMotion.MotionWindowMs * FctMotion.ProcTimeFrac * FctMotion.FallPhaseFrac, proc.FadeMs, 0.001);
    }

    [TestMethod]
    public void ExpiredHitsArePrunedWithTheirBackendAttachments()
    {
      var ingest = NewIngest();

      // distinct amounts on purpose: identical ones would fold into a single counted number and this test is about
      // five live numbers leaving, not about folding
      for (var i = 0; i < 5; i++)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 500 + (i * 10), "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, i * 100);
      }

      var removed = new List<FctHitState>();
      Assert.AreEqual(0, ingest.PruneExpired(_hits, 500, removed.Add));
      Assert.AreEqual(5, _hits.Count);

      // lifetimes are adaptive (3.5 s baseline here), so everything is gone well past that
      Assert.AreEqual(5, ingest.PruneExpired(_hits, 10_000, removed.Add));
      Assert.AreEqual(0, _hits.Count);
      Assert.AreEqual(5, removed.Count, "the backend must get a chance to release per-hit resources");
    }

    [TestMethod]
    public void NothingSpawnsBeforeTheCanvasHasASize()
    {
      var ingest = NewIngest();

      Assert.IsNull(ingest.Accept(_hits, FctLane.DamageDealt, 500, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, 0, 0, 0));
      Assert.AreEqual(0, _hits.Count);
      Assert.AreEqual(0, ingest.DroppedCount, "an unmeasured window is not an overload");
    }

    /*
     * One style's worth of an outgoing burst, asserting as it goes that nothing enters the protected strip or leaves the
     * window horizontally, and reporting how much x the drawn text covered. Spacing is wide (900 ms) so hits overlap the
     * way a slow fight does rather than tripping the lane cap, which would test dropping instead of geometry.
     */
    private (double Width, double MinX, double MaxX, double Travel) Sweep(FctMotionStyle style)
    {
      var hits = new List<FctHitState>();
      var ingest = NewIngest();
      ingest.Style = style;

      var gapTop = Height * FctLayout.GapTopFrac;
      var minX = double.MaxValue;
      var maxX = double.MinValue;
      var travel = 0.0;
      var flown = 0;

      for (var i = 0; i < 40; i++)
      {
        // exactly what a canvas does every tick: without this the list fills to the lane cap and the burst stops
        // spawning, which measures twelve samples of the random angle instead of the style
        ingest.PruneExpired(hits, i * 900);

        var hit = ingest.Accept(hits, FctLane.DamageDealt, 5000 - (i * 31), "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, i * 900);
        if (hit is null)
        {
          continue; // folded into a live number: real behaviour, just not this test's subject
        }

        travel += Math.Abs(FctMotion.ArcedX(hit, 1) - FctMotion.ArcedX(hit, 0));
        flown++;

        for (var t = 0.0; t <= 1.0; t += 0.05)
        {
          var x = FctMotion.ArcedX(hit, t);
          var y = FctMotion.RaisedY(hit, t);

          minX = Math.Min(minX, x - (hit.ValueWidth / 2.0));
          maxX = Math.Max(maxX, x + (hit.ValueWidth / 2.0));

          Assert.IsTrue(y + FctLayout.TextReserve(hit) <= gapTop + 0.001,
            $"{style} put text into the protected strip at t={t:0.00} (bottom {y + FctLayout.TextReserve(hit):0.#}, gap top {gapTop:0.#})");
        }
      }

      return (maxX - minX, minX, maxX, flown == 0 ? 0 : travel / flown);
    }

    private FctHitState OnlyWith(string source)
    {
      foreach (var hit in _hits)
      {
        if (hit.Source == source)
        {
          return hit;
        }
      }

      Assert.Fail($"no live number labelled {source}");
      return null; // unreachable; Assert.Fail throws
    }

    private int CountWith(string source)
    {
      var found = 0;
      foreach (var hit in _hits)
      {
        if (hit.Source == source)
        {
          found++;
        }
      }

      return found;
    }

    /* What the numbers on screen add up to: every face value once per hit it stands for. */
    private static double TotalOf(List<FctHitState> hits)
    {
      var total = 0.0;
      foreach (var hit in hits)
      {
        total += hit.Value * hit.MergeCount;
      }

      return total;
    }

    /* How many separate hits the visible numbers stand for. */
    private static int Represented(List<FctHitState> hits)
    {
      var count = 0;
      foreach (var hit in hits)
      {
        count += hit.MergeCount;
      }

      return count;
    }
  }
}