using System;
using System.Collections.Generic;

namespace EQLogParser
{
  class NpcDamageManager
  {
    public DateTime LastUpdateTime { get; set; }

    private List<DamageAtTime> DamageTimeLine;
    private DictionaryAddHelper<long, int> AddHelper = new DictionaryAddHelper<long, int>();
    private DamageAtTime DamageAtThisTime = null;
    private const int NPC_DEATH_TIME = 25;
    private int CurrentNpcID = 0;
    private int CurrentGroupID = 0;

    public NpcDamageManager()
    {
      DamageTimeLine = new List<DamageAtTime>();
      DamageLineParser.EventsDamageProcessed += HandleDamageProcessed;
      DataManager.Instance.EventsClearedActiveData += (sender, cleared) =>
      {
        CurrentGroupID = 0;
        DamageTimeLine = new List<DamageAtTime>();
      };
    }

    public IList<DamageAtTime> GetDamageStartingAt(DateTime startTime)
    {
      DamageAtTimeComparer comparer = new DamageAtTimeComparer();
      int index = DamageTimeLine.BinarySearch(new DamageAtTime() { CurrentTime = startTime }, comparer);
      if (index < 0)
      {
        index = Math.Abs(index) - 1;
      }

     return DamageTimeLine.GetRange(index, DamageTimeLine.Count - index);
    }

    ~NpcDamageManager()
    {
      DamageLineParser.EventsDamageProcessed -= HandleDamageProcessed;
    }

    private void HandleDamageProcessed(object sender, DamageProcessedEvent processed)
    {
      if (processed.Record != null && LastUpdateTime != DateTime.MinValue)
      {
        TimeSpan diff = processed.ProcessLine.CurrentTime.Subtract(LastUpdateTime);
        if (diff.TotalSeconds > 60)
        {
          CurrentGroupID++;
          DataManager.Instance.AddNonPlayerMapBreak(Helpers.FormatTimeSpan(diff));
        }
      }

      AddOrUpdateNpc(processed.Record, processed.ProcessLine.CurrentTime, processed.ProcessLine.TimeString.Substring(4, 15));
    }

    private void AddOrUpdateNpc(DamageRecord record, DateTime currentTime, String origTimeString)
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

      npc.LastTime = currentTime;
      LastUpdateTime = currentTime;

      // update basic stats
      DamageStats stats = npc.DamageMap[record.Attacker];
      if (!stats.HitMap.ContainsKey(record.Type))
      {
        stats.HitMap[record.Type] = new Hit() { CritFreqValues = new Dictionary<long, int>(), NonCritFreqValues = new Dictionary<long, int>() };
      }

      stats.Count++;
      stats.TotalDamage += record.Damage;
      stats.Max = (stats.Max < record.Damage) ? record.Damage : stats.Max;
      stats.HitMap[record.Type].Count++;
      stats.HitMap[record.Type].TotalDamage += record.Damage;
      stats.HitMap[record.Type].Max = (stats.HitMap[record.Type].Max < record.Damage) ? record.Damage : stats.HitMap[record.Type].Max;

      int critCount = stats.CritCount;
      UpdateModifiers(stats, record);

      // if crit count did not increase this hit was a non-crit
      if (critCount == stats.CritCount)
      {
        AddHelper.Add(stats.HitMap[record.Type].NonCritFreqValues, record.Damage, 1);
      }
      else
      {
        AddHelper.Add(stats.HitMap[record.Type].CritFreqValues, record.Damage, 1);
      }

      stats.LastTime = currentTime;

      if (record.AttackerPetType != "")
      {
        stats.IsPet = true;
        stats.Owner = record.AttackerOwner;
      }

      if (DamageAtThisTime == null)
      {
        DamageAtThisTime = new DamageAtTime() { CurrentTime = currentTime, PlayerDamage = new Dictionary<string, long>(), GroupID = CurrentGroupID };
        DamageTimeLine.Add(DamageAtThisTime);
      }
      else if (currentTime.Subtract(DamageAtThisTime.CurrentTime).TotalSeconds >= 1) // EQ granular to 1 second
      {
        DamageAtThisTime = new DamageAtTime() { CurrentTime = currentTime, PlayerDamage = new Dictionary<string, long>(), GroupID = CurrentGroupID };
        DamageTimeLine.Add(DamageAtThisTime);
      }

      if (!DamageAtThisTime.PlayerDamage.ContainsKey(record.Attacker))
      {
        DamageAtThisTime.PlayerDamage[record.Attacker] = 0;
      }

      DamageAtThisTime.PlayerDamage[record.Attacker] += record.Damage;
      DamageAtThisTime.CurrentTime = currentTime;

      DataManager.Instance.UpdateIfNewNonPlayerMap(npc.CorrectMapKey, npc);
    }

    private void UpdateModifiers(DamageStats stats, DamageRecord record)
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
          case "Flurry":
            stats.FlurryCount++;
            break;
          case "Lucky":
            stats.LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            stats.HitMap[record.Type].LuckyCount++;
            stats.HitMap[record.Type].TotalLuckyDamage += record.Damage;
            // workaround to keep better track of avg base crit damage
            stats.TotalCritDamage -= record.Damage;
            stats.HitMap[record.Type].TotalCritDamage -= record.Damage;
            break;
          case "Critical":
          case "Crippling": // Crippling Blow
          case "Deadly":    // Deadly Strike
            stats.CritCount++;
            stats.TotalCritDamage += record.Damage;
            stats.HitMap[record.Type].CritCount++;
            stats.HitMap[record.Type].TotalCritDamage += record.Damage;
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
          GroupID = CurrentGroupID,
          CorrectMapKey = record.Defender
        };
      }

      return npc;
    }

    private class DamageAtTimeComparer : IComparer<DamageAtTime>
    {
      public int Compare(DamageAtTime x, DamageAtTime y)
      {
        return x.CurrentTime.CompareTo(y.CurrentTime);
      }
    }
  }
}
