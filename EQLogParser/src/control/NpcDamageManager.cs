using System;
using System.Globalization;

namespace EQLogParser
{
  class NpcDamageManager
  {
    internal const int NPC_DEATH_TIME = 24;
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

    private static Fight Find(string defender)
    {
      Fight npc;

      if (char.IsUpper(defender[0]))
      {
        npc = DataManager.Instance.GetFight(char.ToLower(defender[0], CultureInfo.CurrentCulture) + defender.Substring(1)) ?? DataManager.Instance.GetFight(defender);
      }
      else
      {
        npc = DataManager.Instance.GetFight(char.ToUpper(defender[0], CultureInfo.CurrentCulture) + defender.Substring(1)) ?? DataManager.Instance.GetFight(defender);
      }

      return npc;
    }

    private void HandleDamageProcessed(object sender, DamageProcessedEvent processed)
    {
      if (IsValidAttack(processed.Record, out bool defender))
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

        // assume npc has been killed and create new entry
        if (processed.BeginTime - fight.LastTime > NPC_DEATH_TIME)
        {
          DataManager.Instance.RemoveActiveFight(fight.CorrectMapKey);
          fight = Get(processed.Record, processed.BeginTime, origTimeString, defender);
        }

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
      }
    }

    private Fight Get(DamageRecord record, double currentTime, string origTimeString, bool defender)
    {
      string npc = defender ? record.Defender : record.Attacker;

      Fight fight = Find(npc);
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
        ID = CurrentNpcID++,
        GroupID = CurrentGroupID,
        CorrectMapKey = string.Intern(defender)
      };
    }

    private static bool IsValidAttack(DamageRecord record, out bool defender)
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
          valid = isAttackerPlayer || Helpers.IsPossiblePlayerName(record.Attacker);
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
          valid = (isAttackerPlayer || !Helpers.IsPossiblePlayerName(record.Attacker)) && !isDefenderPlayer;
          defender = true;
        }
      }

      return valid;
    }
  }
}
