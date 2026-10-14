using System;

namespace EQLogParser
{
    /*
     * The chat archive's date range: whole days with an EXCLUSIVE upper edge. An end date means "through that day", which only next-midnight
     * expresses — the old bound was the raw instant off the dial, stamped once at construction, so today aged out of its own range at midnight and no
     * refresh could bring it back (docs/DesignNotes.md → "A chat range is whole days").
     *
     * An end dial nobody touched means "up to now" and rides the clock into tomorrow; the moment an operator picks a date it is theirs and stops
     * moving. Which lines show inside a surviving day is untouched — per-line filtering never consulted time.
     */
  internal static class ChatDateRange
  {
    /* "Unbounded". Both dials answer 0 when they hold no value, and every comparison below reads 0 as "no edge here". */
    public const double NoBound = 0;

    /* The lower edge: the start of the picked day, inclusive — a person picking Mar 16 means that day is in. */
    internal static double ResolveStart(DateTime? picked) =>
      picked is { } value ? DateUtil.ToDotNetSeconds(value.Date) : NoBound;

        /*
         * The upper edge, exclusive: midnight ending the last covered day. An untouched dial follows now; a picked dial is frozen. When the two disagree
         * the later day wins, so a caller that forgets to stamp cannot re-create the bug this fixes.
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
