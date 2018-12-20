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
        string theSpell = cast.SpellAbbrv;
        string thePlayer = cast.Caster;
        UpdateMaps(theSpell, thePlayer, uniqueSpells, playerCounts, totalCountMap);
      }

      foreach (var received in GetReceivedSpellsDuring(beginTime, endTime).AsParallel().Where(received => playerList.Contains(received.Receiver)))
      {
        string theSpell = "Received " + received.SpellAbbrv;
        string thePlayer = received.Receiver;
        UpdateMaps(theSpell, thePlayer, uniqueSpells, playerCounts, totalCountMap);
      }

      SpellCounts totals = new SpellCounts() { PlayerCountMap = playerCounts, TotalCountMap = totalCountMap };
      totals.SpellList = uniqueSpells.Keys.OrderByDescending(key => uniqueSpells[key]).ToList();
      totals.SortedPlayers = playerCounts.Keys.OrderByDescending(key => totalCountMap[key]).ToList();
      totals.UniqueSpellCounts = uniqueSpells;
      return totals;
    }

    private static void UpdateMaps(string theSpell, string thePlayer, Dictionary<string, int> uniqueSpells,
      Dictionary<string, Dictionary<string, int>> playerCounts, Dictionary<string, int> totalCountMap)
    {
      if (!uniqueSpells.ContainsKey(theSpell))
      {
        uniqueSpells[theSpell] = 0;
      }

      uniqueSpells[theSpell]++;

      if (!playerCounts.ContainsKey(thePlayer))
      {
        playerCounts[thePlayer] = new Dictionary<string, int>();
      }

      if (!playerCounts[thePlayer].ContainsKey(theSpell))
      {
        playerCounts[thePlayer][theSpell] = 0;
      }

      playerCounts[thePlayer][theSpell]++;

      if (!totalCountMap.ContainsKey(thePlayer))
      {
        totalCountMap[thePlayer] = 0;
      }

      totalCountMap[thePlayer]++;
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

    private static List<ReceivedSpell> GetReceivedSpellsDuring(DateTime beginTime, DateTime endTime)
    {
      List<ReceivedSpell> allReceivedSpells = DataManager.Instance.GetAllReceivedSpells();
      ReceivedSpell startReceived = new ReceivedSpell() { BeginTime = beginTime };
      ReceivedSpell endReceived = new ReceivedSpell() { BeginTime = endTime.AddSeconds(1) };
      CastComparer comparer = new CastComparer();

      int startIndex = allReceivedSpells.BinarySearch(startReceived, comparer);
      if (startIndex < 0)
      {
        startIndex = Math.Abs(startIndex) - 1;
      }

      int endIndex = allReceivedSpells.BinarySearch(endReceived, comparer);
      if (endIndex < 0)
      {
        endIndex = Math.Abs(endIndex) - 1;
      }

      return allReceivedSpells.GetRange(startIndex, endIndex - startIndex);
    }

    private class CastComparer : IComparer<ReceivedSpell>
    {
      public int Compare(ReceivedSpell x, ReceivedSpell y)
      {
        return x.BeginTime.CompareTo(y.BeginTime);
      }
    }
  }
}
