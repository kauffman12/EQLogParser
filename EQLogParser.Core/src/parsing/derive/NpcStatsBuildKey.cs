using System.Collections.Generic;

namespace EQLogParser
{
  internal static class NpcStatsBuildKey
  {
    // Snapshot routing, live stream prefixes, zone boundaries and scope all affect this answer. A hand's Refresh is new work.
    public static long For(int sessionId, long contentStamp, int damageCount, int healCount, int zoneCount,
      IReadOnlyList<DerivedFight> selected, long generation, long refreshGeneration)
    {
      var hash = unchecked((contentStamp * 397) ^ sessionId);
      hash = unchecked(hash * 31 + damageCount);
      hash = unchecked(hash * 31 + healCount);
      hash = unchecked(hash * 31 + zoneCount);
      hash = unchecked(hash * 31 + generation);
      hash = unchecked(hash * 31 + refreshGeneration);
      hash = unchecked(hash * 31 + (selected is null ? 0 : 1));
      if (selected is not null)
      {
        foreach (var fight in selected) hash = unchecked(hash * 17 + fight.Id);
      }

      return hash;
    }
  }
}
