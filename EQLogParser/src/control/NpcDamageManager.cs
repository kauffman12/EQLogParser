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
      if (processed.Record.Attacker != processed.Record.Defender && processed.Record.Total > 0)
      {
        if (!double.IsNaN(LastUpdateTime))
        {
          var seconds = processed.BeginTime - LastUpdateTime;
          if (seconds >= GROUP_TIMEOUT)
          {
            CurrentGroupID++;
          }
        }

        AddOrUpdateFight(processed.Record, processed.BeginTime, processed.TimeString.Substring(4, 15));
      }
    }

    private void AddOrUpdateFight(DamageRecord record, double currentTime, string origTimeString)
    {
      Fight fight = Get(record, currentTime, origTimeString);

      // assume npc has been killed and create new entry
      if (currentTime - fight.LastTime > NPC_DEATH_TIME)
      {
        DataManager.Instance.RemoveActiveFight(fight.CorrectMapKey);
        fight = Get(record, currentTime, origTimeString);
      }

      var previous = fight.IsNpc;
      fight.IsNpc = fight.IsNpc || IsDefenderNpc(record);
      fight.LastTime = currentTime;
      LastUpdateTime = currentTime;

      DataManager.Instance.UpdateIfNewFightMap(fight.CorrectMapKey, fight, previous != fight.IsNpc);
    }

    private Fight Get(DamageRecord record, double currentTime, string origTimeString)
    {
      Fight npc = Find(record.Defender);

      if (npc == null)
      {
        npc = Create(record.Defender, currentTime, origTimeString);
      }

      return npc;
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

    private static bool IsDefenderNpc(DamageRecord record)
    {
      bool valid = false;

      var isDefenderNpc = record.Defender.StartsWith("Combat Dummy", StringComparison.OrdinalIgnoreCase) || DataManager.Instance.IsKnownNpc(record.Defender);
      var isAttackerNpc = record.Attacker.StartsWith("Combat Dummy", StringComparison.OrdinalIgnoreCase) || DataManager.Instance.IsKnownNpc(record.Attacker);
      var isAttackerPlayer = PlayerManager.Instance.IsPetOrPlayer(record.Attacker);

      if (isDefenderNpc && !isAttackerNpc)
      {
        valid = isAttackerPlayer || Helpers.IsPossiblePlayerName(record.Attacker);
      }
      else if (!isDefenderNpc && !isAttackerNpc)
      {
        var isDefenderPlayer = PlayerManager.Instance.IsPetOrPlayer(record.Defender);
        valid = (isAttackerPlayer || !Helpers.IsPossiblePlayerName(record.Attacker)) && !isDefenderPlayer;
      }

      return valid;
    }
  }
}
