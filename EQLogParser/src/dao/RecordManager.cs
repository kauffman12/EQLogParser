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
    public const string DeathRecords = "DeathRecords";
    public const string HealRecords = "HealRecords";
    public const string LootRecords = "LootRecords";
    public const string LootRecordsToAssign = "LootRecordsToAssign";
    public const string MezBreakRecords = "MezBreakRecords";
    public const string RandomRecords = "RandomRecords";
    public const string SpellRecords = "SpellRecords";
    public const string ResistRecords = "ResistRecords";
    public const string SpecialRecords = "SpecialRecords";
    public const string ZoneRecords = "ZoneRecords";
    // stats
    private readonly ConcurrentDictionary<string, List<RecordList>> _recordDictionaries = new();
    private readonly ConcurrentDictionary<string, bool> _recordNeedsEvent = new();
    private readonly Dictionary<string, NpcResistStats> _npcSpellStatsDict = [];
    private readonly List<RecordList> _playerAmbiguityCastCache = [];
    // observables
    private readonly object _collectionLock = new();
    internal readonly ObservableCollection<QuickShareRecord> AllQuickShareRecords = [];
    private readonly Timer _eventTimer;

    private static readonly string[] TimedRecordTypes =
    {
      DeathRecords,
      HealRecords,
      LootRecords,
      LootRecordsToAssign,
      MezBreakRecords,
      RandomRecords,
      SpellRecords,
      ResistRecords,
      SpecialRecords,
      ZoneRecords
    };

    private RecordManager()
    {
      BindingOperations.EnableCollectionSynchronization(AllQuickShareRecords, _collectionLock);

      // initialize dictionaries
      foreach (var type in TimedRecordTypes)
      {
        _recordDictionaries[type] = [];
      }

      _eventTimer = new Timer(SendEvents, null, TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(1500));
    }

    internal void Add(DeathRecord record, double beginTime) => Add(DeathRecords, record, beginTime);
    internal void Add(HealRecord record, double beginTime) => Add(HealRecords, record, beginTime);
    internal void Add(MezBreakRecord record, double beginTime) => Add(MezBreakRecords, record, beginTime);
    internal void Add(RandomRecord record, double beginTime) => Add(RandomRecords, record, beginTime);
    internal void Add(ResistRecord record, double beginTime) => Add(ResistRecords, record, beginTime);
    internal void Add(ReceivedSpell spell, double beginTime) => Add(SpellRecords, spell, beginTime);
    internal void Add(SpecialRecord record, double beginTime) => Add(SpecialRecords, record, beginTime);
    internal void Add(ZoneRecord record, double beginTime) => Add(ZoneRecords, record, beginTime);
    internal IEnumerable<(double, DeathRecord)> GetAllDeaths() => GetAll(DeathRecords).Select(r => (r.Item1, (DeathRecord)r.Item2));
    internal IEnumerable<(double, HealRecord)> GetAllHeals() => GetAll(HealRecords).Select(r => (r.Item1, (HealRecord)r.Item2));
    internal IEnumerable<(double, LootRecord)> GetAllLoot() => GetAll(LootRecords).Select(r => (r.Item1, (LootRecord)r.Item2));
    internal IEnumerable<(double, MezBreakRecord)> GetAllMezBreaks() => GetAll(MezBreakRecords).Select(r => (r.Item1, (MezBreakRecord)r.Item2));
    internal IEnumerable<(double, RandomRecord)> GetAllRandoms() => GetAll(RandomRecords).Select(r => (r.Item1, (RandomRecord)r.Item2));
    internal IEnumerable<(double, ResistRecord)> GetAllResists() => GetAll(ResistRecords).Select(r => (r.Item1, (ResistRecord)r.Item2));
    internal IEnumerable<(double, SpecialRecord)> GetAllSpecials() => GetAll(SpecialRecords).Select(r => (r.Item1, (SpecialRecord)r.Item2));
    internal IEnumerable<(double, ZoneRecord)> GetAllZoning() => GetAll(ZoneRecords).Select(r => (r.Item1, (ZoneRecord)r.Item2));
    internal IEnumerable<(double, DeathRecord)> GetDeathsDuring(double beginTime, double endTime) =>
      GetDuring(DeathRecords, beginTime, endTime).Select(r => (r.Item1, (DeathRecord)r.Item2));
    internal IEnumerable<(double, HealRecord)> GetHealsDuring(double beginTime, double endTime) =>
      GetDuring(HealRecords, beginTime, endTime).Select(r => (r.Item1, (HealRecord)r.Item2));
    internal IEnumerable<(double, IAction)> GetSpellsDuring(double beginTime, double endTime, bool reverse = false) =>
      GetDuring(SpellRecords, beginTime, endTime, reverse).Select(r => (r.Item1, (IAction)r.Item2));
    internal void Stop() => _eventTimer?.Dispose();

    internal void Add(LootRecord record, double beginTime)
    {
      Add(LootRecords, record, beginTime);
      if (record.IsCurrency) return;

      // if quantity zero then loot needs to be assigned
      if (record.Quantity == 0)
      {
        Add(LootRecordsToAssign, record, beginTime);
        return;
      }

      // loot assigned so remove previous instance
      if (_recordDictionaries.TryGetValue(LootRecordsToAssign, out var toAssign) && toAssign.Count > 0)
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
              if (_recordDictionaries.TryGetValue(LootRecords, out var looted) && looted.FirstOrDefault(r => r.BeginTime.Equals(toAssignCopy[i].BeginTime)) is { } orig)
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
      Add(SpellRecords, spell, beginTime);
      if (spell.SpellData?.HasAmbiguity != true)
      {
        return;
      }

      lock (_playerAmbiguityCastCache)
      {
        Add(_playerAmbiguityCastCache, spell, beginTime);
      }
    }

    internal void Add(QuickShareRecord action)
    {
      lock (_collectionLock)
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
        _recordDictionaries[type].Clear();
      }

      lock (_playerAmbiguityCastCache)
      {
        _playerAmbiguityCastCache.Clear();
      }

      lock (_collectionLock)
      {
        AllQuickShareRecords.Clear();
      }

      lock (_npcSpellStatsDict)
      {
        _npcSpellStatsDict.Clear();
      }
    }

    internal IEnumerable<NpcResistStats> GetAllNpcResistStats()
    {
      NpcResistStats[] statsCopy;
      lock (_npcSpellStatsDict)
      {
        statsCopy = _npcSpellStatsDict.Values.ToArray();
      }

      foreach (var stat in statsCopy)
      {
        yield return stat;
      }
    }

    internal IEnumerable<(double, SpellCast)> GetSpellsLast(double duration)
    {
      lock (_playerAmbiguityCastCache)
      {
        var end = _playerAmbiguityCastCache.Count - 1;

        if (end <= -1)
        {
          yield break;
        }

        var endTime = _playerAmbiguityCastCache[end].BeginTime - duration;
        for (var i = end; i >= 0 && _playerAmbiguityCastCache[i].BeginTime >= endTime; i--)
        {
          var list = _playerAmbiguityCastCache[i];
          for (var j = list.Records.Count - 1; j >= 0; j--)
          {
            yield return (list.BeginTime, (SpellCast)list.Records[j]);
          }
        }
      }
    }

    internal bool IsQuickShareMine(string key)
    {
      lock (_collectionLock)
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

        lock (_npcSpellStatsDict)
        {
          if (!_npcSpellStatsDict.TryGetValue(npc, out npcStats))
          {
            npcStats = new NpcResistStats { Npc = npc };
            _npcSpellStatsDict[npc] = npcStats;
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
      if (!_recordDictionaries.TryGetValue(type, out var list))
      {
        return;
      }

      Add(list, record, beginTime);
      _recordNeedsEvent[type] = true;
    }

    private static void Add(List<RecordList> list, object record, double beginTime)
    {
      RecordList found;
      lock (list)
      {
        if (list.Count == 0)
        {
          var newRecordList = new RecordList { BeginTime = beginTime, Records = [record] };
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
            var newRecordList = new RecordList { BeginTime = beginTime, Records = [record] };
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
              var newRecordList = new RecordList { BeginTime = beginTime, Records = [record] };
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
      if (_recordDictionaries.TryGetValue(type, out var list))
      {
        RecordList[] listCopy;
        lock (list)
        {
          listCopy = [.. list];
        }

        foreach (var group in listCopy)
        {
          object[] recordsCopy;
          lock (group.Records)
          {
            recordsCopy = [.. group.Records];
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
      if (_recordDictionaries.TryGetValue(type, out var list))
      {
        List<RecordList> listCopy;
        lock (list)
        {
          listCopy = [.. list];
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

    private static IEnumerable<(double, object)> ProcessRecordList(RecordList recordList)
    {
      object[] recordsCopy;
      lock (recordList)
      {
        recordsCopy = [.. recordList.Records];
      }

      foreach (var record in recordsCopy)
      {
        yield return (recordList.BeginTime, record);
      }
    }


    private void SendEvents(object state)
    {
      var keys = _recordNeedsEvent.Keys.ToArray();
      _recordNeedsEvent.Clear();
      foreach (var key in keys)
      {
        RecordsUpdatedEvent?.Invoke(key);
      }
    }

    private static void Remove(ICollection<RecordList> list, RecordList group, object item)
    {
      group.Records.Remove(item);
      if (group.Records.Count == 0)
      {
        list.Remove(group);
      }
    }

    private static int BinarySearch<T>(IReadOnlyList<T> list, Func<T, int> comparer)
    {
      int low = 0, high = list.Count - 1;

      while (low <= high)
      {
        var mid = low + ((high - low) / 2);
        var comparison = comparer(list[mid]);

        switch (comparison)
        {
          case 0:
            return mid;
          case < 0:
            low = mid + 1;
            break;
          default:
            high = mid - 1;
            break;
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
