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

    [TestMethod]
    public void PeriodicTicksFoldIntoOneRunningNumber()
    {
      var ingest = NewIngest();

      ingest.Accept(_hits, FctLane.DamageDealt, 200, "Immolation", crit: false, minor: true, periodic: true, fixedText: null, Width, Height, 0);

      for (var now = 200.0; now < 1200; now += 200)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 200, "Immolation", crit: false, minor: true, periodic: true, fixedText: null, Width, Height, now);
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
        ingest.Accept(_hits, FctLane.HealingDealt, 1500, "Healing Word", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, now);
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
        ingest.Accept(_hits, FctLane.DamageDealt, 600, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, now);
      }

      var before = _hits.Count;
      Assert.IsNull(ingest.Accept(_hits, FctLane.DamageDealt, 60, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 1000));
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
        ingest.Accept(_hits, FctLane.DamageDealt, 900, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, now);
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
        ingest.Accept(_hits, FctLane.DamageDealt, 900, "Flurry", crit: true, minor: false, periodic: false, fixedText: null, Width, Height, now);
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

      var outgoing = ingest.Accept(_hits, FctLane.DamageDealt, 500, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0);
      var incoming = ingest.Accept(_hits, FctLane.DamageTaken, 500, "Bites", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 10);

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

      var outgoing = ingest.Accept(_hits, FctLane.DamageDealt, 500, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0);
      var incoming = ingest.Accept(_hits, FctLane.DamageTaken, 500, "Bites", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 10);

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
     * Spray exists to fan repeated hits out instead of stacking them into one unreadable column, so the assertion is
     * relative to hold rather than an absolute pixel figure — that would just be a second constant to maintain.
     */
    [TestMethod]
    public void SprayFansWiderThanHold()
    {
      var held = Sweep(FctMotionStyle.Hold);
      var sprayed = Sweep(FctMotionStyle.Spray);

      // a modest margin on purpose: spray should be visibly wider, not a different universe of chaos
      Assert.IsTrue(sprayed.Width > held.Width * 1.35,
        $"spray is supposed to fan out: hold covered {held.Width:0} px of x, spray {sprayed.Width:0} px");
    }

    /* Every style owes the same two promises; sweep asserts them for hold, fountain, pulse and spray alike. */
    [TestMethod]
    public void EveryMotionStyleKeepsTheStripClearAndTheWindowInside()
    {
      foreach (var style in new[] { FctMotionStyle.Hold, FctMotionStyle.Fountain, FctMotionStyle.Pulse, FctMotionStyle.Spray })
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
     * A proc lands on top of the swing the player aimed, and items fire them constantly, so it rides a size below its
     * lane and runs a shorter life. Both halves are pinned because either alone looks like tuning drift rather than a
     * rule, and the source line is included: a full-size ability name under a shrunken value would undo the whole thing.
     */
    [TestMethod]
    public void ProcsAreSmallerAndQuickerThanTheHitThatProvokedThem()
    {
      var plain = NewIngest().Accept(new List<FctHitState>(), FctLane.DamageDealt, 1000, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0);
      var proc = NewIngest().Accept(new List<FctHitState>(), FctLane.DamageDealt, 1000, "Soul Strike", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, 0, proc: true);

      Assert.AreEqual(FctStyle.DamageDealtFontSize * FctStyle.ProcSizeFrac, proc.ValueFontSize, 0.001);
      Assert.IsTrue(proc.ValueFontSize < plain.ValueFontSize, "a proc must not compete with the hit behind it");
      Assert.IsTrue(proc.SourceFontSize < plain.SourceFontSize, "the ability line shrinks with its value");
      Assert.IsTrue(proc.LifetimeMs < plain.LifetimeMs, $"a proc clears sooner (was {proc.LifetimeMs:0} vs {plain.LifetimeMs:0})");
      Assert.IsTrue(proc.MotionMs < plain.MotionMs, "the whole tempo shortens, not just the tail");
      Assert.IsTrue(proc.FadeMs < plain.FadeMs, "and it fades proportionally, or it lingers while shrinking");

      // still legible: subordinate is not footnote
      Assert.IsTrue(proc.ValueFontSize > FctStyle.MinorFontSize, $"proc size {proc.ValueFontSize:0.#}");
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

      for (var i = 0; i < 5; i++)
      {
        ingest.Accept(_hits, FctLane.DamageDealt, 500, "Flurry", crit: false, minor: false, periodic: false, fixedText: null, Width, Height, i * 100);
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
    private (double Width, double MinX, double MaxX) Sweep(FctMotionStyle style)
    {
      var hits = new List<FctHitState>();
      var ingest = NewIngest();
      ingest.Style = style;

      var gapTop = Height * FctLayout.GapTopFrac;
      var minX = double.MaxValue;
      var maxX = double.MinValue;

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

      return (maxX - minX, minX, maxX);
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
