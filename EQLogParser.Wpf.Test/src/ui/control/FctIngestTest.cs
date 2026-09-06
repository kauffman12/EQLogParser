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
    public void FirstHitSpawnsOnItsSide()
    {
      var ingest = NewIngest();

      var hit = ingest.Accept(_hits, FctLane.DamageDealt, 500, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0, fountain: false);

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

      var taken = ingest.Accept(_hits, FctLane.DamageTaken, 900, "Bites", crit: true, minor: false, periodic: false, fixedText: null, Width, Height, 0, fountain: false);
      var dealt = ingest.Accept(_hits, FctLane.DamageDealt, 900, "Flurry", crit: true, minor: false, periodic: false, fixedText: null, Width, Height, 10, fountain: false);

      // a crit I take must still appear on the incoming side, or the region scheme stops meaning anything
      Assert.AreEqual(FctLane.Crit, taken.Lane);
      Assert.IsTrue(taken.Incoming);
      Assert.AreEqual(FctLane.Crit, dealt.Lane);
      Assert.IsFalse(dealt.Incoming);
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

      // the last fold happened at 1000 ms and counts up over CountUpMs: read the text after it settles
      FctMotion.RefreshText(_hits[0], 1000 + FctMotion.CountUpMs + 1);
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
        ingest.Accept(_hits, FctLane.DamageDealt, 900, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, now, fountain: false);
      }

      Assert.IsTrue(_hits.Count <= 12, $"lane cap was not enforced ({_hits.Count} live)");
      Assert.AreEqual(0, ingest.DroppedCount, "while a hit of the lane is alive, nothing may be lost");

      // every hit after the 12th merged into a live number: the full 40 x 900 is still on screen
      Assert.AreEqual(40 * 900.0, TotalOf(_hits), 0.001);
    }

    [TestMethod]
    public void CritOverloadIsCountedBecauseCritsNeverAbsorb()
    {
      var ingest = NewIngest();
      var now = 0.0;

      for (var i = 0; i < 20; i++, now += 60)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 900, "Flurry", crit: true, minor: false, periodic: false, fixedText: null, Width, Height, now, fountain: false);
      }

      Assert.AreEqual(12, _hits.Count, "the crit lane is capped and nothing merged into a crit");
      Assert.AreEqual(8, ingest.DroppedCount, "lost crits have to show up in the header, not silently disappear");
    }

    /*
     * Bands is the default and carries the overlay's whole direction story on its own: incoming lives below the
     * protected strip and travels down, outgoing above it and up. Pinned here because the canvases only forward
     * the mode, so a regression would silently turn into "which side is which again?" while invisible to a unit
     * test of the renderers.
     */
    [TestMethod]
    public void BandsModePutsIncomingBelowAndOutgoingAboveTheStrip()
    {
      var ingest = NewIngest();

      var outgoing = ingest.Accept(_hits, FctLane.DamageDealt, 500, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0, fountain: false);
      var incoming = ingest.Accept(_hits, FctLane.DamageTaken, 500, "Bites", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 10, fountain: false);

      Assert.IsTrue(outgoing.Rise > 0, $"outgoing text must rise (Rise {outgoing.Rise})");
      Assert.IsTrue(outgoing.BandMaxY <= (Height * FctLayout.GapTopFrac) + 0.001, "outgoing band must stop at the protected strip");
      Assert.IsTrue(incoming.Rise < 0, $"incoming text must sink (Rise {incoming.Rise})");
      Assert.IsTrue(incoming.BandMinY >= (Height * FctLayout.GapBottomFrac) - 0.001, "incoming band must start below the protected strip");
    }

    /* The mode has to reach geometry rather than just be remembered: halves restores the left/right clamp and adds
     * no vertical limit at all. */
    [TestMethod]
    public void LayoutModeReachesTheGeometry()
    {
      var ingest = NewIngest();
      ingest.Mode = FctLayoutMode.Halves;

      var outgoing = ingest.Accept(_hits, FctLane.DamageDealt, 500, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0, fountain: false);
      var incoming = ingest.Accept(_hits, FctLane.DamageTaken, 500, "Bites", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 10, fountain: false);

      Assert.IsTrue(outgoing.SideMin >= (Width / 2) - 1, $"halves mode put my hits on the incoming side ({outgoing.SideMin})");
      Assert.IsTrue(incoming.SideMax <= (Width / 2) + 1, $"halves mode put hits on me on the outgoing side ({incoming.SideMax})");
      Assert.AreEqual(0.0, outgoing.BandMaxY, "halves mode adds no vertical clamp");
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
      var gapBottom = Height * FctLayout.GapBottomFrac;

      var incoming = ingest.Accept(_hits, FctLane.DamageTaken, 500, "Bites", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0, fountain: true);

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
      var gapTop = Height * FctLayout.GapTopFrac;

      var outgoing = ingest.Accept(_hits, FctLane.DamageDealt, 900, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0, fountain: true);

      Assert.AreEqual(Height * 0.28, outgoing.FallDist, 0.001, "the outgoing band keeps its literal gravity tail");
      Assert.IsTrue(FctMotion.RaisedY(outgoing, 1.0) > (outgoing.Y0 - outgoing.Rise), "outgoing fountain text comes back down");

      for (var t = 0.0; t <= 1.0; t += 0.05)
      {
        var y = FctMotion.RaisedY(outgoing, t);
        Assert.IsTrue(y + FctLayout.TextReserve(outgoing) <= gapTop + 0.001,
          $"outgoing hit fell into the protected strip at t={t:0.00} (bottom {y + FctLayout.TextReserve(outgoing):0.#}, gap top {gapTop:0.#})");
      }
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
