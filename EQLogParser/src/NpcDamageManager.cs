using System;
using System.Collections.Generic;

namespace EQLogParser
{
  class NpcDamageManager
  {
    private const int NPC_DEATH_TIME = 25;
    public DateTime LastUpdateTime { get; set; }
    private long CurrentNpcID = 0;

    private static Dictionary<string, byte> Unique = new Dictionary<string, byte>();

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
        npc.DamageMap.Add(record.Attacker, new DamageStats()
        {
          BeginTime = currentTime,
          Owner = "",
          IsPet = false,
          HitMap = new Dictionary<string, Hit>()
        });
      }

      // update basic stats
      DamageStats stats = npc.DamageMap[record.Attacker];
      if (!stats.HitMap.ContainsKey(record.Type))
      {
        stats.HitMap[record.Type] = new Hit() { Count = 0, Max = 0, TotalDamage = 0 };
      }

      stats.Count++;
      stats.TotalDamage += record.Damage;
      stats.Max = (stats.Max < record.Damage) ? record.Damage : stats.Max;
      stats.HitMap[record.Type].Count++;
      stats.HitMap[record.Type].TotalDamage += record.Damage;
      stats.HitMap[record.Type].Max = (stats.HitMap[record.Type].Max < record.Damage) ? record.Damage : stats.HitMap[record.Type].Max;

      updateModifiers(stats, record);
      stats.LastTime = currentTime;
      npc.LastTime = currentTime;
      LastUpdateTime = currentTime;

      if (record.AttackerPetType != "")
      {
        stats.IsPet = true;
        stats.Owner = record.AttackerOwner;
      }

      DataManager.Instance.UpdateIfNewNonPlayerMap(npc.CorrectMapKey, npc);
    }

    private void updateModifiers(DamageStats stats, DamageRecord record)
    {
      bool wild = false;
      bool rampage = false;
      foreach (string modifier in record.Modifiers.Keys)
      {
        switch (modifier)
        {
          case "Bow": // Double Bow Shot
            stats.DoubleBowShotCount++;
            stats.HitMap[record.Type].DoubleBowShotCount++;
            break;
          case "Critical":
          case "Crippling": // Crippling Blow
          case "Deadly":    // Deadly Strike
            stats.CritCount++;
            stats.TotalCritDamage += record.Damage;
            stats.HitMap[record.Type].CritCount++;
            stats.HitMap[record.Type].TotalCritDamage += record.Damage;
            break;
          case "Flurry":
            stats.FlurryCount++;
            break;
          case "Lucky":
            stats.LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            stats.HitMap[record.Type].LuckyCount++;
            stats.HitMap[record.Type].TotalLuckyDamage += record.Damage;
            break;
          case "Rampage":
            rampage = true;
            break;
          case "Twincast":
            stats.TwincastCount++;
            stats.HitMap[record.Type].TwincastCount++;
            break;
          case "Undead":
            stats.SlayUndeadCount++;
            break;
          case "Wild":
            wild = true;
            break;
        }
      }

      if (rampage && !wild)
      {
        stats.RampageCount++;
      }
      else if (rampage && wild)
      {
        stats.WildRampageCount++;
      }
    }

    private NonPlayer Get(DamageRecord record, DateTime currentTime, String origTimeString)
    {
      NonPlayer npc = DataManager.Instance.GetNonPlayer(record.Defender);

      if (npc == null && Char.IsUpper(record.Defender[0]) && record.Action == "DoT")
      {
        // DoTs will show upper case when they shouldn't because they start a sentence
        npc = DataManager.Instance.GetNonPlayer(Char.ToLower(record.Defender[0]) + record.Defender.Substring(1));
      }
      else if (npc == null && Char.IsLower(record.Defender[0]) && record.Action == "DD")
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
