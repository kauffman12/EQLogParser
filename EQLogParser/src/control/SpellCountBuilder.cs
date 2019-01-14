using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  class SpellCountBuilder
  {
    public static SpellCountData GetSpellCounts(List<string> playerList, DateTime beginTime, DateTime endTime)
    {
      Dictionary<string, Dictionary<string, int>> playerCastCounts = new Dictionary<string, Dictionary<string, int>>();
      Dictionary<string, Dictionary<string, int>> playerReceivedCounts = new Dictionary<string, Dictionary<string, int>>();
      Dictionary<string, int> maxCastCounts = new Dictionary<string, int>();
      Dictionary<string, int> maxReceivedCounts = new Dictionary<string, int>();
      Dictionary<string, SpellData> spellMap = new Dictionary<string, SpellData>();

      foreach (var cast in GetCastsDuring(beginTime, endTime).AsParallel().Where(cast => playerList.Contains(cast.Caster)))
      {
        var spellData = DataManager.Instance.GetSpellByAbbrv(cast.SpellAbbrv);
        UpdateMaps(spellData, cast.Caster, playerCastCounts, maxCastCounts, spellMap);
      }

      foreach (var received in GetReceivedSpellsDuring(beginTime, endTime).AsParallel().Where(received => playerList.Contains(received.Receiver)))
      {
        UpdateMaps(received.SpellData, received.Receiver, playerReceivedCounts, maxReceivedCounts, spellMap);
      }

      return new SpellCountData()
      {
        PlayerCastCounts = playerCastCounts,
        PlayerReceivedCounts = playerReceivedCounts,
        MaxCastCounts = maxCastCounts,
        MaxReceivedCounts = maxReceivedCounts,
        UniqueSpells = spellMap
      };
    }

    private static void UpdateMaps(SpellData theSpell, string thePlayer, Dictionary<string, Dictionary<string, int>> playerCounts,
      Dictionary<string, int> maxSpellCounts, Dictionary<string, SpellData> spellMap)
    {
      if (!playerCounts.ContainsKey(thePlayer))
      {
        playerCounts[thePlayer] = new Dictionary<string, int>();
      }

      if (!playerCounts[thePlayer].ContainsKey(theSpell.ID))
      {
        playerCounts[thePlayer][theSpell.ID] = 0;
      }

      playerCounts[thePlayer][theSpell.ID]++;

      if (!maxSpellCounts.ContainsKey(theSpell.ID))
      {
        maxSpellCounts[theSpell.ID] = playerCounts[thePlayer][theSpell.ID];
      }
      else if (playerCounts[thePlayer][theSpell.ID] > maxSpellCounts[theSpell.ID])
      {
        maxSpellCounts[theSpell.ID] = playerCounts[thePlayer][theSpell.ID];
      }

      spellMap[theSpell.ID] = theSpell;
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
