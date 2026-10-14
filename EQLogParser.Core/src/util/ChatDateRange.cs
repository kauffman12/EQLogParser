using System;

namespace EQLogParser
{
  /*
   * The chat archive's date range, decided as whole days with an EXCLUSIVE upper edge.
   *
   * The Chat pane keeps its range in two DependencyObject-backed date dials and asks for the two bounds as dotnet
   * seconds every time it builds a query; `ChatFilter` then uses them to prune which year folders, month archives and
   * day entries are even opened. Until now the upper bound was the raw instant off that dial, and the pane stamped it
   * once in its constructor (`DateTime.Now`) — so for every hour the app stayed open the bound aged. The pruning test
   * was `_endDate >= dayBegin`, which holds for the day the dial was stamped and fails for the next one: at 00:00:01
   * today stopped being part of the answer, the newest line on screen belonged to yesterday, and Refresh rebuilt the
   * filter from the same stale instant, so there was nothing the operator could press to get today back short of
   * restarting the app or nudging the picker (docs/DesignNotes.md → "A chat range is whole days"). The same arithmetic
   * cut off a new month on the 1st and a new year folder on Jan 1, because `DuringMonth`/`DuringYear` compare the same
   * pair.
   *
   * Two decisions follow from what the dials actually mean to a person reading them:
   *
   * - **A day is the unit, so the bound is a day edge.** An end date means "through that day", which only an exclusive
   *   next-midnight expresses: it keeps the picked day whole (the picker keeps whatever clock time the app happened to
   *   open at, which was never part of the question) and refuses every later day.
   * - **An end dial nobody touched means "up to now", not "up to when this pane was built"**, so it rides the clock and
   *   rolls into tomorrow by itself. The moment an operator picks a date the dial is theirs and stops moving — that is
   *   the one case where a stale-looking answer is the correct answer.
   *
   * Nothing here changes which lines are shown inside a day that survives pruning: per-line filtering never consulted
   * time at all (channels, speaker, keyword only), so this can neither widen a deliberately narrowed range nor reveal
   * lines the old bound excluded on purpose.
   */
  internal static class ChatDateRange
  {
    /* "Unbounded". Both dials answer 0 when they hold no value, and every comparison below reads 0 as "no edge here". */
    public const double NoBound = 0;

    /* The lower edge: the start of the picked day, inclusive — a person picking Mar 16 means that day is in. */
    internal static double ResolveStart(DateTime? picked) =>
      picked is { } value ? DateUtil.ToDotNetSeconds(value.Date) : NoBound;

    /*
     * The upper edge, exclusive: midnight ending the last day the range covers. An untouched dial follows `now`, so a
     * session that runs past midnight still covers today; a picked dial is frozen at what it was picked as. If both
     * sides somehow disagree (a dial stamped before the clock rolled and nobody re-stamped it) the later day wins, so
     * this function never re-creates the bug it fixes even if the caller forgets to stamp.
     */
    internal static double ResolveEnd(DateTime? picked, bool touchedByOperator, DateTime now)
    {
      if (picked is not { } value)
      {
        return NoBound;
      }

      var through = value.Date;
      if (!touchedByOperator && now.Date > through)
      {
        through = now.Date;
      }

      return DateUtil.ToDotNetSeconds(through.AddDays(1));
    }

    /*
     * Does the range cover any of [spanBegin, spanEnd)? Half-open against half-open: touching edges do not overlap, so
     * a range ending at Dec 1 00:00 covers November and not December. A 0 edge means unbounded on that side, exactly as
     * the two dials have always meant it.
     */
    internal static bool Overlaps(double rangeBegin, double rangeEnd, double spanBegin, double spanEnd) =>
      (rangeBegin == NoBound || rangeBegin < spanEnd) && (rangeEnd == NoBound || rangeEnd > spanBegin);
  }
}
