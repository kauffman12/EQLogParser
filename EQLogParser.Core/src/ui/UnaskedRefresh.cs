/*
 * Annotations only, no null-flow analysis (the project builds with Nullable=disable): a selection can legitimately be
 * empty or null on the way to a board, and "that costs nothing" is the answer this policy gives rather than a warning.
 */
#nullable enable annotations

namespace EQLogParser;

/*
 * Whether a summary rebuild may happen WITHOUT the operator having asked for it.
 *
 * Two facts, from the field report and from the stopwatch:
 *
 *   - A displayed board is a report somebody is reading. Figures moving on their own were the complaint; the cost was
 *     never the first half of it (docs/summary-refresh-notification.md §0). An operator who clicked something is a
 *     different case — that is an ask, and it already rebuilds today.
 *   - Cost scales with outcomes, not rows: on the reference capture a five-minute selection (8 rows / 1,245 outcomes)
 *     rebuilds all three boards in 2-21 ms, while a whole-capture selection (4,646 rows / 1,720,467 outcomes) costs
 *     ~0.6-0.8 s of materialization plus ~1.5 s of building — roughly **1.3 µs per outcome**, and it lands on top of the
 *     grid the reader is looking at (docs/DesignNotes.md → "Where a board build's time actually goes").
 *
 * So the rule is not "never rebuild unasked" and not "always". It is a **budget**: an unasked rebuild may take a fixed share
 * of the wall-clock time since the last unasked rebuild in that lane ran. A live pull's board (an eighth of a second of work)
 * therefore refreshes on every pass, because there is always a gap big enough to pay for it; a selected running encounter
 * refreshes every few seconds; a whole night about once a minute. Nothing is refused forever, and nothing hogs the UI thread.
 *
 * Why not the flat ceiling this file shipped with (100,000 outcomes, declined above it): measured over the biggest capture on
 * this machine (`eqlog_Kizant_xegony-09-03-26.txt`, 998 MB, 708 rows / 4,581,738 outcomes), ONE selected running boss is
 * 280,690 outcomes and costs 0.81 s while a whole night is 4.58 M outcomes and costs 9.76 s. A constant therefore sits either on
 * top of the case that must keep refreshing (the rows a live meter holds: ~3,255 outcomes, 0.12 s) or below a case it should
 * allow - and where it sits it acts as a cliff, not a rate: at 100,000 a selected running encounter was quietly never refreshed.
 * A continuous cost wants a continuous policy; "figures moved by themselves" is answered just as well because a deferred rebuild
 * is only ever LATER, never never, and a gesture (a click, a dial, Refresh) still jumps the budget entirely.
 *
 * The estimate comes from the counted outcomes of the selection, which is what makes asking cheap enough to do on every announce:
 * no trial build, no profiler. Healing is not counted separately because a `DerivedFight` counts hits, not heals - its share lives
 * inside the per-outcome rate below, which is why that rate sits ABOVE what damage alone measures.
 */
internal static class UnaskedRefresh
{
  /*
   * ONE lane today: the three summary boards, which are built from one selection by one call. The live damage overlay is a
   * different door with a different cost model (it recomputes a WINDOW over the captured facts per tick through `DerivedTotals`,
   * measured on its own in docs/DesignNotes.md -> "How long a meter update takes, measured end to end"), so it does not ask here and does not
   * spend this budget. If a second summary-shaped door ever appears, give it a lane rather than letting two selections quietly
   * share one clock.
   */

  /*
   * An ask this small is free: it needs no gap opened behind it. Measured as the whole three-board build over the rows a live meter
   * holds at the end of a farm night (3,255 outcomes) - **0.124 s** on the 998 MB capture, and 2-21 ms for the same shape on the
   * reference capture - so refreshing that every pass is what the operator already expects to see.
   */
  internal const long FreeOutcomes = 3_000;

  /// <summary>The part of a build no selection size removes: three grids, the grouping passes, materialize setup (~0.12 s measured).</summary>
  internal const long FixedCostMs = 120;

  /*
   * Marginal cost of one counted outcome across all three boards, in microseconds.
   *
   * Measured on this machine: 0.87 µs per outcome over the reference capture's select-all (1.5 s / 1,720,467 outcomes) and 1.35 µs
   * over the 998 MB night (9.76 s / 4,581,738). That second figure carries 2,670,809 heals which are NOT in this outcome count, so
   * the constant deliberately sits ABOVE both readings: an unasked rebuild that turns out cheaper than predicted merely ran early,
   * while one under-estimated takes the UI thread by surprise.
   */
  internal const double MicrosecondsPerOutcome = 2.0;

  /*
   * How much of the time since this lane's last accepted unasked rebuild the next one may take. A quarter keeps a small board
   * refreshing on every pass (a 125 ms ask needs 500 ms of gap, and both the settle timer and the derive pump are slower than that)
   * while a 20 s ask waits 80 s. Too small a share stops a live pull refreshing; too large lets one big selection starve the lane.
   */
  internal const int BudgetSharePercent = 25;

