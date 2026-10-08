using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;
using System.Collections.Generic;

using static EQLogParser.BoardReason;

namespace EQLogParser.Tests.Ui
{

  /*
   * Whether a rebuild that nobody asked for may run, in the one place that decides it (EQLogParser.Core/src/ui/UnaskedRefresh.cs).
   *
   * The rule is a BUDGET, not a ceiling: an unasked rebuild may take `BudgetSharePercent` of the wall time since the last unasked
   * rebuild ran, priced from the selection's outcome count. The reason a flat limit was replaced is measured and recorded in
   * docs/DesignNotes.md ("Where a board build's time actually goes" → "The same test, run bigger"): over the 998 MB capture ONE
   * selected running encounter is 280,690 outcomes (0.81 s) while a whole night is 4,581,738 (9.76 s), so any constant either sits
   * above the case that must keep refreshing (a live meter's rows: ~3,255 outcomes, 0.124 s) or below a case it should allow - and at
   * 100,000 it did the latter, so a selected running boss was quietly never refreshed. A cliff was the defect; these tests pin the slope.
   *
   * Every test here moves time by HAND: `Allows(..., nowMs)` takes the clock as a parameter exactly so the interval law can be
   * asserted instead of slept through. A test that slept would pass on a fast machine and fail on a loaded one, which is the failure
   * mode this whole file exists to prevent.
   */
  [TestClass]
  public class UnaskedRefreshTest
  {
    // The shape a live meter holds: the rows still fighting at the end of a farm night (measured 3,255 outcomes).
    private const long LiveMeterOutcomes = 3_000;

    // Clearing the debt clears the budget clock with it (they are one accounting), which is what a test boundary wants.
    [TestInitialize]
    public void Setup() => EQLogParser.UnaskedRefresh.ClearGesture();

    [TestCleanup]
    public void Cleanup() => EQLogParser.UnaskedRefresh.ClearGesture();

    private static List<DerivedFight> Fights(long outcomes)
      => [new DerivedFight { Id = 1, DamageHits = (uint)outcomes }];

    private static bool Allows(BoardReason reason, IReadOnlyList<DerivedFight>? rows, long nowMs = 0)
      => EQLogParser.UnaskedRefresh.Allows(reason, rows, out _, nowMs);

    // ── the vocabulary: which words mean "a pass did this" ────────────────────────────────────────────────

    [TestMethod]
    public void ThreeReasonsComeFromAPassAndFourFromAnOperator()
    {
      var unasked = new HashSet<BoardReason>();
      var asked = new HashSet<BoardReason>();

      foreach (var reason in Enum.GetValues<BoardReason>())
      {
        // The pass cannot produce a gesture word, and no gesture word may be reachable by a pass: the same split the
        // budget keys on is what the dialog policy would key on. If a sixth reason arrives, this names it rather than
        // letting it default into either bucket.
        (EQLogParser.UnaskedRefresh.IsUnasked(reason) ? unasked : asked).Add(reason);
      }

      Assert.IsTrue(unasked.SetEquals([ContentMoved, RowEdited, SnapshotSwap]),
          "the pass-driven doors are exactly these three; a fourth word means a new announce path arrived and must decide whether it is traffic");
      Assert.IsTrue(asked.SetEquals([SelectCommand, MenuClose, SettleTick, Manual]),
          "an operator gesture is exactly these four - a selection command, a menu released, the settle timer, and Refresh");
    }

    // ── what the budget means: deferred, never refused ────────────────────────────────────────────────────

    [TestMethod]
    public void ASelectedRunningEncounterIsDeferredAndThenRuns()
    {
      /*
       * The field case that killed the flat ceiling: a raid selects the encounter it is fighting (280,690 outcomes over the three
       * largest rows of `eqlog_Kizant_xegony-09-03-26.txt`, measured at 807 ms for the whole board) and a pass moves under it.
       */
      var running = Fights(280_690);

      Assert.IsTrue(Allows(ContentMoved, running, nowMs: 0), "the first ask after a capture opens runs whatever its size");
      Assert.IsFalse(Allows(ContentMoved, running, nowMs: 1_000), "one second later is inside its own budget; the report on screen stays as it was");

      var gap = EQLogParser.UnaskedRefresh.RequiredGapMs(280_690);
      Assert.IsTrue(Allows(ContentMoved, running, nowMs: gap + 1), "once the gap opens the same ask runs - deferred, not refused");
    }

