using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EQLogParser
{
  internal static class SpellCountBuilder
  {
    public const double BuffOffset = 30d;
    public const double HalfOffset = BuffOffset / 2;
    public const double DmgOffset = 5d;

    internal static SpellCountData GetSpellCounts(List<string> playerList, PlayerStats raidStats)
    {
      var result = new SpellCountData();
      var castsDuring = new HashSet<IAction>();
      var receivedDuring = new HashSet<IAction>();
      QuerySpells(raidStats, castsDuring, receivedDuring);

      foreach (var action in castsDuring)
      {
        if (action is SpellCast { SpellData: not null } cast)
        {
          if ((playerList != null && playerList.Contains(cast.Caster)) || (playerList == null && PlayerManager.Instance.IsVerifiedPlayer(cast.Caster)))
          {
            UpdateMaps(cast.SpellData, cast.Caster, result.PlayerCastCounts, result.PlayerInterruptedCounts, result.MaxCastCounts,
              result.UniqueSpells, cast.Interrupted);
            result.UniquePlayers[cast.Caster] = true;
          }
        }
      }

      foreach (var action in receivedDuring)
      {
        // don't include detrimental received spells since they're mostly things like being nuked
        if (action is ReceivedSpell { SpellData: not null, IsWearOff: false } received)
        {
          if ((playerList != null && playerList.Contains(received.Receiver)) || (playerList == null && PlayerManager.Instance.IsVerifiedPlayer(received.Receiver)))
          {
            UpdateMaps(received.SpellData, received.Receiver, result.PlayerReceivedCounts, null, result.MaxReceivedCounts, result.UniqueSpells);
            result.UniquePlayers[received.Receiver] = true;
          }
        }
      }

      return result;
    }

    public static double QuerySpells(PlayerStats raidStats, HashSet<IAction> castsDuring,
      HashSet<IAction> receivedDuring, Dictionary<IAction, double> times = null)
    {
      var maxTime = double.NaN;
      var startTime = double.NaN;
      foreach (var segment in CollectionsMarshal.AsSpan(raidStats.AllRanges.TimeSegments))
      {
        var damageAfter = segment.BeginTime - DmgOffset;
        var damageBefore = segment.EndTime;

        startTime = double.IsNaN(startTime) ? segment.BeginTime : Math.Min(startTime, segment.BeginTime);
        maxTime = double.IsNaN(maxTime) ? segment.BeginTime + raidStats.TotalSeconds : maxTime;

        foreach (var (beginTime, spell) in RecordManager.Instance.GetSpellsDuring(segment.BeginTime - BuffOffset, segment.EndTime + HalfOffset))
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
      HashSet<IAction> actions, double damageAfter, double damageBefore)
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

      void Add(double theTime, SpellData spellData, IAction theAction)
      {
        if ((theTime >= damageAfter && theTime <= damageBefore) || spellData == null || spellData.Damaging < 1)
        {
          actions.Add(theAction);
        }
      }
    }

    private static void UpdateMaps(SpellData theSpell, string thePlayer, Dictionary<string, Dictionary<string, uint>> playerCounts,
      Dictionary<string, Dictionary<string, uint>> interruptedCounts, Dictionary<string, uint> maxSpellCounts,
      Dictionary<string, SpellData> spellMap, bool interrupted = false)
    {
      if (!playerCounts.TryGetValue(thePlayer, out var counts))
      {
        counts = ([]);
        playerCounts[thePlayer] = counts;
      }

      counts.TryAdd(theSpell.Id, 0);
      counts[theSpell.Id] += interrupted ? 0u : 1;

      if (interruptedCounts != null)
      {
        if (!interruptedCounts.TryGetValue(thePlayer, out var interrupts))
        {
          interrupts = ([]);
          interruptedCounts[thePlayer] = interrupts;
        }

        interrupts.TryAdd(theSpell.NameAbbrv, 0);
        interrupts[theSpell.NameAbbrv] += interrupted ? 1u : 0;
      }

      if (!maxSpellCounts.TryGetValue(theSpell.Id, out var maxCounts))
      {
        maxSpellCounts[theSpell.Id] = counts[theSpell.Id];
      }
      else if (counts[theSpell.Id] > maxCounts)
      {
        maxSpellCounts[theSpell.Id] = counts[theSpell.Id];
      }

      spellMap[theSpell.Id] = theSpell;
    }
  }
}