  /*
   * The three doors a derive pass can walk through on its own. Everything else on `BoardReason` is a gesture: SelectCommand
   * (the menu), MenuClose (a selection parked behind a menu, released when it closed), SettleTick (the settle timer after a
   * click or drag) and Manual (Refresh). A pass cannot produce those words, which is why the question is answerable from
   * the reason alone and needs no new flag beside it.
   *
   * `RowEdited` being unasked is deliberate and it follows the plan's own ruling: when a selected encounter disappears from
   * the live grid, the displayed report is PRESERVED (a real encounter's numbers are not deleted because the row re-split),
   * and only a deliberate deselection or a capture clear takes the boards away.
   */
  internal static bool IsUnasked(BoardReason reason)
    => reason is BoardReason.ContentMoved or BoardReason.RowEdited or BoardReason.SnapshotSwap;

  /// <summary>What a build over these rows would cost, in counted outcomes (both directions: dealing and being hit).</summary>
  internal static long OutcomeCount(IReadOnlyList<DerivedFight>? fights)
  {
    if (fights is null) return 0;

    long outcomes = 0;
    for (var i = 0; i < fights.Count; i++)
    {
      var fight = fights[i];
      if (fight is null) continue;
      outcomes += fight.DamageHits + fight.TankHits;
    }

    return outcomes;
  }

  /*
   * When the last unasked rebuild was accepted. `-1` means "never", which is the answer that lets the FIRST ask of a session through
   * whatever its size: an operator who opened a log and touched nothing still gets their boards.
   *
   * An accept stamps here rather than when the build finishes, on purpose: the caller can still drop the request (the summary gate
   * skips a build whose inputs are identical), and a skipped duplicate that cost nothing making the NEXT ask wait a little longer is
   * the safe direction. Stamping on completion would need a callback through that gate for a rounding error no one can see.
   */
  private static long _lastAcceptedMs = -1;

  /// <summary>How long a build over this many outcomes is expected to take, in whole milliseconds.</summary>
  internal static long EstimatedCostMs(long outcomes)
    => outcomes <= 0 ? 0 : FixedCostMs + (long)(outcomes * MicrosecondsPerOutcome / 1000.0);

  /// <summary>The gap an ask of this size has to see behind it before it runs.</summary>
  internal static long RequiredGapMs(long outcomes)
    => EstimatedCostMs(outcomes) * 100 / BudgetSharePercent;

  /// <summary>Let this ask through? `outcomes` comes back either way so the caller can name the size in its log line.</summary>
  internal static bool Allows(BoardReason reason, IReadOnlyList<DerivedFight>? fights, out long outcomes)
    => Allows(reason, fights, out outcomes, Environment.TickCount64);

  /*
   * The clock is a parameter because the law here is about intervals: a test that cannot move time can only assert what a constant
   * says, not what the budget does. `Environment.TickCount64` is the production clock - monotonic, and unmoved by an operator who
   * changes the wall clock mid-raid.
   */
  internal static bool Allows(BoardReason reason, IReadOnlyList<DerivedFight>? fights, out long outcomes, long nowMs)
  {
    outcomes = OutcomeCount(fights);

    // A gesture is an ask; it never pays a policy. The whole-capture select-all costs what it costs and the operator chose it.
    if (!IsUnasked(reason)) return true;

    var last = Volatile.Read(ref _lastAcceptedMs);

    // Nothing to pay for, or nothing spent recently: run it, and start/renew the clock.
    if (outcomes <= FreeOutcomes || last < 0 || nowMs - last >= RequiredGapMs(outcomes))
    {
      Volatile.Write(ref _lastAcceptedMs, nowMs);
      return true;
    }

    return false;
  }

  /*
   * An operator write that only shows up ONCE THE NEXT PASS RUNS owes its gesture to that pass.
   *
   * The problem this solves is a hole in the budget: claiming a pet or writing a verdict is a click, but the announce it causes is
   * not — the write asks for a pass, the pass patches the grid, and the patch announces `ContentMoved`/`RowEdited`, which a
   * whole-capture selection will decline. So the operator's deliberate act would be swallowed by a policy written for traffic, and
   * "did my click do anything?" — the exact question this design exists to answer — would have no answer until Refresh.
   *
   * One slot, not a queue: several writes before the next pass are one gesture ("rebuild, eventually, because an operator acted"),
   * and the phrase is what the log names. It is consumed by the FIRST announce after the write, whatever reason that announce
   * carries — an operator who selects another row in the meantime still gets their write on screen, just with that ask.
   *
   * Process-static like the dialogs' `BoardReason` flow, so two things hold it in check: a capture change clears it (rows from the
   * old log cannot be rebuilt by this one's pass), and only the app's operator doors set it — NOT the stores underneath them, which
   * tests and imports also drive, and a debt minted by a test would spend a forced build in an unrelated one.
   */
  private static string? _owedGesture;

  /// <summary>Record that an operator acted and the result only lands on the next pass. `what` names the act in the log.</summary>
  internal static void OweGesture(string what) => Volatile.Write(ref _owedGesture, what);

  /// <summary>Take the owed gesture, if any. The caller announces as `Manual` so the budget cannot decline it.</summary>
  internal static bool TryConsumeGesture(out string what)
  {
    var gesture = Interlocked.Exchange(ref _owedGesture, null);
    what = gesture!;
    return gesture is not null;
  }

  /*
   * Forget the owed gesture AND the budget clock. They clear together because they are the same kind of thing - an accounting of
   * what this pane owes itself - and a session change or a test cleanup wants both gone: rows from a closed capture cannot be
   * rebuilt by this one's pass, and a build some earlier test spent must not make a later one wait.
   */
  internal static void ClearGesture()
  {
    Volatile.Write(ref _owedGesture, null);
    Volatile.Write(ref _lastAcceptedMs, -1);
  }
}
