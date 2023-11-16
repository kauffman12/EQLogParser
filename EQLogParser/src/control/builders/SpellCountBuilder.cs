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
      var castsDuring = new HashSet<IAction>();
      var receivedDuring = new HashSet<IAction>();
      QuerySpells(raidStats, castsDuring, receivedDuring);

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

    public static double QuerySpells(PlayerStats raidStats, HashSet<IAction> castsDuring,
      HashSet<IAction> receivedDuring, Dictionary<IAction, double> times = null)
    {
      var maxTime = double.NaN;
      var startTime = double.NaN;
      foreach (ref var segment in raidStats.Ranges.TimeSegments.ToArray().AsSpan())
      {
        var damageAfter = segment.BeginTime - DMG_OFFSET;
        var damageBefore = segment.EndTime;

        startTime = double.IsNaN(startTime) ? segment.BeginTime : Math.Min(startTime, segment.BeginTime);
        maxTime = double.IsNaN(maxTime) ? segment.BeginTime + raidStats.TotalSeconds : maxTime;

        foreach (var (beginTime, spell) in RecordManager.Instance.GetSpellsDuring(segment.BeginTime - BUFF_OFFSET, segment.EndTime + HALF_OFFSET))
        {
          if (times != null)
          {
            times[spell] = beginTime;
          }

          if (spell is ReceivedSpell)
          {
            AddSpell(raidStats, spell, beginTime, maxTime, receivedDuring, damageAfter, damageBefore);
          }
          else
          {
            AddSpell(raidStats, spell, beginTime, maxTime, castsDuring, damageAfter, damageBefore);
          }
        }
      }

      return startTime;
    }

    private static void AddSpell(PlayerStats raidStats, IAction action, double beginTime, double maxTime,
      ISet<IAction> actions, double damageAfter, double damageBefore)
    {
      if (!raidStats.MaxTime.Equals(raidStats.TotalSeconds) && !(beginTime <= maxTime))
      {
        return;
      }

      if (action is SpellCast cast)
      {
        Add(beginTime, cast.SpellData, cast);
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
          Add(beginTime, received.SpellData, received);
        }
      }

      return;

      void Add(double beginTime, SpellData spellData, IAction action)
      {
        if ((beginTime >= damageAfter && beginTime <= damageBefore) || spellData.Damaging < 1)
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
