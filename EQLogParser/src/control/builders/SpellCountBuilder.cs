using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  internal static class SpellCountBuilder
  {
    public const double BUFF_OFFSET = 30d;
    public const double HALF_OFFSET = BUFF_OFFSET / 2;
    public const double DMG_OFFSET = 5d;

    internal static SpellCountData GetSpellCounts(List<string> playerList, PlayerStats raidStats)
    {
      var result = new SpellCountData();
      var castsDuring = new HashSet<TimedAction>();
      var receivedDuring = new HashSet<TimedAction>();
      QuerySpellBlocks(raidStats, castsDuring, receivedDuring);

      foreach (var action in castsDuring.Where(cast => playerList.Contains((cast as SpellCast)?.Caster)))
      {
        if (action is SpellCast { SpellData: not null } cast)
        {
          UpdateMaps(cast.SpellData, cast.Caster, result.PlayerCastCounts, result.PlayerInterruptedCounts, result.MaxCastCounts,
            result.UniqueSpells, cast.Interrupted);
        }
      }

      foreach (var action in receivedDuring.Where(received => playerList.Contains((received as ReceivedSpell)?.Receiver)))
      {
        // dont include detrimental received spells since they're mostly things like being nuked
        if (action is ReceivedSpell { SpellData: not null, IsWearOff: false } received)
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

      var maxTime = double.NaN;
      var startTime = double.NaN;
      foreach (ref var segment in raidStats.Ranges.TimeSegments.ToArray().AsSpan())
      {
        var damageAfter = segment.BeginTime - DMG_OFFSET;
        var damageBefore = segment.EndTime;

        startTime = double.IsNaN(startTime) ? segment.BeginTime : Math.Min(startTime, segment.BeginTime);
        maxTime = double.IsNaN(maxTime) ? segment.BeginTime + raidStats.TotalSeconds : maxTime;

        // damage section is a subset of this query
        var blocks = DataManager.Instance.GetCastsDuring(segment.BeginTime - BUFF_OFFSET, segment.EndTime + HALF_OFFSET);
        AddGroups(raidStats, blocks, maxTime, castsDuring, damageAfter, damageBefore);

        blocks = DataManager.Instance.GetReceivedSpellsDuring(segment.BeginTime - BUFF_OFFSET, segment.EndTime + HALF_OFFSET);
        AddGroups(raidStats, blocks, maxTime, receivedDuring, damageAfter, damageBefore);
      }

      return startTime;
    }

    private static void AddGroups(PlayerStats raidStats, List<ActionGroup> blocks, double maxTime,
      ISet<TimedAction> actions, double damageAfter, double damageBefore)
    {
      foreach (ref var block in blocks.ToArray().AsSpan())
      {
        if (!StatsUtil.DoubleEquals(raidStats.MaxTime, raidStats.TotalSeconds) && !(block.BeginTime <= maxTime))
        {
          continue;
        }

        foreach (ref var action in block.Actions.ToArray().AsSpan())
        {
          if (action is SpellCast cast)
          {
            Add(block, cast.SpellData, cast);
          }
          else if (action is ReceivedSpell { IsWearOff: false } received)
          {
            if (received.SpellData == null && received.Ambiguity.Count > 0 &&
                DataManager.ResolveSpellAmbiguity(received, out var replaced))
            {
              received.SpellData = replaced;
            }

            if (received.SpellData != null)
            {
              Add(block, received.SpellData, received);
            }
          }
        }
      }

      return;

      void Add(TimedAction block, SpellData spellData, TimedAction action)
      {
        if ((block.BeginTime >= damageAfter && block.BeginTime <= damageBefore) || spellData.Damaging < 1)
        {
          actions.Add(action);
        }
      }
    }

    private static void UpdateMaps(SpellData theSpell, string thePlayer, IDictionary<string, Dictionary<string, uint>> playerCounts,
      IDictionary<string, Dictionary<string, uint>> interruptedCounts, IDictionary<string, uint> maxSpellCounts,
      IDictionary<string, SpellData> spellMap, bool interrupted = false)
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
