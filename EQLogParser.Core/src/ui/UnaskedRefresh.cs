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
 * So the rule is not "never rebuild unasked" and not "always": it is **an unasked rebuild runs while it is too small to
 * be noticed**. Below the limit a live pull keeps updating its own boards exactly as it always has; above it, the report on
 * screen stays exactly as it was until the operator asks — by selecting, by changing a dial, or by Refresh — because at that
 * size a pass-driven rebuild is both visible and repeated (a live raid hands out derive passes ~2/second).
 *
 * The limit is stated in outcomes because that is the unit cost moves in. Healing is not counted separately: healing rides
 * the same materialization and scans the same window, and the two track each other on real captures (403,500 heals against
 * 1,720,467 outcomes on the reference capture), so one number decides both.
 */
internal static class UnaskedRefresh
{
  /// <summary>What an unasked rebuild may cost before it is declined: ~0.13 s at the measured 1.3 µs per outcome.</summary>
  internal const long MaxOutcomes = 100_000;

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

  /// <summary>Let this ask through? `outcomes` comes back either way so the caller can name the size in its log line.</summary>
  internal static bool Allows(BoardReason reason, IReadOnlyList<DerivedFight>? fights, out long outcomes)
  {
    outcomes = OutcomeCount(fights);

    // A gesture is an ask; it never pays a policy. The whole-capture select-all costs what it costs and the operator chose it.
    if (!IsUnasked(reason)) return true;

    return outcomes <= MaxOutcomes;
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

  /// <summary>Forget a debt — a session change, or a test that does not want to spend a build it never asked for.</summary>
  internal static void ClearGesture() => Volatile.Write(ref _owedGesture, null);
}
