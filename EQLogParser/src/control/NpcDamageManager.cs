using System;
using System.Globalization;

namespace EQLogParser
{
  class NpcDamageManager
  {
    public static event EventHandler<DamageProcessedEvent> EventsPlayerAttackProcessed;

    internal const int GROUP_TIMEOUT = 120;
    internal double LastFightProcessTime = double.NaN;

    private int CurrentNpcID = 0;
    private int CurrentGroupID = 0;

    public NpcDamageManager()
    {
      DamageLineParser.EventsDamageProcessed += HandleDamageProcessed;
      DataManager.Instance.EventsClearedActiveData += (sender, cleared) => CurrentGroupID = 0;
    }

    ~NpcDamageManager()
    {
      DamageLineParser.EventsDamageProcessed -= HandleDamageProcessed;
    }

    internal void ResetTime()
    {
      LastFightProcessTime = double.NaN;
    }

    private void HandleDamageProcessed(object sender, DamageProcessedEvent processed)
    {
      if (LastFightProcessTime != processed.BeginTime)
      {
        DataManager.Instance.CheckExpireFights(processed.BeginTime);
      }

      if (IsValidAttack(processed.Record, out bool defender))
      {
        if (!double.IsNaN(LastFightProcessTime))
        {
          var seconds = processed.BeginTime - LastFightProcessTime;
          if (seconds >= GROUP_TIMEOUT)
          {
            CurrentGroupID++;
          }
        }

        string origTimeString = processed.OrigTimeString.Substring(4, 15);

        Fight fight = Get(processed.Record, processed.BeginTime, origTimeString, defender);

        if (defender)
        {
          Helpers.AddAction(fight.DamageBlocks, processed.Record, processed.BeginTime);
          AddPlayerTime(fight, processed.Record, processed.Record.Attacker, processed.BeginTime);
          fight.Total += processed.Record.Total;
          fight.BeginDamageTime = double.IsNaN(fight.BeginDamageTime) ? processed.BeginTime : fight.BeginDamageTime;
          fight.LastDamageTime = processed.BeginTime;
          fight.DamageHits++;
        }
        else
        {
          Helpers.AddAction(fight.TankingBlocks, processed.Record, processed.BeginTime);
          AddPlayerTime(fight, processed.Record, processed.Record.Defender, processed.BeginTime);
          fight.BeginTankingTime = double.IsNaN(fight.BeginTankingTime) ? processed.BeginTime : fight.BeginTankingTime;
          fight.LastTankingTime = processed.BeginTime;
          fight.TankHits++;
        }

        fight.LastTime = processed.BeginTime;
        LastFightProcessTime = processed.BeginTime;

        var ttl = fight.LastTime - fight.BeginTime + 1;
        fight.TooltipText = string.Format(CultureInfo.CurrentCulture, "#Hits To Players: {0}, #Hits From Players: {1}, Time Alive: {2}s", fight.TankHits, fight.DamageHits, ttl);

        DataManager.Instance.UpdateIfNewFightMap(fight.CorrectMapKey, fight);

        if (defender)
        {
          EventsPlayerAttackProcessed?.Invoke(processed.Record, processed);
        }
      }
  }

    private Fight Get(DamageRecord record, double currentTime, string origTimeString, bool defender)
    {
      string npc = defender ? record.Defender : record.Attacker;

      Fight fight = DataManager.Instance.GetFight(npc);
      if (fight == null)
      {
        fight = Create(npc, currentTime, origTimeString);
      }

      return fight;
    }

    private Fight Create(string defender, double currentTime, string origTimeString)
    {
      return new Fight()
      {
        Name = string.Intern(defender),
        BeginTimeString = string.Intern(origTimeString),
        BeginTime = currentTime,
        LastTime = currentTime,
        Id = CurrentNpcID++,
        GroupId = CurrentGroupID,
        CorrectMapKey = string.Intern(defender)
      };
    }

    private static void AddPlayerTime(Fight fight, DamageRecord record, string player, double time)
    {
      var isInitialTanking = fight.DamageBlocks.Count == 0;
      var segments = isInitialTanking ? fight.InitialTankSegments : fight.DamageSegments;
      var subSegments = isInitialTanking ? fight.InitialTankSubSegments : fight.DamageSubSegments;
      StatsUtil.UpdateTimeSegments(segments, subSegments, Helpers.CreateRecordKey(record.Type, record.SubType), player, time);
    }

    private static bool IsValidAttack(DamageRecord record, out bool defender)
    {
      bool valid = false;
      defender = false;

      if (!record.Attacker.Equals(record.Defender, StringComparison.OrdinalIgnoreCase))
      {
        var isDefenderNpc = record.Defender.StartsWith("Combat Dummy", StringComparison.OrdinalIgnoreCase) || DataManager.Instance.IsKnownNpc(record.Defender);
        var isAttackerNpc = record.Attacker.StartsWith("Combat Dummy", StringComparison.OrdinalIgnoreCase) || DataManager.Instance.IsKnownNpc(record.Attacker);
        var isAttackerPlayer = record.Attacker == Labels.UNK || PlayerManager.Instance.IsPetOrPlayer(record.Attacker);

        if (isDefenderNpc && !isAttackerNpc)
        {
          valid = isAttackerPlayer || PlayerManager.Instance.IsPossiblePlayerName(record.Attacker);
          defender = true;
        }
        else if (!isDefenderNpc && isAttackerNpc)
        {
          valid = true;
          defender = false;
        }
        else if (!isDefenderNpc && !isAttackerNpc)
        {
          var isDefenderPlayer = PlayerManager.Instance.IsPetOrPlayer(record.Defender);
          if (isDefenderPlayer || isAttackerPlayer)
          {
            valid = isDefenderPlayer != isAttackerPlayer;
            defender = !isDefenderPlayer;
          }
          else
          {
            defender = PlayerManager.Instance.IsPossiblePlayerName(record.Attacker) || !PlayerManager.Instance.IsPossiblePlayerName(record.Defender);
            valid = true;
          }
        }
        else if (isDefenderNpc && isAttackerNpc && DataManager.Instance.GetFight(record.Defender) != null
          && DataManager.Instance.GetFight(record.Attacker) == null)
        {
          valid = true;
          defender = true;
        }
      }

      return valid;
    }
  }
}
