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
    private const string NpcResistStatsType = "NpcResistStats";
    private readonly string FilePath;
    private readonly object RecordLock = new();
    private readonly object StatsLock = new();
    private readonly Dictionary<string, List<RecordList>> RecordsActive = new();
    private readonly Dictionary<string, NpcResistStats> NpcSpellStatsActive = new();
    private readonly Timer DatabaseUpdater;
    private LiteDatabase Db;

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

    internal void Add(SpellCast spell, double beginTime) => Add(SPELL_RECORDS, spell, beginTime);
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
    internal IEnumerable<(double, IAction)> GetSpellsDuring(double beginTime, double endTime) =>
      GetDuring(SPELL_RECORDS, beginTime, endTime).Select(r => (r.Item1, (IAction)r.Item2));

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
      if (Db?.GetCollection<RecordList>(LOOT_RECORDS_TO_ASSIGN) is { } toAssign && toAssign.Count() > 0)
      {
        lock (RecordLock)
        {
          // may need to remove more than one so search all
          foreach (var group in toAssign.FindAll().ToArray().Reverse())
          {
            foreach (var found in group.Records.Cast<LootRecord>().Where(r => r.Player == record.Player && r.Item == record.Item).ToArray())
            {
              Remove(toAssign, group, found);
              if (Db?.GetCollection<RecordList>(LOOT_RECORDS) is { } looted &&
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

    internal IEnumerable<NpcResistStats> GetAllNpcResistStats()
    {
      if (Db?.GetCollection<NpcResistStats>(NpcResistStatsType) is { } stats)
      {
        foreach (var stat in stats.FindAll())
        {
          yield return stat;
        }
      }
    }

    internal void UpdateNpcSpellStats(string npc, SpellResist resist, bool isResist = false)
    {
      if (!string.IsNullOrEmpty(npc))
      {
        npc = npc.ToLower();
        lock (StatsLock)
        {
          if (!NpcSpellStatsActive.TryGetValue(npc, out var npcStats))
          {
            npcStats = new NpcResistStats { Npc = npc };
            NpcSpellStatsActive[npc] = npcStats;
          }

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

    internal void Clear()
    {
      lock (RecordLock)
      {
        RecordsActive.Clear();
        foreach (var type in TimedRecordTypes)
        {
          Db?.GetCollection<RecordList>(type)?.DeleteAll();
        }
      }

      lock (StatsLock)
      {
        NpcSpellStatsActive.Clear();
        Db?.GetCollection<NpcResistStats>(NpcResistStatsType)?.DeleteAll();
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
      Dictionary<string, NpcResistStats> copyNpcSpellStats = null;
      lock (StatsLock)
      {
        if (NpcSpellStatsActive.Count > 0)
        {
          copyNpcSpellStats = new Dictionary<string, NpcResistStats>(NpcSpellStatsActive);
          NpcSpellStatsActive.Clear();
        }
      }

      if (copyNpcSpellStats != null && Db?.GetCollection<NpcResistStats>(NpcResistStatsType) is { } stats)
      {
        foreach (var kv in copyNpcSpellStats)
        {
          if (stats.FindOne(s => s.Npc == kv.Key) is not { } npcStats)
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
      }

      Dictionary<string, List<RecordList>> copyRecordsActive = null;
      lock (RecordLock)
      {
        if (RecordsActive.Count > 0)
        {
          copyRecordsActive = new Dictionary<string, List<RecordList>>(RecordsActive);
          RecordsActive.Clear();
        }
      }

      if (copyRecordsActive != null)
      {
        var updatedTypes = new List<string>();
        foreach (var kv in copyRecordsActive)
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

        foreach (var type in updatedTypes)
        {
          RecordsUpdatedEvent?.Invoke(type);
        }
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
