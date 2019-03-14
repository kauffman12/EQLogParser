using System;
using System.Collections.Generic;

namespace EQLogParser
{
  class NpcDamageManager
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal DateTime LastUpdateTime { get; set; }

    private const int NPC_DEATH_TIME = 25;
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

    internal NonPlayer Find(string defender, string type)
    {
      NonPlayer npc = null;

      if (type == Labels.DOT_NAME || type == Labels.DS_NAME)
      {
        // DoTs or DS will show upper case when they shouldn't because they start a sentence so try lower case first
        npc = DataManager.Instance.GetNonPlayer(char.ToLower(defender[0]) + defender.Substring(1)) ?? DataManager.Instance.GetNonPlayer(defender);
      }
      else
      {
        // DDs are correct but still need to deal with names saved by a DoT so try upper case second
        npc = DataManager.Instance.GetNonPlayer(defender) ?? DataManager.Instance.GetNonPlayer(char.ToUpper(defender[0]) + defender.Substring(1));
      }

      return npc;
    }

    private void HandleDamageProcessed(object sender, DamageProcessedEvent processed)
    {
      if (processed.Record != null && LastUpdateTime != DateTime.MinValue)
      {
        TimeSpan diff = processed.Record.BeginTime.Subtract(LastUpdateTime);
        if (diff.TotalSeconds > 60)
        {
          CurrentGroupID++;
          DataManager.Instance.AddNonPlayerMapBreak(Helpers.FormatTimeSpan(diff));
        }
      }

      AddOrUpdateNpc(processed.Record, processed.Record.BeginTime, processed.TimeString.Substring(4, 15));
    }

    private void AddOrUpdateNpc(DamageRecord record, DateTime currentTime, string origTimeString)
    {
      NonPlayer npc = Get(record, currentTime, origTimeString);

      // assume npc has been killed and create new entry
      if (currentTime.Subtract(npc.LastTime).TotalSeconds > NPC_DEATH_TIME)
      {
        DataManager.Instance.RemoveActiveNonPlayer(npc.CorrectMapKey);
        npc = Get(record, currentTime, origTimeString);
      }

      npc.LastTime = currentTime;
      LastUpdateTime = currentTime;
      DataManager.Instance.UpdateIfNewNonPlayerMap(npc.CorrectMapKey, npc);
    }

    private NonPlayer Get(DamageRecord record, DateTime currentTime, string origTimeString)
    {
      NonPlayer npc = Find(record.Defender, record.Type);

      if (npc == null)
      {
        npc = Create(record.Defender, currentTime, origTimeString);
      }

      return npc;
    }

    private NonPlayer Create(string defender, DateTime currentTime, string origTimeString)
    {
      return new NonPlayer()
      {
        Name = defender,
        BeginTimeString = origTimeString,
        BeginTime = currentTime,
        LastTime = currentTime,
        ID = CurrentNpcID++,
        GroupID = CurrentGroupID,
        CorrectMapKey = defender
      };
    }
  }
}
