using System;
using System.Globalization;

namespace EQLogParser
{
  class NpcDamageManager
  {
    internal const int NPC_DEATH_TIME = 25;
    internal double LastUpdateTime { get; set; }

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

    private static NonPlayer Find(string defender)
    {
      NonPlayer npc;

      if (char.IsUpper(defender[0]))
      {
        npc = DataManager.Instance.GetNonPlayer(char.ToLower(defender[0], CultureInfo.CurrentCulture) + defender.Substring(1)) ?? DataManager.Instance.GetNonPlayer(defender);
      }
      else
      {
        npc = DataManager.Instance.GetNonPlayer(char.ToUpper(defender[0], CultureInfo.CurrentCulture) + defender.Substring(1)) ?? DataManager.Instance.GetNonPlayer(defender);
      }

      return npc;
    }

    private void HandleDamageProcessed(object sender, DamageProcessedEvent processed)
    {
      if (PlayerManager.Instance.IsPlayerDamage(processed.Record))
      {
        if (!double.IsNaN(LastUpdateTime))
        {
          var seconds = processed.BeginTime - LastUpdateTime;
          if (seconds > 60)
          {
            CurrentGroupID++;
            DataManager.Instance.AddNonPlayerMapBreak(FormatTime(seconds));
          }
        }

        AddOrUpdateNpc(processed.Record, processed.BeginTime, processed.TimeString.Substring(4, 15));
      }
    }

    private void AddOrUpdateNpc(DamageRecord record, double currentTime, string origTimeString)
    {
      NonPlayer npc = Get(record, currentTime, origTimeString);

      // assume npc has been killed and create new entry
      if (currentTime - npc.LastTime > NPC_DEATH_TIME)
      {
        DataManager.Instance.RemoveActiveNonPlayer(npc.CorrectMapKey);
        npc = Get(record, currentTime, origTimeString);
      }

      npc.LastTime = currentTime;
      LastUpdateTime = currentTime;
      DataManager.Instance.UpdateIfNewNonPlayerMap(npc.CorrectMapKey, npc);
    }

    private NonPlayer Get(DamageRecord record, double currentTime, string origTimeString)
    {
      NonPlayer npc = Find(record.Defender);

      if (npc == null)
      {
        npc = Create(record.Defender, currentTime, origTimeString);
      }

      return npc;
    }

    private NonPlayer Create(string defender, double currentTime, string origTimeString)
    {
      return new NonPlayer()
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

    private static string FormatTime(double seconds)
    {
      TimeSpan diff = TimeSpan.FromSeconds(seconds);
      string result = "Inactivity > ";

      if (diff.Days >= 1)
      {
        result += diff.Days + " days";
      }
      else if (diff.Hours >= 1)
      {
        result += diff.Hours + " hours";
      }
      else
      {
        result += diff.Minutes + " minutes";
      }

      return result;
    }
  }
}
