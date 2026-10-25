using System.Collections.Generic;

namespace EQLogParser
{
  // Keeps a prefix of both parallel lists, preserving each line's source position. The viewer prepends file batches,
  // so a late delivery may displace previously retained results; cancellation does not prevent those deliveries.
  // docs/DesignNotes.md → "Log search retains a paired prefix, not the first deliveries".
  internal static class SearchResultCap
  {
    /// <summary>Keeps both parallel lists at most `maxRows` long, dropping the tail. No-op when neither list exceeds it.</summary>
    internal static void TrimToCap<TLine, TPosition>(List<TLine> lines, List<TPosition> positions, int maxRows)
    {
      TrimTail(lines, maxRows);
      TrimTail(positions, maxRows);
    }

    private static void TrimTail<T>(List<T> items, int maxRows)
    {
      if (items.Count > maxRows)
      {
        items.RemoveRange(maxRows, items.Count - maxRows);
      }
    }
  }
}
