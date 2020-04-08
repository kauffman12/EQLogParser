using System;

namespace EQLogParser
{
  class NpcDamageManager
  {
    public static event EventHandler<DamageProcessedEvent> EventsPlayerAttackProcessed;

    internal const int GROUP_TIMEOUT = 120;
    internal double LastUpdateTime = double.NaN;

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
      LastUpdateTime = double.NaN;
    }

    private void HandleDamageProcessed(object sender, DamageProcessedEvent processed)
    {
      if (IsValidAttack(processed.Record, processed.BeginTime, out bool defender))
      {
        if (!double.IsNaN(LastUpdateTime))
        {
          var seconds = processed.BeginTime - LastUpdateTime;
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
          fight.Total += processed.Record.Total;
        }
        else
        {
          Helpers.AddAction(fight.TankingBlocks, processed.Record, processed.BeginTime);
        }

        fight.LastTime = processed.BeginTime;
        LastUpdateTime = processed.BeginTime;

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

      Fight fight = DataManager.Instance.GetFight(npc, currentTime);
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

    private static bool IsValidAttack(DamageRecord record, double currentTime, out bool defender)
    {
      bool valid = false;
      defender = false;

      if (!record.Attacker.Equals(record.Defender, StringComparison.OrdinalIgnoreCase))
      {
        var isDefenderNpc = record.Defender.StartsWith("Combat Dummy", StringComparison.OrdinalIgnoreCase) || DataManager.Instance.IsKnownNpc(record.Defender);
        var isAttackerNpc = record.Attacker.StartsWith("Combat Dummy", StringComparison.OrdinalIgnoreCase) || DataManager.Instance.IsKnownNpc(record.Attacker);
        var isAttackerPlayer = PlayerManager.Instance.IsPetOrPlayer(record.Attacker);

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
          valid = (isAttackerPlayer || !PlayerManager.Instance.IsPossiblePlayerName(record.Attacker)) && !isDefenderPlayer;
          defender = true;
        }
        else if (isDefenderNpc && isAttackerNpc && DataManager.Instance.GetFight(record.Defender, currentTime) != null
          && DataManager.Instance.GetFight(record.Attacker, currentTime) == null)
        {
          valid = true;
          defender = true;
        }
      }

      return valid;
    }
  }
}
