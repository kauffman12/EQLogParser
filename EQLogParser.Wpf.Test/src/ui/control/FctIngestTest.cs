using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * Ingest decides what happens to an incoming hit: fold it, spawn it, or lose it to the lane cap. That is
   * the policy that keeps a raid pull readable, so it is pinned here — totals must survive overload, periodic
   * ticks must collapse into one running number, and drops must be counted rather than silent.
   */
  [TestClass]
  public sealed class FctIngestTest
  {
    private const double Width = 980;
    private const double Height = 640;

    private readonly List<FctHitState> _hits = [];

    private static FctIngest NewIngest() => new(new Random(20_260_714));

    [TestMethod]
    public void FirstHitSpawnsOnItsOwnHalf()
    {
      var ingest = NewIngest();

      var hit = ingest.Accept(_hits, FctLane.DamageDealt, 500, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0, fountain: false);

      Assert.IsNotNull(hit);
      Assert.AreEqual(1, _hits.Count);
      Assert.AreEqual(FctLane.DamageDealt, hit.Lane);
      Assert.IsFalse(hit.LeftSide);
      Assert.AreEqual("Flurry", hit.Source);
      Assert.AreEqual("500", hit.DisplayText);
      Assert.IsTrue(hit.ValueWidth > 0, "spawn must seed a width estimate for the center clamp");
    }

    [TestMethod]
    public void CritsPoolOntoTheCritLaneButKeepTheirSide()
    {
      var ingest = NewIngest();

      var taken = ingest.Accept(_hits, FctLane.DamageTaken, 900, "Bites", crit: true, minor: false, periodic: false, fixedText: null, Width, Height, 0, fountain: false);
      var dealt = ingest.Accept(_hits, FctLane.DamageDealt, 900, "Flurry", crit: true, minor: false, periodic: false, fixedText: null, Width, Height, 10, fountain: false);

      // a crit I take must still appear on the incoming side, or the halves stop meaning anything
      Assert.AreEqual(FctLane.Crit, taken.Lane);
      Assert.IsTrue(taken.LeftSide);
      Assert.AreEqual(FctLane.Crit, dealt.Lane);
      Assert.IsFalse(dealt.LeftSide);
      Assert.IsTrue(taken.Blowout && dealt.Blowout);
    }

    [TestMethod]
    public void LabelsSpawnAsTheirOwnText()
    {
      var ingest = NewIngest();

      var hit = ingest.Accept(_hits, FctLane.Missed, 0, "Backstab", crit: false, minor: true, periodic: false, fixedText: Labels.Dodge, Width, Height, 0, fountain: false);

      Assert.IsNotNull(hit);
      Assert.AreEqual(Labels.Dodge, hit.DisplayText);
      Assert.AreEqual(FctLane.Missed, hit.Lane);

      // a label must never be folded into (or absorb into) a numeric hit
      Assert.IsFalse(ingest.Accept(_hits, FctLane.Missed, 0, "Backstab", crit: false, minor: true, periodic: false, fixedText: Labels.Dodge, Width, Height, 50, fountain: false) is null);
    }

    [TestMethod]
    public void PeriodicTicksFoldIntoOneRunningNumber()
    {
      var ingest = NewIngest();

      ingest.Accept(_hits, FctLane.DamageDealt, 200, "Immolation", crit: false, minor: true, periodic: true, fixedText: null, Width, Height, 0, fountain: false);

      for (var now = 200.0; now < 1200; now += 200)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 200, "Immolation", crit: false, minor: true, periodic: true, fixedText: null, Width, Height, now, fountain: false);
      }

      // one spawn plus five folded ticks
      Assert.AreEqual(1, _hits.Count);
      Assert.AreEqual(1200, _hits[0].TargetValue, 0.001);
      FctMotion.RefreshText(_hits[0], 1200);
      Assert.AreEqual("1,200", _hits[0].DisplayText);
    }

    [TestMethod]
    public void HealingStaysOneNumberPerCast()
    {
      var ingest = NewIngest();

      for (var now = 0.0; now < 3000; now += 500)
      {
        ingest.Accept(_hits, FctLane.HealingDealt, 1500, "Healing Word", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, now, fountain: false);
      }

      // players read heals individually; folding them hides who got patched and for how much
      Assert.AreEqual(6, _hits.Count);
    }

    [TestMethod]
    public void SmallDirectHitsCountUpOnALiveNumber()
    {
      var ingest = NewIngest();

      // seed the lane's median with ordinary hits first: 60 is small against that history
      for (var now = 0.0; now < 900; now += 100)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 600, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, now, fountain: false);
      }

      var before = _hits.Count;
      Assert.IsNull(ingest.Accept(_hits, FctLane.DamageDealt, 60, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 1000, fountain: false));
      Assert.AreEqual(before, _hits.Count);

      // the newest hit carries the count-up: it started at 600 and ends 60 higher
      var target = _hits[^1];
      Assert.IsTrue(target.CountUpMs > 0);
      Assert.AreEqual(660, target.TargetValue, 0.001);
      FctMotion.RefreshText(target, (1000 - target.SpawnMs) + target.CountUpMs);
      Assert.AreEqual("660", target.DisplayText);
    }

    [TestMethod]
    public void OverloadMergesBeforeItDropsAndCountsWhatItLoses()
    {
      var ingest = NewIngest();
      var now = 0.0;

      for (var i = 0; i < 40; i++, now += 60)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 900, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, now, fountain: false);
      }

      Assert.IsTrue(_hits.Count <= 12, $"lane cap was not enforced ({_hits.Count} live)");
      Assert.AreEqual(0, ingest.DroppedCount, "while numbers can still merge, nothing is lost — totals survive overload");

      // every hit after the 12th merged into a live number: the full 40 x 900 is still on screen
      Assert.AreEqual(40 * 900.0, TotalOf(_hits), 0.001);

      // now every live hit is too old to absorb into: at that point drops are counted, never silent
      now += 5000;
      for (var i = 0; i < 10; i++, now += 60)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 900, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, now, fountain: false);
      }

      Assert.AreEqual(10, ingest.DroppedCount);
      Assert.IsTrue(_hits.Count <= 12);
    }

    [TestMethod]
    public void ExpiredHitsArePrunedWithTheirBackendAttachments()
    {
      var ingest = NewIngest();

      for (var i = 0; i < 5; i++)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 500, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, i * 100, fountain: false);
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

      Assert.IsNull(ingest.Accept(_hits, FctLane.DamageDealt, 500, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, 0, 0, 0, fountain: false));
      Assert.AreEqual(0, _hits.Count);
      Assert.AreEqual(0, ingest.DroppedCount, "an unmeasured window is not an overload");
    }

    private static double TotalOf(List<FctHitState> hits)
    {
      var total = 0.0;
      foreach (var hit in hits)
      {
        total += hit.TargetValue;
      }

      return total;
    }
  }
}