    [TestMethod]
    public void AWholeNightIsDeferredAndNeverRefusedForever()
    {
      /*
       * The other half of the cliff: under the old rule a whole-capture selection (4,581,738 outcomes measured over that capture)
       * was DECLINED at every pass, forever. A budget cannot say "never" - it can only say "not yet".
       */
      var night = Fights(4_581_738);

      Assert.IsTrue(Allows(SnapshotSwap, night, nowMs: 0));
      Assert.IsFalse(Allows(SnapshotSwap, night, nowMs: 10_000), "a 20-second rebuild does not get to run ten seconds after the last one");

      for (var t = 10_000; t <= 5 * 60_000; t += 1_000)
      {
        if (Allows(SnapshotSwap, night, nowMs: t)) return;
      }

      Assert.Fail("a whole-night ask was still refused after five minutes of quiet - the budget has become the old ceiling again");
    }

    [TestMethod]
    public void ABiggerAskWaitsLongerThanASmallerOne()
    {
      /*
       * The slope IS the policy: cost is continuous, so the wait has to be too. These are the sizes measured over real captures,
       * in the order a raid actually meets them.
       */
      Assert.IsTrue(EQLogParser.UnaskedRefresh.RequiredGapMs(LiveMeterOutcomes) < EQLogParser.UnaskedRefresh.RequiredGapMs(100_000));
      Assert.IsTrue(EQLogParser.UnaskedRefresh.RequiredGapMs(100_000) < EQLogParser.UnaskedRefresh.RequiredGapMs(280_690));
      Assert.IsTrue(EQLogParser.UnaskedRefresh.RequiredGapMs(280_690) < EQLogParser.UnaskedRefresh.RequiredGapMs(4_581_738));

      // ...and no size costs MORE than its own estimate times the reciprocal share, which is what keeps one ask from owning the lane.
      Assert.IsTrue(EQLogParser.UnaskedRefresh.RequiredGapMs(280_690) == EQLogParser.UnaskedRefresh.EstimatedCostMs(280_690) * 100 / EQLogParser.UnaskedRefresh.BudgetSharePercent);
    }

    [TestMethod]
    public void ASizeThatCountsAsNothingRefreshesEveryTime()
    {
      /*
       * A live pull's board is the case that MUST keep updating itself: measured 0.124 s for all three boards over the rows a meter
       * holds at the end of a farm night, and 2-21 ms for the same shape on the reference capture. It pays no budget, so a pass every
       * half-second refreshes it.
       */
      for (var i = 0; i < 10; i++)
      {
        Assert.IsTrue(Allows(ContentMoved, Fights(LiveMeterOutcomes), nowMs: i * 500), $"pass {i} must still refresh a board this cheap");
      }

      Assert.IsTrue(Allows(ContentMoved, null, nowMs: 0), "an empty selection is free by definition");
    }

    [TestMethod]
    public void ADeferredAskLeavesTheClockWhereItWas()
    {
      /*
       * Only an ACCEPT spends the budget. If a refusal moved the stamp, a big deferred ask would push every later rebuild further away
       * - and on a live tail, where asks arrive twice a second, one expensive selection could starve the cheap refreshes that follow it.
       */
      Assert.IsTrue(Allows(ContentMoved, Fights(LiveMeterOutcomes), nowMs: 1_000));
      Assert.IsFalse(Allows(ContentMoved, Fights(4_581_738), nowMs: 1_500), "the night-sized ask is deferred");

      var gap = EQLogParser.UnaskedRefresh.RequiredGapMs(LiveMeterOutcomes);
      Assert.IsTrue(Allows(RowEdited, Fights(LiveMeterOutcomes), nowMs: 1_500 + gap + 1),
          "the refused ask spent nothing; the meter-sized board behind it still refreshes on its own schedule");
    }

    [TestMethod]
    public void AGestureNeverPaysTheBudget()
    {
      // An operator clicked: a select-all costs what it costs, and it never waits behind a pass's spending.
      Assert.IsTrue(Allows(ContentMoved, Fights(9_000_000), nowMs: 0));

      foreach (var reason in new[] { SelectCommand, MenuClose, SettleTick, Manual })
      {
        for (var t = 1; t <= 4; t++)
        {
          Assert.IsTrue(Allows(reason, Fights(9_000_000), nowMs: t), $"{reason} must not wait for a gap - it is an ask, not traffic");
        }
      }
    }

