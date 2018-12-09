using System;
using System.Collections.Generic;

namespace EQLogParser
{
  class NpcDamageManager
  {
    private const int NPC_DEATH_TIME = 25;
    public DateTime LastUpdateTime { get; set; }
    private long CurrentNpcID = 0;

    public void AddOrUpdateNpc(DamageRecord record, DateTime currentTime, String origTimeString)
    {
      NonPlayer npc = Get(record, currentTime, origTimeString);

      // assume npc has been killed and create new entry
      if (currentTime.Subtract(npc.LastTime).TotalSeconds > NPC_DEATH_TIME)
      {
        DataManager.Instance.RemoveActiveNonPlayer(npc.CorrectMapKey);
        npc = Get(record, currentTime, origTimeString);
      }

      if (!npc.DamageMap.ContainsKey(record.Attacker))
      {
        npc.DamageMap.Add(record.Attacker, new DamageStats() { BeginTime = currentTime, Owner = "", IsPet = false });
      }

      // update basic stats
      DamageStats stats = npc.DamageMap[record.Attacker];
      stats.Damage += record.Damage;
      stats.Hits++;
      stats.Max = (stats.Max < record.Damage) ? record.Damage : stats.Max;
      stats.LastTime = currentTime;
      LastUpdateTime = currentTime;
      npc.LastTime = currentTime;

      if (record.AttackerPetType != "")
      {
        stats.IsPet = true;
        stats.Owner = record.AttackerOwner;
      }

      DataManager.Instance.UpdateIfNewNonPlayerMap(npc.CorrectMapKey, npc);
    }

    private  NonPlayer Get(DamageRecord record, DateTime currentTime, String origTimeString)
    {
      NonPlayer npc = DataManager.Instance.GetNonPlayer(record.Defender);

      if (npc == null && Char.IsUpper(record.Defender[0]) && record.Type == "DoT")
      {
        // DoTs will show upper case when they shouldn't because they start a sentence
        npc = DataManager.Instance.GetNonPlayer(Char.ToLower(record.Defender[0]) + record.Defender.Substring(1));
      } else if (npc == null && Char.IsLower(record.Defender[0]) && record.Type == "DD")
      {
        // DDs deal with having to work around DoTs
        npc = DataManager.Instance.GetNonPlayer(Char.ToUpper(record.Defender[0]) + record.Defender.Substring(1));
      }

      if (npc == null)
      {
        npc = new NonPlayer()
        {
          Name = record.Defender,
          BeginTimeString = origTimeString,
          BeginTime = currentTime,
          LastTime = currentTime,
          DamageMap = new Dictionary<string, DamageStats>(),
          ID = CurrentNpcID++,
          CorrectMapKey = record.Defender
        };
      }

      return npc;
    }
  }
}
