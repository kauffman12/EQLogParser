using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  class SpellCastManager
  {
    public void Add(SpellCast cast)
    {
      if (cast != null)
      {
        bool replaced;
        cast.Caster = DataManager.Instance.ReplaceAttacker(cast.Caster, out replaced);
        DataManager.Instance.AddSpellCast(cast);
      }
    }

    public SpellCounts GetSpellCounts(List<string> playerList, DateTime beginTime, DateTime endTime)
    {
      Dictionary<string, int> uniqueSpells = new Dictionary<string, int>();
      Dictionary<string, Dictionary<string, int>> playerCounts = new Dictionary<string, Dictionary<string, int>>(); 

      foreach (var cast in GetCastsDuring(playerList, beginTime, endTime))
      {
        if (!uniqueSpells.ContainsKey(cast.Spell))
        {
          uniqueSpells[cast.Spell] = 0;
        }

        uniqueSpells[cast.Spell]++;

        if (!playerCounts.ContainsKey(cast.Caster))
        {
          playerCounts[cast.Caster] = new Dictionary<string, int>();
        }

        if (!playerCounts[cast.Caster].ContainsKey(cast.Spell))
        {
          playerCounts[cast.Caster][cast.Spell] = 0;
        }

        playerCounts[cast.Caster][cast.Spell]++;
      }

      SpellCounts totals = new SpellCounts() { PlayerCountMap = playerCounts, TotalCountMap = uniqueSpells };
      totals.SpellList = uniqueSpells.Keys.OrderByDescending(key => uniqueSpells[key]).ToList();
      return totals;
    }

    private List<SpellCast> GetCastsDuring(List<string> playerList, DateTime beginTime, DateTime endTime)
    {
      List<SpellCast> allCasts = DataManager.Instance.GetSpellCasts();
      SpellCast startCast = new SpellCast() { BeginTime = beginTime };
      SpellCast endCast = new SpellCast() { BeginTime = endTime.AddSeconds(1) };
      CastComparer comparer = new CastComparer();
      Dictionary<string, byte> playerLookup = new Dictionary<string, byte>();

      // temp player dictionary
      foreach(string p in playerList)
      {
        playerLookup[p] = 1;
      }

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

      return allCasts.GetRange(startIndex, endIndex - startIndex).Where(cast => playerLookup.ContainsKey(cast.Caster)).ToList();
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