    // ── the estimator, pinned to what a board actually costs ──────────────────────────────────────────────

    [TestMethod]
    public void TheEstimatorSitsOnTheMeasuredBoards()
    {
      /*
       * Three anchors from docs/DesignNotes.md so that changing a constant is a decision with numbers in it, and a refactor that
       * makes boards cheaper or dearer cannot quietly move the policy out from under its own justification. Bands rather than exact
       * values: run-to-run spread on these builds is tens of percent, and a test that pins jitter fails for the wrong reason.
       */
      Assert.AreEqual(0, EQLogParser.UnaskedRefresh.EstimatedCostMs(0), "nothing to compute must not be charged a fixed cost");

      var live = EQLogParser.UnaskedRefresh.EstimatedCostMs(3_255);
      Assert.IsTrue(live >= 100 && live <= 400, $"a meter-sized board measured 124 ms; the estimate answered {live} ms");

      var encounter = EQLogParser.UnaskedRefresh.EstimatedCostMs(280_690);
      Assert.IsTrue(encounter >= 400 && encounter <= 1_200, $"one big encounter measured 807 ms; the estimate answered {encounter} ms");

      var night = EQLogParser.UnaskedRefresh.EstimatedCostMs(4_581_738);
      Assert.IsTrue(night >= 6_000 && night <= 20_000, $"a whole night measured 9,760 ms; the estimate answered {night:N0} ms");
    }

    [TestMethod]
    public void TheBudgetIsCountedInHitsNotInRows()
    {
      /*
       * Cost follows outcomes, not row count: on Incogitable 40 rows cost 27-56 ms and 4,646 rows cost 1.5 s, while the biggest single
       * encounter on the 998 MB capture is ONE row that costs 0.81 s. `OutcomeCount` reads both directions of a row's work, because
       * being hit is counted into the tanking board too.
       */
      var many = new List<DerivedFight>();
      for (var i = 0; i < 4_646; i++)
      {
        many.Add(new DerivedFight { Id = i, DamageHits = 1, TankHits = 0 });
      }

      Assert.AreEqual(4_646, EQLogParser.UnaskedRefresh.OutcomeCount(many));

      var oneBig = new List<DerivedFight> { new() { Id = 1, DamageHits = 200_000, TankHits = 80_000 } };
      Assert.AreEqual(280_000, EQLogParser.UnaskedRefresh.OutcomeCount(oneBig));
      Assert.IsTrue(EQLogParser.UnaskedRefresh.OutcomeCount(oneBig) > EQLogParser.UnaskedRefresh.OutcomeCount(many),
          "one running encounter out-asks four thousand rows that barely fought");
    }

    // ── the debt an operator write leaves behind (unchanged law, still load-bearing) ──────────────────────

    [TestMethod]
    public void AnOperatorWriteLendsItsGestureToThePassItCaused()
    {
      /*
       * Claiming a pet is a click whose result only exists after the next pass: the write asks for the pass, the patch announces
       * `ContentMoved`, and that word is traffic. Without this the deliberate act would be declined by a policy written for traffic -
       * and with a budget instead of a ceiling it still would be, because an ask over a whole-capture selection defers on its first try.
       */
      EQLogParser.UnaskedRefresh.OweGesture("pet claim Picklepaw -> Beorun");
      Assert.IsTrue(EQLogParser.UnaskedRefresh.TryConsumeGesture(out var gesture));
      Assert.AreEqual("pet claim Picklepaw -> Beorun", gesture);

      // Consumed once: the next pass is traffic again, and the write is on screen by now.
      Assert.IsFalse(EQLogParser.UnaskedRefresh.TryConsumeGesture(out _), "one write owes one announce, not every pass forever");
    }

    [TestMethod]
    public void AForgettableDebtIsForgotten()
    {
      EQLogParser.UnaskedRefresh.OweGesture("verdict Picklepaw = Pet");
      Assert.IsTrue(EQLogParser.UnaskedRefresh.TryConsumeGesture(out _));

      EQLogParser.UnaskedRefresh.OweGesture("identity edit");
      EQLogParser.UnaskedRefresh.ClearGesture();

      Assert.IsFalse(EQLogParser.UnaskedRefresh.TryConsumeGesture(out _));
    }
  }
}
