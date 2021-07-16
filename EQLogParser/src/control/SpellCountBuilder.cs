using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  class SpellCountBuilder
  {
    public const int BUFF_OFFSET = 30;
    public const int DMG_OFFSET = 5;
    internal static SpellCountData GetSpellCounts(List<string> playerList, PlayerStats raidStats)
    {
      var result = new SpellCountData();
      HashSet<TimedAction> castsDuring = new HashSet<TimedAction>();
      HashSet<TimedAction> receivedDuring = new HashSet<TimedAction>();
      QuerySpellBlocks(raidStats, castsDuring, receivedDuring);

      foreach (var action in castsDuring.AsParallel().Where(cast => playerList.Contains((cast as SpellCast).Caster)))
      {
        SpellCast cast = action as SpellCast;
        if (cast.SpellData != null)
        {
          UpdateMaps(cast.SpellData, cast.Caster, result.PlayerCastCounts, result.PlayerInterruptedCounts, result.MaxCastCounts, result.UniqueSpells, cast.Interrupted);
        }
      }

      foreach (var action in receivedDuring.AsParallel().Where(received => playerList.Contains((received as ReceivedSpell).Receiver)))
      {
        ReceivedSpell received = action as ReceivedSpell;

        // dont include detrimental received spells since they're mostly things like being nuked
        if (received.SpellData != null && received.SpellData.IsBeneficial)
        {
          UpdateMaps(received.SpellData, received.Receiver, result.PlayerReceivedCounts, null, result.MaxReceivedCounts, result.UniqueSpells);
        }
      }

      return result;
    }

    public static double QuerySpellBlocks(PlayerStats raidStats, HashSet<TimedAction> castsDuring, HashSet<TimedAction> receivedDuring = null)
    {
      // add spells to one hashset if only one is passed in
      if (receivedDuring == null)
      {
        receivedDuring = castsDuring;
      }

      double maxTime = -1;
      var startTime = double.NaN;

      raidStats.Ranges.TimeSegments.ForEach(segment =>
      {
        startTime = double.IsNaN(startTime) ? segment.BeginTime : Math.Min(startTime, segment.BeginTime);
        maxTime = maxTime == -1 ? segment.BeginTime + raidStats.TotalSeconds : maxTime;
        var blocks = DataManager.Instance.GetCastsDuring(segment.BeginTime - DMG_OFFSET, segment.EndTime);
        AddBlocks(raidStats, blocks, maxTime, castsDuring, true);
        blocks = DataManager.Instance.GetCastsDuring(segment.BeginTime - BUFF_OFFSET, segment.EndTime + BUFF_OFFSET / 2);
        AddBlocks(raidStats, blocks, maxTime, castsDuring);

        blocks = DataManager.Instance.GetReceivedSpellsDuring(segment.BeginTime - DMG_OFFSET, segment.EndTime + DMG_OFFSET);
        AddBlocks(raidStats, blocks, maxTime, receivedDuring, true);
        blocks = DataManager.Instance.GetReceivedSpellsDuring(segment.BeginTime - BUFF_OFFSET, segment.EndTime + BUFF_OFFSET / 2);
        AddBlocks(raidStats, blocks, maxTime, receivedDuring);
      });

      return startTime;
    }

    private static void AddBlocks(PlayerStats raidStats, List<ActionBlock> blocks, double maxTime, HashSet<TimedAction> actions, bool damageOnly = false)
    {
      blocks.ForEach(block =>
      {
        if (raidStats.MaxTime == raidStats.TotalSeconds || block.BeginTime <= maxTime)
        {
          block.Actions.ForEach(action =>
          {
            if (action is SpellCast cast && (!damageOnly || cast.SpellData.Damaging != 0))
            {
              actions.Add(cast);
            }
            else if (action is ReceivedSpell received)
            {
              if (received.SpellData == null && received.Ambiguity.Count > 0 && DataManager.ResolveSpellAmbiguity(received, out SpellData replaced))
              {
                received.SpellData = replaced;
              }

              if (received.SpellData != null && (!damageOnly || received.SpellData.Damaging != 0))
              {
                actions.Add(received);
              }
            }
          });
        }
      });
    }

    private static void UpdateMaps(SpellData theSpell, string thePlayer, Dictionary<string, Dictionary<string, uint>> playerCounts,
      Dictionary<string, Dictionary<string, uint>> interruptedCounts, Dictionary<string, uint> maxSpellCounts, Dictionary<string, SpellData> spellMap, bool interrupted = false)
    {
      if (!playerCounts.ContainsKey(thePlayer))
      {
        playerCounts[thePlayer] = new Dictionary<string, uint>();
      }

      if (!playerCounts[thePlayer].ContainsKey(theSpell.ID))
      {
        playerCounts[thePlayer][theSpell.ID] = 0;
      }

      playerCounts[thePlayer][theSpell.ID] += interrupted ? 0u : 1;

      if (interruptedCounts != null)
      {
        if (!interruptedCounts.ContainsKey(thePlayer))
        {
          interruptedCounts[thePlayer] = new Dictionary<string, uint>();
        }

        if (!interruptedCounts[thePlayer].ContainsKey(theSpell.NameAbbrv))
        {
          interruptedCounts[thePlayer][theSpell.NameAbbrv] = 0;
        }

        interruptedCounts[thePlayer][theSpell.NameAbbrv] += interrupted ? 1u : 0;
      }

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
