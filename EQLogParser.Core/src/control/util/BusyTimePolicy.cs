using System;

namespace EQLogParser
{
  /*
   * Busy seconds: the distinct log seconds that carried at least one record this lane accepted, with runs of marked seconds welded
   * across silences of GapSeconds or less. Marks are taken from the block timestamps the builder's own walk sees, never from a stored
   * first-to-last span — a span cannot see the silence inside it, which is the whole point of the column.
   *
   * The cut is made INSIDE the seconds Sec already counts: Busy subtracts idle stretches from the same coverage TotalSeconds was read
   * out of and never adds to it, so a pause the row has no data for costs both columns instead of buying Busy time the capture never
   * recorded (marks 7-10 seconds apart across two fights, or between two different spells of one healer).
   *
   * The scratch set lives on the row and must be released before publication (Apply), since rows outlive the build in CombinedStats.
   * docs/DesignNotes.md → "Busy seconds: activity is measured from marks, never from a stored span".
   */
  internal static class BusyTimePolicy
  {
    /* The welding boundary: a distance of exactly this joins, one more splits. A heuristic on logged activity, not proof of attention. */
    internal const int GapSeconds = 10;

    // The grid column carrying the measurement, so the pane setup and the model cannot drift apart on the string.
    internal const string ColumnMappingName = "BusySeconds";

    /// <summary>Mark the whole log second of an accepted record onto its row. Repeated seconds cost nothing.</summary>
    internal static void Note(PlayerStats stats, double logTime)
    {
      // Blocks carry finite whole log seconds (.NET-epoch, ~64 billion for modern logs), so a long is the smallest safe mark.
      var mark = (long)logTime;

      // The gate only skips consecutive actions in the same second; the set is what deduplicates a revisited second.
      if (mark == stats.LastMarkSec)
        return;

      stats.LastMarkSec = mark;
      (stats.ActivityMarks ??= new HashSet<long>()).Add(mark);
    }

    /*
     * Total seconds of `marks` joined into runs, cut against `covered`. A run extends only while both ends sit inside the same covered
     * span, because that is exactly where Sec claims this row was present; a mark outside every span counts nothing, so Busy can only
     * ever come out smaller than the coverage it was cut from. Covered spans are ascending and disjoint — the invariant TimeRange.Add
     * keeps — which lets one cursor walk marks and spans together instead of searching per mark.
     */
    internal static long Weld(HashSet<long> marks, TimeRange covered, int gapSeconds = GapSeconds)
    {
      if (gapSeconds < 1)
        throw new ArgumentOutOfRangeException(nameof(gapSeconds), "A weld needs a positive gap.");

      if (marks is null || marks.Count == 0 || covered is null)
        return 0;

      var spans = covered.TimeSegments;
      if (spans.Count == 0)
        return 0;

      var sorted = new long[marks.Count];
      marks.CopyTo(sorted);
      Array.Sort(sorted);

      long total = 0;
      var span = 0;
      var runStart = 0L;
      var runEnd = 0L;
      var runSpan = -1;

      foreach (var mark in sorted)
      {
        while (span < spans.Count && mark > spans[span].EndTime)
          span++;

        // Marks ascend, so once the spans run out there is no coverage left for any of them.
        if (span == spans.Count)
          break;

        var current = spans[span];
        if (mark < current.BeginTime)
          continue;

        if (runSpan == span && mark - runEnd <= gapSeconds)
        {
          runEnd = mark;
          continue;
        }

        if (runSpan >= 0)
          total += runEnd - runStart + 1;

        runStart = mark;
        runEnd = mark;
        runSpan = span;
      }

      return runSpan >= 0 ? total + (runEnd - runStart + 1) : total;
    }

    /*
     * Publish the row's Busy seconds and release its scratch. `covered` is the very range this row's TotalSeconds came from, so the two
     * columns cannot disagree about which seconds exist and Busy is always Sec minus idle stretches. A row with no marks — or no
     * covered seconds to measure inside, which is the same row Sec reports as zero — measures zero.
     */
    internal static void Apply(PlayerStats stats, TimeRange covered)
    {
      var marks = stats.ActivityMarks;
      stats.ActivityMarks = null;
      stats.LastMarkSec = long.MinValue;

      stats.BusySeconds = marks is { Count: > 0 } ? Weld(marks, covered) : 0;
    }
  }
}
