using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The accumulation switch, which is the door rather than the policy: OFF (as it ships) every event keeps its own row and
   * folding is never consulted; ON, an event identical to a number already flying joins it with a count - "412 ×3" where
   * three 412s used to fly, words stack too ("Miss ×2"), crits stack only onto crits, and marks and direct heals never
   * stack at all. The key itself, the age rules and every never are pinned by FctIngestTest, FctSpecialTest and friends,
   * which run with the switch explicitly open; this file is about the door and the two policies that changed when it
   * arrived: crits found their pool, and words learned to count.
   */
  [TestClass]
  public sealed class FctAccumulationTest
  {
    [TestInitialize]
    public void ResetPlayerScale()
    {
      FctLayout.LabelSide = FctAmbient.LabelSideDefault;
      FctScale.Text = FctScale.SizeDefault;
      FctScale.Crit = FctScale.CritSizeDefault;
      FctScale.Time = 1;
    }

    private const double Width = 980;
    private const double Height = 640;

    private static FctIngest Ingest() => new(new Random(20_260_731)) { Layout = FctLayoutChoice.Bands };

    [TestMethod]
    public void TheSwitchShipsOffAndEveryHitKeepsItsOwnRow()
    {
      var ingest = Ingest();
      var hits = new List<FctHitState>();

      for (var now = 0.0; now < 600; now += 100)
      {
        Assert.IsNotNull(ingest.Accept(hits, FctLane.DamageDealt, 412, "Immolation", crit: false, minor: true, periodic: true,
          fixedText: null, Width, Height, now), "off means the door is shut: six ticks, six rows");
      }

      Assert.AreEqual(6, hits.Count);
      foreach (var hit in hits)
      {
        Assert.AreEqual(1, hit.MergeCount);
        Assert.AreEqual("412", hit.DisplayText, "and nothing wears a count nobody asked for");
      }
    }

    [TestMethod]
    public void TurningItOnStacksTheIdenticalStream()
    {
      var ingest = Ingest();
      ingest.Accumulate = true;
      var hits = new List<FctHitState>();

      var first = ingest.Accept(hits, FctLane.DamageDealt, 412, "Immolation", crit: false, minor: true, periodic: true,
        fixedText: null, Width, Height, 0);
      Assert.IsNotNull(first);

      for (var now = 100.0; now < 600; now += 100)
      {
        Assert.IsNull(ingest.Accept(hits, FctLane.DamageDealt, 412, "Immolation", crit: false, minor: true, periodic: true,
          fixedText: null, Width, Height, now), "the row already flying takes the next identical tick");
      }

      Assert.AreEqual(6, first.MergeCount);
      Assert.AreEqual("412 ×6", first.DisplayText, "the face value stays one tick's; only the count moves");
    }

    /* Words by the yard: the count is the whole message and no fact is lost to it - four identical evasions are one
       fact said four times, and a different word is a different sentence that never joins the count. */
    [TestMethod]
    public void WordsStackTheirCountToo()
    {
      var ingest = Ingest();
      ingest.Accumulate = true;
      var hits = new List<FctHitState>();

      var first = ingest.Accept(hits, FctLane.Missed, 0, "Flurry", crit: false, minor: false, periodic: false,
        fixedText: Labels.Miss, Width, Height, 0);
      Assert.IsNotNull(first);
      Assert.AreEqual(Labels.Miss, first.DisplayText, "one word says no more than the word");

      Assert.IsNull(ingest.Accept(hits, FctLane.Missed, 0, "Flurry", crit: false, minor: false, periodic: false,
        fixedText: Labels.Miss, Width, Height, 300));
      Assert.IsNull(ingest.Accept(hits, FctLane.Missed, 0, "Flurry", crit: false, minor: false, periodic: false,
        fixedText: Labels.Miss, Width, Height, 600));

      Assert.AreEqual(3, first.MergeCount);
      Assert.AreEqual($"{Labels.Miss} ×3", first.DisplayText);

      var dodge = ingest.Accept(hits, FctLane.Missed, 0, "Flurry", crit: false, minor: false, periodic: false,
        fixedText: Labels.Dodge, Width, Height, 700);
      Assert.IsNotNull(dodge, "a dodge beside three misses is two sentences, not one count of three-and-one");
      Assert.AreEqual(2, hits.Count);
    }

    /* Crits found their pool: identical crits stack onto each other - honest ledgers, since the pool keeps them off every
       ordinary row and back - while a plain hit of the same amount and ability is a different class of event entirely,
       and whose story a crit tells (dealt vs taken) is part of its key. */
    [TestMethod]
    public void CritsStackOnlyOntoCrits()
    {
      var ingest = Ingest();
      ingest.Accumulate = true;
      var hits = new List<FctHitState>();

      var crit = ingest.Accept(hits, FctLane.DamageDealt, 1700, "Backstab", crit: true, minor: false, periodic: false,
        fixedText: null, Width, Height, 0);
      Assert.IsNotNull(crit);
      Assert.AreEqual(FctLane.Crit, crit.Lane, "the stack lives in the crit pool");

      Assert.IsNull(ingest.Accept(hits, FctLane.DamageDealt, 1700, "Backstab", crit: true, minor: false, periodic: false,
        fixedText: null, Width, Height, 100), "an identical crit folds onto its twin");
      Assert.AreEqual(2, crit.MergeCount);

      var plain = ingest.Accept(hits, FctLane.DamageDealt, 1700, "Backstab", crit: false, minor: false, periodic: false,
        fixedText: null, Width, Height, 200);
      Assert.IsNotNull(plain, "the same number without the crit is a different event - it keeps its own row");
      Assert.AreEqual(FctLane.DamageDealt, plain.Lane);

      var taken = ingest.Accept(hits, FctLane.DamageTaken, 1700, "Backstab", crit: true, minor: false, periodic: false,
        fixedText: null, Width, Height, 300);
      Assert.IsNotNull(taken, "a taken crit shares the pool but not the story; it never joins the dealt stack");
      Assert.AreEqual(2, crit.MergeCount, "and the stack is still two");
    }

    /* The nevers hold with the door wide open: a direct heal is read one cast at a time, and a marked event counted
       away is the event itself lost. (The same rules re-pinned under fold-key pressure live in FctIngestTest and
       FctSpecialTest.) */
    [TestMethod]
    public void HealsAndMarksNeverStackEvenWithTheSwitchOn()
    {
      var ingest = Ingest();
      ingest.Accumulate = true;
      var hits = new List<FctHitState>();

      for (var now = 0.0; now < 1500; now += 300)
      {
        Assert.IsNotNull(ingest.Accept(hits, FctLane.HealingDealt, 800, "Healing Word", crit: false, minor: false, periodic: false,
          fixedText: null, Width, Height, now), "five casts of the identical heal stay five numbers");
      }

      var mark = ingest.Accept(hits, FctLane.DamageDealt, 3000, "Backstab", crit: false, minor: false, periodic: false,
        fixedText: null, Width, Height, 2000, special: FctSpecial.Assassinate);
      Assert.IsNotNull(mark);
      var markAgain = ingest.Accept(hits, FctLane.DamageDealt, 3000, "Backstab", crit: false, minor: false, periodic: false,
        fixedText: null, Width, Height, 2100, special: FctSpecial.Assassinate);
      Assert.IsNotNull(markAgain, "two identical marks are two events; neither may be counted away");
    }
  }
}
