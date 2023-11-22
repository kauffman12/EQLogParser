using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Data;

namespace EQLogParser
{
  internal class RecordManager
  {
    internal event Action<string> RecordsUpdatedEvent;
    private static readonly Lazy<RecordManager> Lazy = new(() => new RecordManager());
    internal static RecordManager Instance => Lazy.Value; // instance
    // records
    public const string DEATH_RECORDS = "DeathRecords";
    public const string HEAL_RECORDS = "HealRecords";
    public const string LOOT_RECORDS = "LootRecords";
    public const string LOOT_RECORDS_TO_ASSIGN = "LootRecordsToAssign";
    public const string MEZ_BREAK_RECORDS = "MezBreakRecords";
    public const string RANDOM_RECORDS = "RandomRecords";
    public const string SPELL_RECORDS = "SpellRecords";
    public const string RESIST_RECORDS = "ResistRecords";
    public const string SPECIAL_RECORDS = "SpecialRecords";
    public const string ZONE_RECORDS = "ZoneRecords";
    // stats
    private readonly ConcurrentDictionary<string, List<RecordList>> RecordDicts = new();
    private readonly ConcurrentDictionary<string, bool> RecordNeedsEvent = new();
    private readonly Dictionary<string, NpcResistStats> NpcSpellStatsDict = new();
    private readonly List<RecordList> PlayerAmbiguityCastCache = new();
    // observables
    private readonly object CollectionLock = new();
    internal readonly ObservableCollection<QuickShareRecord> AllQuickShareRecords = new();
    private readonly Timer EventTimer;

    private static readonly string[] TimedRecordTypes =
    {
      DEATH_RECORDS,
      HEAL_RECORDS,
      LOOT_RECORDS,
      LOOT_RECORDS_TO_ASSIGN,
      MEZ_BREAK_RECORDS,
      RANDOM_RECORDS,
      SPELL_RECORDS,
      RESIST_RECORDS,
      SPECIAL_RECORDS,
      ZONE_RECORDS
    };

    private RecordManager()
    {
      BindingOperations.EnableCollectionSynchronization(AllQuickShareRecords, CollectionLock);

      // initialize dictionaries
      foreach (var type in TimedRecordTypes)
      {
        RecordDicts[type] = new List<RecordList>();
      }

      EventTimer = new Timer(SendEvents, null, TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(1500));
    }

    internal void Add(DeathRecord record, double beginTime) => Add(DEATH_RECORDS, record, beginTime);
    internal void Add(HealRecord record, double beginTime) => Add(HEAL_RECORDS, record, beginTime);
    internal void Add(MezBreakRecord record, double beginTime) => Add(MEZ_BREAK_RECORDS, record, beginTime);
    internal void Add(RandomRecord record, double beginTime) => Add(RANDOM_RECORDS, record, beginTime);
    internal void Add(ResistRecord record, double beginTime) => Add(RESIST_RECORDS, record, beginTime);
    internal void Add(ReceivedSpell spell, double beginTime) => Add(SPELL_RECORDS, spell, beginTime);
    internal void Add(SpecialRecord record, double beginTime) => Add(SPECIAL_RECORDS, record, beginTime);
    internal void Add(ZoneRecord record, double beginTime) => Add(ZONE_RECORDS, record, beginTime);
    internal IEnumerable<(double, DeathRecord)> GetAllDeaths() => GetAll(DEATH_RECORDS).Select(r => (r.Item1, (DeathRecord)r.Item2));
    internal IEnumerable<(double, HealRecord)> GetAllHeals() => GetAll(HEAL_RECORDS).Select(r => (r.Item1, (HealRecord)r.Item2));
    internal IEnumerable<(double, LootRecord)> GetAllLoot() => GetAll(LOOT_RECORDS).Select(r => (r.Item1, (LootRecord)r.Item2));
    internal IEnumerable<(double, MezBreakRecord)> GetAllMezBreaks() => GetAll(MEZ_BREAK_RECORDS).Select(r => (r.Item1, (MezBreakRecord)r.Item2));
    internal IEnumerable<(double, RandomRecord)> GetAllRandoms() => GetAll(RANDOM_RECORDS).Select(r => (r.Item1, (RandomRecord)r.Item2));
    internal IEnumerable<(double, ResistRecord)> GetAllResists() => GetAll(RESIST_RECORDS).Select(r => (r.Item1, (ResistRecord)r.Item2));
    internal IEnumerable<(double, SpecialRecord)> GetAllSpecials() => GetAll(SPECIAL_RECORDS).Select(r => (r.Item1, (SpecialRecord)r.Item2));
    internal IEnumerable<(double, ZoneRecord)> GetAllZoning() => GetAll(ZONE_RECORDS).Select(r => (r.Item1, (ZoneRecord)r.Item2));
    internal IEnumerable<(double, DeathRecord)> GetDeathsDuring(double beginTime, double endTime) =>
      GetDuring(DEATH_RECORDS, beginTime, endTime).Select(r => (r.Item1, (DeathRecord)r.Item2));
    internal IEnumerable<(double, HealRecord)> GetHealsDuring(double beginTime, double endTime) =>
      GetDuring(HEAL_RECORDS, beginTime, endTime).Select(r => (r.Item1, (HealRecord)r.Item2));
    internal IEnumerable<(double, IAction)> GetSpellsDuring(double beginTime, double endTime, bool reverse = false) =>
      GetDuring(SPELL_RECORDS, beginTime, endTime, reverse).Select(r => (r.Item1, (IAction)r.Item2));
    internal void Stop() => EventTimer?.Dispose();

