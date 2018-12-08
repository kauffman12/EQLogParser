using System;
using System.Collections.Generic;

namespace EQLogParser
{
  class NpcDamageManager
  {
    private const int NPC_DEATH_TIME = 25;
    private Dictionary<string, NonPlayer> ActiveNonPlayerMap = new Dictionary<string, NonPlayer>();
    private Dictionary<string, bool> LifetimeNonPlayerMap = new Dictionary<string, bool>();
    private DateTime LastUpdateTime;
    private long CurrentNpcID = 0;

    public DateTime GetLastUpdateTime()
    {
      return LastUpdateTime;
    }

    public NonPlayer AddOrUpdateNpc(DamageRecord record, DateTime currentTime, String origTimeString, out bool newEntry)
    {
      newEntry = false;
      NonPlayerEntry entry = Get(record, currentTime, origTimeString);

      // assume npc has been killed and create new entry
      if (currentTime.Subtract(entry.Npc.LastTime).TotalSeconds > NPC_DEATH_TIME)
      {
        ActiveNonPlayerMap.Remove(record.Defender);
        entry = Get(record, currentTime, origTimeString);
      }

      if (!entry.Npc.DamageMap.ContainsKey(record.Attacker))
      {
        entry.Npc.DamageMap.Add(record.Attacker, new DamageStats() { BeginTime = currentTime, Owner = "", IsPet = false });
      }

      // update basic stats
      DamageStats stats = entry.Npc.DamageMap[record.Attacker];
      stats.Damage += record.Damage;
      stats.Hits++;
      stats.Max = (stats.Max < record.Damage) ? record.Damage : stats.Max;
      stats.LastTime = currentTime;
      LastUpdateTime = currentTime;

      if (record.AttackerPetType != "" && record.AttackerOwner != "")
      {
        stats.IsPet = true;
        stats.Owner = record.AttackerOwner;
      }
      else if (record.AttackerPetType == "" && LineParser.PetToPlayers.ContainsKey(record.Attacker))
      {
        stats.IsPet = true;
        stats.Owner = LineParser.PetToPlayers[record.Attacker];
      }
      else if (LineParser.GeneratedPets.ContainsKey(record.Attacker))
      {
        stats.IsPet = true;
      }

      entry.Npc.LastTime = currentTime;
      newEntry = entry.IsNew;
      entry.IsNew = false;
      return entry.Npc;
    }

    public bool CheckForPlayer(string name)
    {
      bool needUpdate = false;
      if (ActiveNonPlayerMap.ContainsKey(name))
      {
        ActiveNonPlayerMap.Remove(name);
        needUpdate = true;
      }

      if (LifetimeNonPlayerMap.ContainsKey(name))
      {
        LifetimeNonPlayerMap.Remove(name);
        needUpdate = true;
      }
      return needUpdate;
    }

    public void Slain(string name)
    {
      if (ActiveNonPlayerMap.ContainsKey(name))
      {
        ActiveNonPlayerMap.Remove(name);
      } else if (ActiveNonPlayerMap.ContainsKey(name.ToLower()))
      {
        ActiveNonPlayerMap.Remove(name.ToLower());
      }
    }

    private  NonPlayerEntry Get(DamageRecord record, DateTime currentTime, String origTimeString)
    {
      NonPlayer npc;
      bool isNew = false;

      bool alreadyExists = ActiveNonPlayerMap.ContainsKey(record.Defender);
      if (!alreadyExists && record.Type == "DoT")
      {
        // DoTs will show upper case when they shouldn't
        if(ActiveNonPlayerMap.ContainsKey(record.Defender.ToLower()))
        {
          alreadyExists = true;
          record.Defender = record.Defender.ToLower();
        }
      }

      if (!alreadyExists)
      {
        isNew = true;
        npc = new NonPlayer()
        {
          Name = record.Defender,
          BeginTimeString = origTimeString,
          BeginTime = currentTime,
          LastTime = currentTime,
          DamageMap = new Dictionary<string, DamageStats>(),
          ID = CurrentNpcID++
        };

        ActiveNonPlayerMap.Add(record.Defender, npc);
        if (!LifetimeNonPlayerMap.ContainsKey(record.Defender))
        {
          LifetimeNonPlayerMap.Add(record.Defender, true);
        }
      }
      else
      {
        npc = ActiveNonPlayerMap[record.Defender];
      }

      return new NonPlayerEntry() { Npc = npc, IsNew = isNew };
    }

    private class NonPlayerEntry
    {
      public NonPlayer Npc { get; set; }
      public bool IsNew { get; set; }
    }
  }
}
