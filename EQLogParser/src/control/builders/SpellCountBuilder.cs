using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  static class SpellCountBuilder
  {
    public const int BUFF_OFFSET = 30;
    public const int DMG_OFFSET = 5;
    internal static SpellCountData GetSpellCounts(List<string> playerList, PlayerStats raidStats)
    {
      var result = new SpellCountData();
      var castsDuring = new HashSet<TimedAction>();
      var receivedDuring = new HashSet<TimedAction>();
      QuerySpellBlocks(raidStats, castsDuring, receivedDuring);

      foreach (var action in castsDuring.AsParallel().Where(cast => playerList.Contains((cast as SpellCast).Caster)))
      {
        var cast = action as SpellCast;
        if (cast.SpellData != null)
        {
          UpdateMaps(cast.SpellData, cast.Caster, result.PlayerCastCounts, result.PlayerInterruptedCounts, result.MaxCastCounts, result.UniqueSpells, cast.Interrupted);
        }
      }

      foreach (var action in receivedDuring.AsParallel().Where(received => playerList.Contains((received as ReceivedSpell).Receiver)))
      {
        var received = action as ReceivedSpell;

        // dont include detrimental received spells since they're mostly things like being nuked
        if (received.SpellData != null)
        {
          UpdateMaps(received.SpellData, received.Receiver, result.PlayerReceivedCounts, null, result.MaxReceivedCounts, result.UniqueSpells);
        }
      }

      return result;
    }

    public static double QuerySpellBlocks(PlayerStats raidStats, HashSet<TimedAction> castsDuring, HashSet<TimedAction> receivedDuring = null)
    {
      // add spells to one hashset if only one is passed in
      receivedDuring ??= castsDuring;

      double maxTime = -1;
      var startTime = double.NaN;

      raidStats.Ranges.TimeSegments.ForEach(segment =>
      {
        var damageAfter = segment.BeginTime - DMG_OFFSET;
        var damageBefore = segment.EndTime;

        startTime = double.IsNaN(startTime) ? segment.BeginTime : Math.Min(startTime, segment.BeginTime);
        maxTime = maxTime == -1 ? segment.BeginTime + raidStats.TotalSeconds : maxTime;

        // damage section is a subset of this query
        var blocks = DataManager.Instance.GetCastsDuring(segment.BeginTime - BUFF_OFFSET, segment.EndTime + (BUFF_OFFSET / 2));
        AddGroups(raidStats, blocks, maxTime, castsDuring, damageAfter, damageBefore);

        blocks = DataManager.Instance.GetReceivedSpellsDuring(segment.BeginTime - BUFF_OFFSET, segment.EndTime + (BUFF_OFFSET / 2));
        AddGroups(raidStats, blocks, maxTime, receivedDuring, damageAfter, damageBefore);
      });

      return startTime;
    }

    private static void AddGroups(PlayerStats raidStats, List<ActionGroup> blocks, double maxTime, HashSet<TimedAction> actions, double damageAfter,
      double damageBefore)
    {
      blocks.ForEach(block =>
      {
        if (raidStats.MaxTime == raidStats.TotalSeconds || block.BeginTime <= maxTime)
        {
          block.Actions.ForEach(action =>
          {
            if (action is SpellCast cast)
            {
              Add(block, cast.SpellData, cast);
            }
            else if (action is ReceivedSpell received)
            {
              if (received.SpellData == null && received.Ambiguity.Count > 0 && DataManager.ResolveSpellAmbiguity(received, out var replaced))
              {
                received.SpellData = replaced;
              }

              if (received.SpellData != null)
              {
                Add(block, received.SpellData, received);
              }
            }
          });
        }
      });

      void Add(ActionGroup block, SpellData spellData, TimedAction action)
      {
        if ((block.BeginTime >= damageAfter && block.BeginTime <= damageBefore) || spellData.Damaging < 1)
        {
          actions.Add(action);
        }
      }
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