    internal void Add(LootRecord record, double beginTime)
    {
      Add(LOOT_RECORDS, record, beginTime);
      if (record.IsCurrency) return;

      // if quantity zero then loot needs to be assigned
      if (record.Quantity == 0)
      {
        Add(LOOT_RECORDS_TO_ASSIGN, record, beginTime);
        return;
      }

      // loot assigned so remove previous instance
      if (RecordDicts.TryGetValue(LOOT_RECORDS_TO_ASSIGN, out var toAssign) && toAssign.Count > 0)
      {
        lock (toAssign)
        {
          // may need to remove more than one so search all
          var toAssignCopy = toAssign.ToArray();
          for (var i = toAssignCopy.Length - 1; i >= 0; i--)
          {
            var recordsCopy = toAssignCopy[i].Records.ToArray();
            foreach (var found in recordsCopy.Cast<LootRecord>().Where(r => r.Player == record.Player && r.Item == record.Item))
            {
              Remove(toAssign, toAssignCopy[i], found);
              if (RecordDicts.TryGetValue(LOOT_RECORDS, out var looted) && looted.FirstOrDefault(r => r.BeginTime.Equals(toAssignCopy[i].BeginTime)) is { } orig)
              {
                lock (looted)
                {
                  if (orig.Records.Cast<LootRecord>().FirstOrDefault(r => r.Player == record.Player && r.Item == record.Item) is { } found2)
                  {
                    Remove(looted, orig, found2);
                  }
                }
              }
            }
          }
        }
      }
    }

    internal void Add(SpellCast spell, double beginTime)
    {
      Add(SPELL_RECORDS, spell, beginTime);

      if (spell.SpellData?.HasAmbiguity == true)
      {
        Add(PlayerAmbiguityCastCache, spell, beginTime);
      }
    }

    internal void Add(QuickShareRecord action)
    {
      lock (CollectionLock)
      {
        if (AllQuickShareRecords.Count == 0 || AllQuickShareRecords[0].Key != action.Key ||
          !AllQuickShareRecords[0].BeginTime.Equals(action.BeginTime))
        {
          AllQuickShareRecords.Insert(0, action);
        }
      }
    }

    internal void Clear()
    {
      foreach (var type in TimedRecordTypes)
      {
        RecordDicts[type].Clear();
      }

      lock (PlayerAmbiguityCastCache)
      {
        PlayerAmbiguityCastCache.Clear();
      }

      lock (CollectionLock)
      {
        AllQuickShareRecords.Clear();
      }
    }

    internal IEnumerable<NpcResistStats> GetAllNpcResistStats()
    {
      NpcResistStats[] statsCopy;
      lock (NpcSpellStatsDict)
      {
        statsCopy = NpcSpellStatsDict.Values.ToArray();
      }

      foreach (var stat in statsCopy)
      {
        yield return stat;
      }
    }

    internal IEnumerable<(double, SpellCast)> GetSpellsLast(double duration)
    {
      lock (PlayerAmbiguityCastCache)
      {
        var end = PlayerAmbiguityCastCache.Count - 1;
        if (end > -1)
        {
          var endTime = PlayerAmbiguityCastCache[end].BeginTime - duration;
          for (var i = end; i >= 0 && PlayerAmbiguityCastCache[i].BeginTime >= endTime; i--)
          {
            var list = PlayerAmbiguityCastCache[i];
            for (var j = list.Records.Count - 1; j >= 0; j--)
            {
              yield return (list.BeginTime, (SpellCast)list.Records[j]);
            }
          }
        }
      }
    }

    internal bool IsQuickShareMine(string key)
    {
      lock (CollectionLock)
      {
        return AllQuickShareRecords.FirstOrDefault(share => share.IsMine && share.Key == key) != null;
      }
    }

