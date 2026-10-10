/*
 * Annotations only, no null-flow analysis (the project builds with Nullable=disable): a click can legitimately have no
 * row behind it on the way to a label, and that is the answer this policy gives rather than a warning.
 */
#nullable enable annotations

namespace EQLogParser;

/*
 * How long the Names pane's "Recalculating…" label stays over the row its click started (2026-11).
 *
 * The verb under it — `ClassificationCommands.Recalculate` — forgets every stored belief about a name and lets this capture's
 * own lines answer again, and the honest outcome of that is SOMETIMES "no visible change" (the capture re-earns exactly what
 * memory said). The operator asked for the label on precisely those grounds: "if the reset ends up with the same result at
 * least the user knows that it tried". So the law has three terms, in this order:
 *
 *   1. **At least `MinVisibleMs`.** Under the floor the label must stay up no matter what — a take-back whose answer is
 *      identical to what was on screen still says it tried. This is the INVERSE of `DeferredBusyState` (which delays SHOWING
 *      because a fast build makes a promise it cannot keep): here the showing is immediate and certain, only the HIDING waits.
 *   2. **Past the floor, until the pass lands.** The repaint IS the answer — the census and the boards both read the derive's
 *      snapshot, so "done" means "the pass this click asked for has run", not a wall-clock guess about it.
 *   3. **`HardCapMs`, whatever else.** A bulk load parks BOTH derive lanes (DeriveCadence answers `None` while facts pour in),
 *      and a label that waits on a pass that may not come for minutes would read as a frozen row. The cap is a lie by at most
 *      five seconds; the alternative is a UI thread waiting on the ingest gate, which is worse.
 */
internal static class RecalcOverlay
{
  /// <summary>The floor: shorter than this and an identical-looking answer shows no trace of the click.</summary>
  internal const long MinVisibleMs = 500;

  /// <summary>The cap: longer than this and a pass that is parked (bulk load, a quiet capture that stopped owing one) holds the row hostage.</summary>
  internal const long HardCapMs = 5000;

  /// <summary>
  /// True once the label may go: the floor has been paid AND either the pass the click asked for has landed or the cap has.
  /// `startTick`/`nowTick` are `Environment.TickCount64` readings — monotonic within the process, and the same clock the pane
  /// already uses for its refresh floor, so no conversion can drift between the two.
  /// </summary>
  internal static bool ShouldHide(long startTick, long nowTick, bool passLanded)
  {
    var elapsed = nowTick - startTick;
    if (elapsed < MinVisibleMs) return false;
    return passLanded || elapsed >= HardCapMs;
  }
}
