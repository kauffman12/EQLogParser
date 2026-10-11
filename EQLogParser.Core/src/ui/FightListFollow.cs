namespace EQLogParser;

/*
 * Whether a growing fight list may move its own viewport down to the newest row.
 *
 * This is the old list's behaviour, ported rather than reinvented: legacy's `NewRowsAdded` scrolled the grid to its last
 * record on every pass that added rows, with ONE exception — `!dataGrid.IsMouseOver`. That exception is the whole design and
 * it is worth keeping exactly: a raid writes a new row every few seconds, so "follow the growth" is what makes the list read
 * as live (the newest pull is at the bottom and the pane shows it), while a mouse anywhere inside the grid means somebody is
 * reading or selecting a part of it and being slid down mid-read is worse than missing a row. It is a HAND test, not a
 * scroll-position test: no memory of where the reader was is kept, so nothing has to be restored and no heuristic can guess
 * wrong about intent.
 *
 * The `paneOnScreen` half is legacy's dock-state guard in WPF's own words (`IsVisible`): an auto-hidden tab nobody has slid
 * out has no viewport worth moving, and scrolling it costs layout work for a grid nobody is looking at. The row-count floor
 * exists because with one row there is nothing to follow: the newest row IS the only row, and scrolling to it on every pass of
 * a capture that found one fight is pure churn.
 *
 * Kept apart from the pane so the rule can be asserted without a window; the pane reads `IsVisible`, `IsMouseOver` and the
 * grid's own visible-record count and asks this question.
 */
internal static class FightListFollow
{
  /// <summary>May this pass move the viewport to the newest row? Only when rows arrived, the pane is on screen, and no hand is on it.</summary>
  internal static bool ShouldFollowToNewest(bool rowsArrived, bool paneOnScreen, bool mouseOverGrid, int shownRowCount)
    => rowsArrived && paneOnScreen && !mouseOverGrid && shownRowCount > 1;
}