    internal void UpdateNpcSpellStats(string npc, SpellResist resist, bool isResist = false)
    {
      if (!string.IsNullOrEmpty(npc))
      {
        NpcResistStats npcStats;
        npc = npc.ToLower();

        lock (NpcSpellStatsDict)
        {
          if (!NpcSpellStatsDict.TryGetValue(npc, out npcStats))
          {
            npcStats = new NpcResistStats { Npc = npc };
            NpcSpellStatsDict[npc] = npcStats;
          }
        }

        lock (npcStats)
        {
          if (!npcStats.ByResist.TryGetValue(resist, out var count))
          {
            count = new ResistCount();
            npcStats.ByResist[resist] = count;
          }

          if (isResist)
          {
            count.Resisted++;
          }
          else
          {
            count.Landed++;
          }
        }
      }
    }

    private void Add(string type, IAction record, double beginTime)
    {
      if (RecordDicts.TryGetValue(type, out var list))
      {
        Add(list, record, beginTime);
        RecordNeedsEvent[type] = true;
      }
    }

    private void Add(List<RecordList> list, object record, double beginTime)
    {
      RecordList found;
      lock (list)
      {
        if (list.Count == 0)
        {
          var newRecordList = new RecordList { BeginTime = beginTime, Records = new List<object> { record } };
          list.Add(newRecordList);
          return;
        }

        var end = list.Count - 1;
        if (list[end].BeginTime.Equals(beginTime))
        {
          found = list[end];
        }
        else
        {
          if (list[end].BeginTime < beginTime)
          {
            var newRecordList = new RecordList { BeginTime = beginTime, Records = new List<object> { record } };
            list.Add(newRecordList);
            return;
          }

          // this shouldn't be needed in the current implementation
          if (BinarySearch(list, item => item.BeginTime.CompareTo(beginTime)) is var index)
          {
            if (index > -1)
            {
              found = list[index];
            }
            else
            {
              var newRecordList = new RecordList { BeginTime = beginTime, Records = new List<object> { record } };
              list.Insert(~index, newRecordList);
              return;
            }
          }
        }
      }

      lock (found)
      {
        found.Records.Add(record);
      }
    }

    private IEnumerable<(double, object)> GetAll(string type)
    {
      if (RecordDicts.TryGetValue(type, out var list))
      {
        RecordList[] listCopy;
        lock (list)
        {
          listCopy = list.ToArray();
        }

        foreach (var group in listCopy)
        {
          object[] recordsCopy;
          lock (group.Records)
          {
            recordsCopy = group.Records.ToArray();
          }

          foreach (var record in recordsCopy)
          {
            yield return (group.BeginTime, record);
          }
        }
      }
    }

    private IEnumerable<(double, object)> GetDuring(string type, double beginTime, double endTime, bool reverse = false)
    {
      if (RecordDicts.TryGetValue(type, out var list))
      {
        List<RecordList> listCopy;
        lock (list)
        {
          listCopy = list.ToList();
        }

        if (reverse)
        {
          var index = BinarySearch(listCopy, item => item.BeginTime.CompareTo(endTime));
          index = index < 0 ? Math.Min(~index, listCopy.Count - 1) : index;
          for (var i = index; i >= 0 && i < listCopy.Count && listCopy[i].BeginTime >= beginTime && listCopy[i].BeginTime <= endTime; i--)
          {
            foreach (var record in ProcessRecordList(listCopy[i]))
            {
              yield return record;
            }
          }
        }
        else
        {
          var index = BinarySearch(listCopy, item => item.BeginTime.CompareTo(beginTime));
          index = index < 0 ? ~index : index;
          for (var i = index; i < listCopy.Count && listCopy[i].BeginTime >= beginTime && listCopy[i].BeginTime <= endTime; i++)
          {
            foreach (var record in ProcessRecordList(listCopy[i]))
            {
              yield return record;
            }
          }
        }
      }
    }

    private IEnumerable<(double, object)> ProcessRecordList(RecordList recordList)
    {
      object[] recordsCopy;
      lock (recordList)
      {
        recordsCopy = recordList.Records.ToArray();
      }

      foreach (var record in recordsCopy)
      {
        yield return (recordList.BeginTime, record);
      }
    }


    private void SendEvents(object state)
    {
      var keys = RecordNeedsEvent.Keys.ToArray();
      RecordNeedsEvent.Clear();
      foreach (var key in keys)
      {
        RecordsUpdatedEvent?.Invoke(key);
      }
    }

    private static void Remove(List<RecordList> list, RecordList group, object item)
    {
      group.Records.Remove(item);
      if (group.Records.Count == 0)
      {
        list.Remove(group);
      }
    }

    private static int BinarySearch<T>(List<T> list, Func<T, int> comparer)
    {
      int low = 0, high = list.Count - 1;

      while (low <= high)
      {
        var mid = low + ((high - low) / 2);
        var comparison = comparer(list[mid]);
        if (comparison == 0)
        {
          return mid;
        }

        if (comparison < 0)
        {
          low = mid + 1;
        }
        else
        {
          high = mid - 1;
        }
      }

      return ~low;
    }

    private class RecordList
    {
      public double BeginTime { get; init; }
      public List<object> Records { get; init; }
    }
  }
}
