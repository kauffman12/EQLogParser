using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  class SpellCountBuilder
  {
    private const int SPELL_TIME_OFFSET = 10; // seconds back

    public static SpellCountData GetSpellCounts(List<string> playerList, PlayerStats raidStats)
    {
      Dictionary<string, Dictionary<string, int>> playerCastCounts = new Dictionary<string, Dictionary<string, int>>();
      Dictionary<string, Dictionary<string, int>> playerReceivedCounts = new Dictionary<string, Dictionary<string, int>>();
      Dictionary<string, int> maxCastCounts = new Dictionary<string, int>();
      Dictionary<string, int> maxReceivedCounts = new Dictionary<string, int>();
      Dictionary<string, SpellData> spellMap = new Dictionary<string, SpellData>();

      DateTime beginTime = raidStats.BeginTimes.First().AddSeconds(-SPELL_TIME_OFFSET);
      DateTime endTime = raidStats.LastTimes.Last();

      foreach (var cast in DataManager.Instance.GetCastsDuring(beginTime, endTime).AsParallel().Where(cast => playerList.Contains(cast.Caster)))
      {
        var spellData = DataManager.Instance.GetSpellByAbbrv(cast.SpellAbbrv);
        if (spellData != null)
        {
          UpdateMaps(spellData, cast.Caster, playerCastCounts, maxCastCounts, spellMap);
        }
      }

      foreach (var received in DataManager.Instance.GetReceivedSpellsDuring(beginTime, endTime).AsParallel().Where(received => playerList.Contains(received.Receiver)))
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

    public static List<string> GetPlayersCastingDuring(PlayerStats raidStats)
    {
      List<string> results = null;
      if (raidStats.BeginTimes.Count > 0 && raidStats.LastTimes.Count > 0)
      {
        DateTime beginTime = raidStats.BeginTimes.First().AddSeconds(-SPELL_TIME_OFFSET);
        DateTime endTime = raidStats.LastTimes.Last();
        List<SpellCast> casts = DataManager.Instance.GetCastsDuring(beginTime, endTime);
        results = casts.Select(cast => cast.Caster).Distinct().ToList();
      }
      else
      {
        results = new List<string>();
      }
      return results;
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
  }
}
