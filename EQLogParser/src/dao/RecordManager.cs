using LiteDB;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace EQLogParser
{
  internal class RecordManager
  {
    internal event Action<string> RecordsUpdatedEvent;
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Lazy<RecordManager> Lazy = new(() => new RecordManager());
    internal static RecordManager Instance => Lazy.Value; // instance
    // records
    private const string DeathRecords = "DeathRecords";
    private const string LootRecords = "LootRecords";
    private const string LootRecordsToAssign = "LootRecordsToAssign";
    private const string MezBreakRecords = "MezBreakRecords";
    private const string RandomRecords = "RandomRecords";
    private const string ResistRecords = "ResistRecords";
    private const string ZoneRecords = "ZoneRecords";
    // stats
    private const string NpcResistStatsType = "NpcResistStats";
    private readonly string FilePath;
    private readonly object RecordLock = new();
    private readonly object StatsLock = new();
    private readonly Dictionary<string, List<RecordList>> RecordsActive = new();
    private readonly Dictionary<string, NpcResistStats> NpcResistStatsActive = new();
    private readonly Timer DatabaseUpdater;
    private LiteDatabase Db;

    private static readonly string[] TimedRecordTypes =
    {
      DeathRecords,
      LootRecords,
      LootRecordsToAssign,
      MezBreakRecords,
      RandomRecords,
      ResistRecords,
      ZoneRecords
    };

    private RecordManager()
    {
      try
      {
        FilePath = Path.GetTempFileName();
        Db = new LiteDatabase(FilePath);

        foreach (var type in TimedRecordTypes)
        {
          var records = Db.GetCollection<RecordList>(type);
          records.EnsureIndex(x => x.BeginTime);
        }

        var stats = Db.GetCollection<NpcResistStats>(NpcResistStatsType);
        stats.EnsureIndex(x => x.Id);
        DatabaseUpdater = new Timer(UpdateDatabase, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
      }
      catch (Exception e)
      {
        Log.Error(e);
      }
    }

    internal void Add(DeathRecord record, double beginTime) => Add(DeathRecords, record, beginTime);
    internal void Add(MezBreakRecord record, double beginTime) => Add(MezBreakRecords, record, beginTime);
    internal void Add(RandomRecord record, double beginTime) => Add(RandomRecords, record, beginTime);
    internal void Add(ZoneRecord record, double beginTime) => Add(ZoneRecords, record, beginTime);
    internal IEnumerable<(double, DeathRecord)> GetAllDeaths() => GetAll(DeathRecords).Select(r => (r.Item1, (DeathRecord)r.Item2));
    internal IEnumerable<(double, LootRecord)> GetAllLoot() => GetAll(LootRecords).Select(r => (r.Item1, (LootRecord)r.Item2));
    internal IEnumerable<(double, MezBreakRecord)> GetAllMezBreaks() => GetAll(MezBreakRecords).Select(r => (r.Item1, (MezBreakRecord)r.Item2));
    internal IEnumerable<(double, RandomRecord)> GetAllRandoms() => GetAll(RandomRecords).Select(r => (r.Item1, (RandomRecord)r.Item2));
    internal IEnumerable<(double, ZoneRecord)> GetAllZoning() => GetAll(ZoneRecords).Select(r => (r.Item1, (ZoneRecord)r.Item2));
    internal IEnumerable<(double, DeathRecord)> GetDeathsDuring(double beginTime, double endTime) =>
      GetDuring(DeathRecords, beginTime, endTime).Select(r => (r.Item1, (DeathRecord)r.Item2));
    internal IEnumerable<(double, ResistRecord)> GetResistsDuring(double beginTime, double endTime) =>
      GetDuring(ResistRecords, beginTime, endTime).Select(r => (r.Item1, (ResistRecord)r.Item2));

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
      if (Db?.GetCollection<RecordList>(LootRecordsToAssign) is { } toAssign && toAssign.Count() > 0)
      {
        lock (RecordLock)
        {
          // may need to remove more than one so search all
          foreach (var group in toAssign.FindAll().ToArray().Reverse())
          {
            foreach (var found in group.Records.Cast<LootRecord>().Where(r => r.Player == record.Player && r.Item == record.Item).ToArray())
            {
              Remove(toAssign, group, found);
              if (Db?.GetCollection<RecordList>(LootRecords) is { } looted &&
                  looted.FindOne(r => r.BeginTime.Equals(group.BeginTime)) is { } orig)
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

    internal void Add(ResistRecord record, double beginTime)
    {
      Add(ResistRecords, record, beginTime);
      if (DataManager.Instance.GetDetSpellByName(record.Spell) is { } spellData)
      {
        UpdateNpcResistStats(record.Defender, spellData.Resist, true);
      }
    }

    internal void UpdateNpcResistStats(string npc, SpellResist resist, bool resisted = false)
    {
      if (!string.IsNullOrEmpty(npc))
      {
        npc = npc.ToLower();
        lock (StatsLock)
        {
          if (!NpcResistStatsActive.TryGetValue(npc, out var npcStats))
          {
            npcStats = new NpcResistStats { Id = npc };
            NpcResistStatsActive[npc] = npcStats;
          }

          if (!npcStats.ByResist.TryGetValue(resist, out var count))
          {
            count = new ResistCount();
            npcStats.ByResist[resist] = count;
          }

          if (resisted)
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

    internal void Clear()
    {
      foreach (var type in TimedRecordTypes)
      {
        Db.GetCollection<RecordList>(type)?.DeleteAll();
      }
    }

    internal void Stop()
    {
      DatabaseUpdater.Dispose();
      Db?.Dispose();
      Db = null;

      try
      {
        File.Delete(FilePath);
      }
      catch (Exception e)
      {
        Log.Debug("Could not delete.", e);
      }
    }

    private void Add(string type, IAction record, double beginTime)
    {
      lock (RecordLock)
      {
        if (!RecordsActive.TryGetValue(type, out var list))
        {
          list = new List<RecordList>();
          RecordsActive[type] = list;
        }

        if (list.Find(item => item.BeginTime.Equals(beginTime)) is { } found)
        {
          found.Records.Add(record);
        }
        else
        {
          list.Add(new RecordList { BeginTime = beginTime, Records = new List<object> { record } });
        }
      }
    }

    private IEnumerable<Tuple<double, object>> GetAll(string type)
    {
      if (Db?.GetCollection<RecordList>(type) is { } groups)
      {
        foreach (var group in groups.FindAll())
        {
          foreach (var record in group.Records)
          {
            yield return Tuple.Create(group.BeginTime, record);
          }
        }
      }
    }

    private IEnumerable<Tuple<double, object>> GetDuring(string type, double beginTime, double endTime)
    {
      if (Db?.GetCollection<RecordList>(type) is { } groups)
      {
        foreach (var group in groups.Query().Where(r => r.BeginTime >= beginTime && r.BeginTime <= endTime).ToArray())
        {
          foreach (var record in group.Records)
          {
            yield return Tuple.Create(group.BeginTime, record);
          }
        }
      }
    }

    private void UpdateDatabase(object state)
    {
      lock (StatsLock)
      {
        if (NpcResistStatsActive.Count > 0 && Db?.GetCollection<NpcResistStats>(NpcResistStatsType) is { } stats)
        {
          foreach (var kv in NpcResistStatsActive)
          {
            if (stats.FindById(kv.Key) is not { } npcStats)
            {
              stats.Insert(kv.Value);
            }
            else
            {
              foreach (var resist in kv.Value.ByResist)
              {
                if (npcStats.ByResist.TryGetValue(resist.Key, out var count))
                {
                  count.Landed += resist.Value.Landed;
                  count.Resisted += resist.Value.Resisted;
                }
                else
                {
                  npcStats.ByResist[resist.Key] = resist.Value;
                }
              }

              stats.Update(npcStats);
            }
          }

          NpcResistStatsActive.Clear();
        }
      }

      var updatedTypes = new List<string>();
      lock (RecordLock)
      {
        if (RecordsActive.Count > 0)
        {
          foreach (var kv in RecordsActive)
          {
            updatedTypes.Add(kv.Key);
            if (Db?.GetCollection<RecordList>(kv.Key) is { } recordType)
            {
              foreach (ref var recordList in kv.Value.ToArray().AsSpan())
              {
                if (recordList.BeginTime is var time && recordType.FindOne(r => r.BeginTime.Equals(time)) is { } found)
                {
                  found.Records.AddRange(recordList.Records);
                  recordType.Update(found);
                }
                else
                {
                  recordType.Insert(recordList);
                }
              }
            }
          }

          RecordsActive.Clear();
        }
      }

      foreach (var type in updatedTypes)
      {
        RecordsUpdatedEvent?.Invoke(type);
      }
    }

    private static void Remove(ILiteCollection<RecordList> list, RecordList group, object item)
    {
      group.Records.Remove(item);
      if (group.Records.Count == 0)
      {
        list.Delete(group.Id);
      }
      else
      {
        list.Update(group);
      }
    }

    private class RecordList
    {
      // ReSharper disable once UnusedMember.Local
      public ObjectId Id { get; set; }
      public double BeginTime { get; init; }
      public List<object> Records { get; init; }
    }
  }
}
