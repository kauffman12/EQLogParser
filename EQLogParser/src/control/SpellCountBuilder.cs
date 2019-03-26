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
      Dictionary<string, Dictionary<string, uint>> playerCastCounts = new Dictionary<string, Dictionary<string, uint>>();
      Dictionary<string, Dictionary<string, uint>> playerReceivedCounts = new Dictionary<string, Dictionary<string, uint>>();
      Dictionary<string, uint> maxCastCounts = new Dictionary<string, uint>();
      Dictionary<string, uint> maxReceivedCounts = new Dictionary<string, uint>();
      Dictionary<string, SpellData> spellMap = new Dictionary<string, SpellData>();

      var offsets = GetOffsetTimes(raidStats);
      var begins = offsets.Item1;
      var lasts = offsets.Item2;

      List<IAction> castsDuring = new List<IAction>();
      List<IAction> receivedDuring = new List<IAction>();
      for (int i = 0; i < begins.Count; i++)
      {
        var blocks = DataManager.Instance.GetCastsDuring(begins[i], lasts[i]);
        blocks.ForEach(block => castsDuring.AddRange(block.Actions));
        blocks = DataManager.Instance.GetReceivedSpellsDuring(begins[i], lasts[i]);
        blocks.ForEach(block => receivedDuring.AddRange(block.Actions));
      }

      foreach (var action in castsDuring.AsParallel().Where(cast => playerList.Contains((cast as SpellCast).Caster)))
      {
        SpellCast cast = action as SpellCast;
        var spellData = DataManager.Instance.GetSpellByAbbrv(Helpers.AbbreviateSpellName(cast.Spell));
        if (spellData != null)
        {
          UpdateMaps(spellData, cast.Caster, playerCastCounts, maxCastCounts, spellMap);
        }
      }

      foreach (var action in receivedDuring.AsParallel().Where(received => playerList.Contains((received as ReceivedSpell).Receiver)))
      {
        ReceivedSpell received = action as ReceivedSpell;
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

        List<IAction> actions = new List<IAction>();
        for (int i = 0; i < begins.Count; i++)
        {
          var blocks = DataManager.Instance.GetCastsDuring(begins[i], lasts[i]);
          blocks.ForEach(block => actions.AddRange(block.Actions));
        }

        results = actions.Select(action => (action as SpellCast).Caster).Distinct().ToList();
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

    private static void UpdateMaps(SpellData theSpell, string thePlayer, Dictionary<string, Dictionary<string, uint>> playerCounts,
      Dictionary<string, uint> maxSpellCounts, Dictionary<string, SpellData> spellMap)
    {
      if (!playerCounts.ContainsKey(thePlayer))
      {
        playerCounts[thePlayer] = new Dictionary<string, uint>();
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
