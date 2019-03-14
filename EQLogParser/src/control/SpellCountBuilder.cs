using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  class SpellCountBuilder
  {
    private const int SPELL_TIME_OFFSET = 10; // seconds back

    internal static SpellCountData GetSpellCounts(List<string> playerList, PlayerStats raidStats)
    {
      Dictionary<string, Dictionary<string, int>> playerCastCounts = new Dictionary<string, Dictionary<string, int>>();
      Dictionary<string, Dictionary<string, int>> playerReceivedCounts = new Dictionary<string, Dictionary<string, int>>();
      Dictionary<string, int> maxCastCounts = new Dictionary<string, int>();
      Dictionary<string, int> maxReceivedCounts = new Dictionary<string, int>();
      Dictionary<string, SpellData> spellMap = new Dictionary<string, SpellData>();

      var offsets = GetOffsetTimes(raidStats);
      var begins = offsets.Item1;
      var lasts = offsets.Item2;

      List<TimedAction> castsDuring = new List<TimedAction>();
      List<TimedAction> receivedDuring = new List<TimedAction>();
      for (int i = 0; i < begins.Count; i++)
      {
        castsDuring.AddRange(DataManager.Instance.GetCastsDuring(begins[i], lasts[i]));
        receivedDuring.AddRange(DataManager.Instance.GetReceivedSpellsDuring(begins[i], lasts[i]));
      }

      foreach (var timedAction in castsDuring.Where(cast => playerList.Contains((cast as SpellCast).Caster)))
      {
        SpellCast cast = timedAction as SpellCast;
        var spellData = DataManager.Instance.GetSpellByAbbrv(cast.SpellAbbrv);
        if (spellData != null)
        {
          UpdateMaps(spellData, cast.Caster, playerCastCounts, maxCastCounts, spellMap);
        }
      }

      foreach (var timedAction in receivedDuring.AsParallel().Where(received => playerList.Contains((received as ReceivedSpell).Receiver)))
      {
        ReceivedSpell received = timedAction as ReceivedSpell;
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

    internal static List<string> GetPlayersCastingDuring(PlayerStats raidStats)
    {
      List<string> results = null;
      if (raidStats.BeginTimes.Count > 0 && raidStats.LastTimes.Count > 0 && raidStats.BeginTimes.Count == raidStats.LastTimes.Count)
      {
        var offsets = GetOffsetTimes(raidStats);
        var begins = offsets.Item1;
        var lasts = offsets.Item2;

        List<TimedAction> timedActions = new List<TimedAction>();
        for (int i = 0; i < begins.Count; i++)
        {
          timedActions.AddRange(DataManager.Instance.GetCastsDuring(begins[i], lasts[i]));
        }

        results = timedActions.Select(action => (action as SpellCast).Caster).Distinct().ToList();
      }
      else
      {
        results = new List<string>();
      }
      return results;
    }

    private static Tuple<List<double>, List<double>> GetOffsetTimes(PlayerStats raidStats)
    {
      List<double> begins = new List<double>();
      List<double> lasts = new List<double>();
      begins.Add(raidStats.BeginTimes.First() - SPELL_TIME_OFFSET);
      lasts.Add(raidStats.LastTimes.First());

      for (int i = 1; i < raidStats.BeginTimes.Count; i++)
      {
        int current = begins.Count - 1;
        if (lasts[current] >= raidStats.BeginTimes[i])
        {
          var offsetLastTime = raidStats.LastTimes[i];
          if (offsetLastTime > lasts[current])
          {
            lasts[current] = offsetLastTime;
          }
        }
        else
        {
          begins.Add(raidStats.BeginTimes[i] - SPELL_TIME_OFFSET);
          lasts.Add(raidStats.LastTimes[i]);
        }
      }

      return new Tuple<List<double>, List<double>>(begins, lasts);
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
