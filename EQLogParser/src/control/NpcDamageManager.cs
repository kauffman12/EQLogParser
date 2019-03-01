using System;
using System.Collections.Generic;

namespace EQLogParser
{
  class NpcDamageManager
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public DateTime LastUpdateTime { get; set; }

    private List<DamageAtTime> DamageTimeLine;
    private DictionaryAddHelper<long, int> LongAddHelper = new DictionaryAddHelper<long, int>();
    private DictionaryAddHelper<string, int> StringAddHelper = new DictionaryAddHelper<string, int>();
    private DamageAtTime DamageAtThisTime = null;
    private const int NPC_DEATH_TIME = 25;
    private int CurrentNpcID = 0;
    private int CurrentGroupID = 0;

    public NpcDamageManager()
    {
      DamageTimeLine = new List<DamageAtTime>();
      DamageLineParser.EventsDamageProcessed += HandleDamageProcessed;
      DamageLineParser.EventsResistProcessed += HandleResistProcessed;
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
      DamageLineParser.EventsResistProcessed -= HandleResistProcessed;
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

    private void HandleResistProcessed(object sender, ResistProcessedEvent processed)
    {
      if (processed.ProcessLine != null && processed.Defender != null && processed.Spell != null)
      {
        // use DoT type since it begins a sentence
        var nonPlayer = Find(processed.Defender, Labels.DOT_TYPE);
        if (nonPlayer == null)
        {
          nonPlayer = Create(processed.Defender, processed.ProcessLine.CurrentTime, processed.ProcessLine.TimeString);
        }

        Dictionary<string, int> resists;
        if (nonPlayer.ResistMap.ContainsKey(DataManager.Instance.PlayerName))
        {
          resists = nonPlayer.ResistMap[DataManager.Instance.PlayerName];
        }
        else
        {
          resists = new Dictionary<string, int>();
          nonPlayer.ResistMap[DataManager.Instance.PlayerName] = resists;
        }

        StringAddHelper.Add(resists, processed.Spell, 1);
      }
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
          SpellDoTMap = new Dictionary<string, Hit>(),
          SpellDDMap = new Dictionary<string, Hit>(),
          SpellProcMap = new Dictionary<string, Hit>()
        });
      }

      npc.LastTime = currentTime;
      LastUpdateTime = currentTime;

      // update basic stats
      DamageStats stats = npc.DamageMap[record.Attacker];

      // store spells and melee hits separately
      Dictionary<string, Hit> aHitMap;
      string aType;
      if (record.Spell != "")
      {
        string spellName = Helpers.AbbreviateSpellName(record.Spell);
        SpellData data = DataManager.Instance.GetSpellByAbbrv(spellName);
        if (data != null && data.IsProc)
        {
          aHitMap = stats.SpellProcMap;
        }
        else
        {
          aHitMap = record.Type == Labels.DD_TYPE ? stats.SpellDDMap : stats.SpellDoTMap;
        }

        aType = record.Spell;
      }
      else
      {
        aHitMap = stats.HitMap;
        aType = record.Type;
      }

      if (!aHitMap.ContainsKey(aType))
      {
        aHitMap[aType] = new Hit() { BeginTime = currentTime, LastTime = currentTime, CritFreqValues = new Dictionary<long, int>(), NonCritFreqValues = new Dictionary<long, int>() };
      }
      else
      {
        aHitMap[aType].LastTime = currentTime;
      }

      // record Bane separately in totals
      if (record.Type == Labels.BANE_TYPE)
      {
        stats.BaneHits++;
      }
      else
      {
        stats.Hits++;
        stats.Total += record.Total;
        stats.Max = (stats.Max < record.Total) ? record.Total : stats.Max;
      }

      aHitMap[aType].Hits++;
      aHitMap[aType].Total += record.Total;
      aHitMap[aType].Max = (aHitMap[aType].Max < record.Total) ? record.Total : aHitMap[aType].Max;

      int critCount = stats.CritHits;
      LineModifiersParser.Parse(record, stats, aHitMap[aType]);

      // if crit count did not increase this hit was a non-crit
      if (critCount == stats.CritHits)
      {
        LongAddHelper.Add(aHitMap[aType].NonCritFreqValues, record.Total, 1);
      }
      else
      {
        LongAddHelper.Add(aHitMap[aType].CritFreqValues, record.Total, 1);
      }

      stats.LastTime = currentTime;

      if (record.AttackerPetType != "")
      {
        stats.IsPet = true;
        stats.Owner = record.AttackerOwner;
      }

      if (record.Type != Labels.BANE_TYPE)
      {
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

        DamageAtThisTime.PlayerDamage[record.Attacker] += record.Total;
        DamageAtThisTime.CurrentTime = currentTime;
      }

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

    public NonPlayer Find(string defender, string type)
    {
      NonPlayer npc = null;

      if (type == Labels.DOT_TYPE || type == Labels.DS_TYPE)
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

    private NonPlayer Create(string defender, DateTime currentTime, string origTimeString)
    {
      return new NonPlayer()
      {
        Name = defender,
        BeginTimeString = origTimeString,
        BeginTime = currentTime,
        LastTime = currentTime,
        DamageMap = new Dictionary<string, DamageStats>(),
        ResistMap = new Dictionary<string, Dictionary<string, int>>(),
        ID = CurrentNpcID++,
        GroupID = CurrentGroupID,
        CorrectMapKey = defender
      };
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
