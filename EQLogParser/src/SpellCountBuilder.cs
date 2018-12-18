using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  class SpellCountBuilder
  {
    public static SpellCounts GetSpellCounts(List<string> playerList, DateTime beginTime, DateTime endTime)
    {
      Dictionary<string, int> uniqueSpells = new Dictionary<string, int>();
      Dictionary<string, Dictionary<string, int>> playerCounts = new Dictionary<string, Dictionary<string, int>>();
      Dictionary<string, int> totalCountMap = new Dictionary<string, int>();

      foreach (var cast in GetCastsDuring(beginTime, endTime).AsParallel().Where(cast => playerList.Contains(cast.Caster)))
      {
        if (!uniqueSpells.ContainsKey(cast.SpellAbbrv))
        {
          uniqueSpells[cast.SpellAbbrv] = 0;
        }

        uniqueSpells[cast.SpellAbbrv]++;

        if (!playerCounts.ContainsKey(cast.Caster))
        {
          playerCounts[cast.Caster] = new Dictionary<string, int>();
        }

        if (!playerCounts[cast.Caster].ContainsKey(cast.SpellAbbrv))
        {
          playerCounts[cast.Caster][cast.SpellAbbrv] = 0;
        }

        playerCounts[cast.Caster][cast.SpellAbbrv]++;

        if (!totalCountMap.ContainsKey(cast.Caster))
        {
          totalCountMap[cast.Caster] = 0;
        }

        totalCountMap[cast.Caster]++;
      }

      SpellCounts totals = new SpellCounts() { PlayerCountMap = playerCounts, TotalCountMap = totalCountMap };
      totals.SpellList = uniqueSpells.Keys.OrderByDescending(key => uniqueSpells[key]).ToList();
      totals.SortedPlayers = playerCounts.Keys.OrderByDescending(key => totalCountMap[key]).ToList();
      return totals;
    }

    private static List<SpellCast> GetCastsDuring(DateTime beginTime, DateTime endTime)
    {
      List<SpellCast> allCasts = DataManager.Instance.GetSpellCasts();
      SpellCast startCast = new SpellCast() { BeginTime = beginTime };
      SpellCast endCast = new SpellCast() { BeginTime = endTime.AddSeconds(1) };
      CastComparer comparer = new CastComparer();

      int startIndex = allCasts.BinarySearch(startCast, comparer);
      if (startIndex < 0)
      {
        startIndex = Math.Abs(startIndex) - 1;
      }

      int endIndex = allCasts.BinarySearch(endCast, comparer);
      if (endIndex < 0)
      {
        endIndex = Math.Abs(endIndex) - 1;
      }

      return allCasts.GetRange(startIndex, endIndex - startIndex);
    }

    private class CastComparer : IComparer<SpellCast>
    {
      public int Compare(SpellCast x, SpellCast y)
      {
        return x.BeginTime.CompareTo(y.BeginTime);
      }
    }
  }
}
