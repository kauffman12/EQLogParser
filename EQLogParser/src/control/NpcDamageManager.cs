using System;
using System.Collections.Generic;

namespace EQLogParser
{
  class NpcDamageManager
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

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
          HitMap = new Dictionary<string, Hit>(),
          SpellMap = new Dictionary<string, Hit>()
        });
      }

      npc.LastTime = currentTime;
      LastUpdateTime = currentTime;

      // update basic stats
      DamageStats stats = npc.DamageMap[record.Attacker];

      // store spells and melee hits separately
      Dictionary<string, Hit> aHitMap;
      string aType;
      if (record.Type == Labels.DOT_TYPE)
      {
        aHitMap = stats.SpellMap;
        aType = record.Spell;
      }
      else
      {
        aHitMap = stats.HitMap;
        aType = record.Type;
      }

      if (!aHitMap.ContainsKey(aType))
      {
        aHitMap[aType] = new Hit() { CritFreqValues = new Dictionary<long, int>(), NonCritFreqValues = new Dictionary<long, int>() };
      }

      stats.Count++;
      stats.TotalDamage += record.Damage;
      stats.Max = (stats.Max < record.Damage) ? record.Damage : stats.Max;
      aHitMap[aType].Count++;
      aHitMap[aType].TotalDamage += record.Damage;
      aHitMap[aType].Max = (aHitMap[aType].Max < record.Damage) ? record.Damage : aHitMap[aType].Max;

      int critCount = stats.CritCount;
      UpdateModifiers(stats, aHitMap, aType, record);

      // if crit count did not increase this hit was a non-crit
      if (critCount == stats.CritCount)
      {
        AddHelper.Add(aHitMap[aType].NonCritFreqValues, record.Damage, 1);
      }
      else
      {
        AddHelper.Add(aHitMap[aType].CritFreqValues, record.Damage, 1);
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

    private void UpdateModifiers(DamageStats stats, Dictionary<string, Hit> aHitMap, string aType, DamageRecord record)
    {
      if (record.Modifiers != null && record.Modifiers != "")
      {
        switch (record.Modifiers)
        {
          case "Crippling Blow":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            break;
          case "Critical":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            break;
          case "Critical Assassinate":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            break;
          case "Critical Double Bow Shot":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            stats.DoubleBowShotCount++;
            aHitMap[aType].DoubleBowShotCount++;
            break;
          case "Critical Headshot":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            break;
          case "Critical Rampage":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            stats.RampageCount++;
            aHitMap[aType].RampageCount++;
            break;
          case "Critical Twincast":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            stats.TwincastCount++;
            aHitMap[aType].TwincastCount++;
            break;
          case "Critical Wild Rampage":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            stats.WildRampageCount++;
            aHitMap[aType].WildRampageCount++;
            break;
          case "Deadly Strike":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            break;
          case "Double Bow Shot":
            stats.DoubleBowShotCount++;
            aHitMap[aType].DoubleBowShotCount++;
            break;
          case "Crippling Blow Double Bow Shot":
          case "Double Bow Shot Crippling Blow": // check if still used in future
            stats.CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].CritCount++;
            aHitMap[aType].TotalCritDamage += record.Damage;
            stats.DoubleBowShotCount++;
            aHitMap[aType].DoubleBowShotCount++;
            break;
          case "Finishing Blow":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            break;
          case "Flurry":
            stats.FlurryCount++;
            aHitMap[aType].FlurryCount++;
            break;
          case "Flurry Crippling Blow":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            stats.FlurryCount++;
            aHitMap[aType].FlurryCount++;
            break;
          case "Flurry Critical":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            stats.FlurryCount++;
            aHitMap[aType].FlurryCount++;
            break;
          case "Flurry Critical Rampage":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            stats.FlurryCount++;
            aHitMap[aType].FlurryCount++;
            stats.RampageCount++;
            aHitMap[aType].RampageCount++;
            break;
          case "Flurry Critical Wild Rampage":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            stats.FlurryCount++;
            aHitMap[aType].FlurryCount++;
            stats.WildRampageCount++;
            aHitMap[aType].WildRampageCount++;
            break;
          case "Flurry Finishing Blow":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.TotalCritDamage += record.Damage;
            aHitMap[aType].TotalCritDamage += record.Damage;
            stats.FlurryCount++;
            aHitMap[aType].FlurryCount++;
            break;
          case "Flurry Lucky Crippling Blow":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            stats.FlurryCount++;
            aHitMap[aType].FlurryCount++;
            break;
          case "Flurry Lucky Critical":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            stats.FlurryCount++;
            aHitMap[aType].FlurryCount++;
            break;
          case "Flurry Lucky Critical Rampage":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            stats.FlurryCount++;
            aHitMap[aType].FlurryCount++;
            stats.RampageCount++;
            aHitMap[aType].RampageCount++;
            break;
          case "Flurry Lucky Critical Wild Rampage":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            stats.FlurryCount++;
            aHitMap[aType].FlurryCount++;
            stats.WildRampageCount++;
            aHitMap[aType].WildRampageCount++;
            break;
          case "Flurry Lucky Finishing Blow":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            stats.FlurryCount++;
            aHitMap[aType].FlurryCount++;
            break;
          case "Flurry Rampage":
            stats.FlurryCount++;
            aHitMap[aType].FlurryCount++;
            stats.RampageCount++;
            aHitMap[aType].RampageCount++;
            break;
          case "Flurry Slay Undead":
            stats.SlayUndeadCount++;
            aHitMap[aType].SlayUndeadCount++;
            stats.FlurryCount++;
            aHitMap[aType].FlurryCount++;
            break;
          case "Flurry Wild Rampage":
            stats.FlurryCount++;
            aHitMap[aType].FlurryCount++;
            stats.WildRampageCount++;
            aHitMap[aType].WildRampageCount++;
            break;
          case "Headshot":
            break;
          case "Lucky Assassinate":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            break;
          case "Lucky Crippling Blow":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            break;
          case "Lucky Crippling Blow Double Bow Shot":
          case "Lucky Double Bow Shot Crippling Blow": // check if still used in future
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            stats.DoubleBowShotCount++;
            aHitMap[aType].DoubleBowShotCount++;
            break;
          case "Lucky Critical":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            break;
          case "Lucky Critical Assassinate":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            break;
          case "Lucky Critical Double Bow Shot":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            stats.DoubleBowShotCount++;
            aHitMap[aType].DoubleBowShotCount++;
            break;
          case "Lucky Critical Headshot":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            break;
          case "Lucky Critical Rampage":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            stats.RampageCount++;
            aHitMap[aType].RampageCount++;
            break;
          case "Lucky Critical Twincast":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            stats.TwincastCount++;
            aHitMap[aType].TwincastCount++;
            break;
          case "Lucky Critical Wild Rampage":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            stats.WildRampageCount++;
            aHitMap[aType].WildRampageCount++;
            break;
          case "Lucky Deadly Strike":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            break;
          case "Lucky Finishing Blow":
            stats.CritCount++;
            aHitMap[aType].CritCount++;
            stats.LuckyCount++;
            aHitMap[aType].LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            aHitMap[aType].TotalLuckyDamage += record.Damage;
            break;
          case "Rampage":
            stats.RampageCount++;
            aHitMap[aType].RampageCount++;
            break;
          case "Slay Undead":
            stats.SlayUndeadCount++;
            aHitMap[aType].SlayUndeadCount++;
            break;
          case "Twincast":
            stats.TwincastCount++;
            aHitMap[aType].TwincastCount++;
            break;
          case "Wild Rampage":
            stats.WildRampageCount++;
            aHitMap[aType].WildRampageCount++;
            break;
          default:
            LOG.Debug("Uknown Modifiers: " + record.Modifiers);
            break;
        }
      }
    }

    private NonPlayer Get(DamageRecord record, DateTime currentTime, String origTimeString)
    {
      NonPlayer npc = Find(record.Defender, record.Type);

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

    public NonPlayer Find(string defender, string type)
    {
      NonPlayer npc = DataManager.Instance.GetNonPlayer(defender);

      if (npc == null && char.IsUpper(defender[0]) && (type == Labels.DOT_TYPE || type == Labels.DS_TYPE))
      {
        // DoTs or DS will show upper case when they shouldn't because they start a sentence
        npc = DataManager.Instance.GetNonPlayer(char.ToLower(defender[0]) + defender.Substring(1));
      }
      else if (npc == null && char.IsLower(defender[0]) && type == Labels.DD_TYPE)
      {
        // DDs deal with having to work around DoTs
        npc = DataManager.Instance.GetNonPlayer(char.ToUpper(defender[0]) + defender.Substring(1));
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
