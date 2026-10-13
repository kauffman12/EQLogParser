using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace EQLogParser
{
  /* Process-lifetime singleton: its Timer is disposed by Shutdown() when LifecycleManager tears the app
   * down (and it is never re-created afterwards), so it is deliberately not IDisposable. */
  [SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
    Justification = "Singleton owned by LifecycleManager; the event timer is disposed in Shutdown(). See class comment.")]
  internal class RecordsStore : ILifecycle
  {
    internal event Action<string> RecordsUpdatedEvent;
    private static readonly Lazy<RecordsStore> Lazy = new(() => new RecordsStore());
    internal static RecordsStore Instance => Lazy.Value; // instance
    // records
    public const string DeathRecords = "DeathRecords";
    // No heal lane: the capture's heal fact table is the record list (see HealRecordSource). Keeping a second copy
    // of every heal as live objects measured 766,713 HealRecord / 29.25 MB for two readers, one of which had already
    // moved to materialized spans.
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

    /*
     * A rolling horizon for the two cast caches below. The ONLY question either of them answers is "what did this spell NAME
     * cast recently", and the only door that asks it in production passes 8 seconds (EQDataStore.FindPreviousCast) — a buff
     * or wear-off line whose name could stand for several ranks, resolved by looking back up to eight seconds for a cast line
     * that printed the message. That query derives its window from the newest entry in these caches (the anchor is a quirk every
     * existing resolution depends on), so an entry older than `newest - horizon` cannot be returned by any question ever asked,
     * and keeping it to the end of the session was pure growth: 397,206 kept casts in one field run, most of them nights worth
     * of seconds nobody can reach.
     *
     * The horizon is 15x the widest window rather than 8 + 1 because the cost of being wrong is a rank resolved from the wrong
     * cast and the cost of being generous is a few hundred list slots. Both the query and the sweep read the SAME newest entry,
     * so a log whose clock jumps moves them together and stays consistent; that is also why the sweep keeps every entry at or
     * after the cutoff regardless of position (see DropOlderThanLocked) — an out-of-order append that lands inside the window can
     * sit anywhere in the list, and dropping it would answer the 8-second question wrong while the board still looked fine.
     */
    internal const double CastHistoryHorizonS = 120;

    private readonly List<RecordList> _playerAmbiguityCastCache = [];

    // Log time at which the next sweep runs: one per horizon, so the amortized cost per cast is a division.
    private double _nextCastPruneAtS = double.NaN;
    // One entry per ambiguous spell name. The value carries its own ordering fact — see CastHistory.
    private readonly ConcurrentDictionary<string, CastHistory> _spellNameIndex = new();

    private readonly Timer _eventTimer;

    private static readonly string[] TimedRecordTypes =
    [
      DeathRecords,
      LootRecords,
      LootRecordsToAssign,
      MezBreakRecords,
      RandomRecords,
      SpellRecords,
      ResistRecords,
      SpecialRecords,
      ZoneRecords
    ];

    private RecordsStore()
    {
      LifecycleManager.Register(this);

      // initialize dictionaries
      foreach (var type in TimedRecordTypes)
      {
        _recordDictionaries[type] = [];
      }

      _eventTimer = new Timer(SendEvents, null, TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(1500));
    }

    internal void Add(DeathRecord record, double beginTime) => Add(DeathRecords, record, beginTime);
    internal void Add(MezBreakRecord record, double beginTime) => Add(MezBreakRecords, record, beginTime);
    internal void Add(RandomRecord record, double beginTime) => Add(RandomRecords, record, beginTime);
    internal void Add(ResistRecord record, double beginTime) => Add(ResistRecords, record, beginTime);
    internal void Add(ReceivedSpell spell, double beginTime) => Add(SpellRecords, spell, beginTime);
    internal void Add(SpecialRecord record, double beginTime) => Add(SpecialRecords, record, beginTime);
    internal void Add(ZoneRecord record, double beginTime) => Add(ZoneRecords, record, beginTime);
    internal IEnumerable<(double, DeathRecord)> GetAllDeaths() => GetAll(DeathRecords).Select(r => (r.Item1, (DeathRecord)r.Item2));
    internal IEnumerable<(double, LootRecord)> GetAllLoot() => GetAll(LootRecords).Select(r => (r.Item1, (LootRecord)r.Item2));
    internal IEnumerable<(double, MezBreakRecord)> GetAllMezBreaks() => GetAll(MezBreakRecords).Select(r => (r.Item1, (MezBreakRecord)r.Item2));
    internal IEnumerable<(double, RandomRecord)> GetAllRandoms() => GetAll(RandomRecords).Select(r => (r.Item1, (RandomRecord)r.Item2));
    internal IEnumerable<(double, ResistRecord)> GetAllResists() => GetAll(ResistRecords).Select(r => (r.Item1, (ResistRecord)r.Item2));
    internal IEnumerable<(double, SpecialRecord)> GetAllSpecials() => GetAll(SpecialRecords).Select(r => (r.Item1, (SpecialRecord)r.Item2));
    internal IEnumerable<(double, ZoneRecord)> GetAllZoning() => GetAll(ZoneRecords).Select(r => (r.Item1, (ZoneRecord)r.Item2));
    internal IEnumerable<(double, DeathRecord)> GetDeathsDuring(double beginTime, double endTime) =>
      GetDuring(DeathRecords, beginTime, endTime).Select(r => (r.Item1, (DeathRecord)r.Item2));
    internal IEnumerable<(double, IAction)> GetSpellsDuring(double beginTime, double endTime, bool reverse = false) =>
      GetDuring(SpellRecords, beginTime, endTime, reverse).Select(r => (r.Item1, (IAction)r.Item2));

    public void Shutdown()
    {
      _eventTimer?.Dispose();
      Clear();
    }

    public void Clear(bool serverChanged = true)
    {
      foreach (var type in TimedRecordTypes)
      {
        _recordDictionaries[type].Clear();
      }

      _recordNeedsEvent.Clear();

      lock (_playerAmbiguityCastCache)
      {
        _playerAmbiguityCastCache.Clear();
      }

      foreach (var history in _spellNameIndex.Values)
      {
        lock (history)
        {
          history.Casts.Clear();

          // A cleared history is ordered again by definition, and the flag is per session: the next log's appends
          // start from nothing, so carrying a retired fast path across captures would cost queries for no safety.
          history.AscendingTime = true;
        }
      }
      _spellNameIndex.Clear();

      // A new capture starts the horizon over: the first cast of the next session schedules the first sweep.
      _nextCastPruneAtS = double.NaN;

      lock (_npcSpellStatsDict)
      {
        _npcSpellStatsDict.Clear();
      }

    }

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
          // remove old records first
          toAssign.RemoveAll(r => (beginTime - r.BeginTime) > 1800);

          var toAssignCopy = toAssign.ToArray();
          for (var i = toAssignCopy.Length - 1; i >= 0; i--)
          {
            FindItem(i, toAssign, toAssignCopy, LootedQuery);
            FindItem(i, toAssign, toAssignCopy, LeftQuery);
          }
        }
      }

      void FindItem(int i, List<RecordList> toAssign, RecordList[] toAssignCopy, Func<LootRecord, bool> query)
      {
        var recordsCopy = toAssignCopy[i].Records.ToArray();
        if (recordsCopy.Cast<LootRecord>().FirstOrDefault(query) is { } found)
        {
          Remove(toAssign, toAssignCopy[i], found);
          if (_recordDictionaries.TryGetValue(LootRecords, out var looted) && looted.FirstOrDefault(r => r.BeginTime.Equals(toAssignCopy[i].BeginTime)) is { } orig)
          {
            lock (looted)
            {
              if (orig.Records.Cast<LootRecord>().FirstOrDefault(query) is { } found2)
              {
                Remove(looted, orig, found2);
              }
            }
          }
        }
      }

      bool LootedQuery(LootRecord r) => (r.Npc?.StartsWith("Given (", StringComparison.OrdinalIgnoreCase) == true || r.Npc?.StartsWith("Won Roll", StringComparison.OrdinalIgnoreCase) == true) &&
        r.Player == record.Player && r.Item == record.Item;
      bool LeftQuery(LootRecord r) => r.Npc?.EndsWith("(Left on Chest)", StringComparison.OrdinalIgnoreCase) == true &&
        r.Item == record.Item && r.Npc?.StartsWith(record.Npc, StringComparison.OrdinalIgnoreCase) == true;
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
        if (string.IsNullOrEmpty(spell.Spell)) return;
        var cached = new CachedCast(beginTime, spell);
        var history = _spellNameIndex.GetOrAdd(spell.Spell, _ => new CastHistory());
        lock (history)
        {
          history.Add(cached);
        }

        PruneCastHistoryLocked(beginTime);
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

    internal List<CachedCast> GetCastsBySpellName(string spellName, double duration)
    {
      /*
       * "What did this spell name cast in the last few seconds?" — asked while resolving an ambiguous
       * abbreviation (EQDataStore.FindPreviousCast), several times per ambiguous line, against a history that
       * grows all night. The old shape paid for both halves of that: it allocated the result with capacity for
       * the spell's ENTIRE history, and it walked the entire history looking for a handful of recent entries.
       *
       * Measured on Incogitable (figures in docs/DesignNotes.md): 214,487 queries visited 149,232,044 entries and returned
       * 164,263 matches — about 700 entries examined per match. The same bound scan visits 376,345 and returns the
       * identical count; on the 952 MiB capture it was 53.7 M visited for ~235 K matches, on beta 67.8 M for ~265 K.
       * That is a scaling hazard rather than a headline speedup (the measurement recorded no proportional whole-load
       * win, and none is promised here): a question about eight seconds should not cost in proportion to a night.
       *
       * Two changes, one per half. The result list grows instead of being pre-sized to the history — measured 0.77
       * matches per query, so the old capacity was ~900× the answer. And the scan stops at the first entry older than
       * the window WHEN IT CAN PROVE THE REST ARE OLDER STILL, which is only when this spell's appends all arrived
       * ascending (CastHistory.AscendingTime). Nothing assumes monotonic time from the file: one out-of-order append
       * retires the fast path for that name and the full walk comes back.
       */
      if (!_spellNameIndex.TryGetValue(spellName, out var history))
      {
        return [];
      }

      /*
       * The anchor stays exactly as it was — the newest cast in the global ambiguity cache, not this spell's newest and
       * not wall time. It is a quirk (a log whose clock jumps moves the window) and it is the behaviour every existing
       * resolution depends on, so bounding the scan is not the day to change which casts are "recent".
       *
       * What the locking lesson cost (2026-10-13) is that these lists are NOT single-thread property any more: every
       * stream that parses appends to them and runs the horizon prune, and pruning's RemoveRange CLEARS the vacated
       * slots — one file can have several readers alive (the open's own loop plus a trigger reader per enabled
       * character), so "exactly one thread ever parsed" was never the invariant it looked like. Reading Count and
       * indexing the last entry outside the cache lock latches a stale size over a sweeping list: the field NRE
       * surfaced at `FindByLandsOn` because both this method and FindPreviousCast
       * inline, so the throw pointed at a call site with no frame naming it. The lock covers anchor reads AND the scan
       * because both must agree with each other; the order is cache → history, the one Add already uses (re-verifying
       * the order is load-bearing: nothing here may ever lock history first). RecordsStoreConcurrencyTest storms this.
       */
      lock (_playerAmbiguityCastCache)
      {
        var end = _playerAmbiguityCastCache.Count - 1;
        if (end <= -1) return [];

        var endTime = _playerAmbiguityCastCache[end].BeginTime - duration;
        lock (history)
        {
          var casts = history.Casts;
          // Plain null, no `?`: Core compiles with nullable annotations disabled, and annotating anyway is the
          // CS8632 this repo has had nine of — the fix is the annotation context or none at all, never a pragma.
          List<CachedCast> result = null;
          for (var i = casts.Count - 1; i >= 0; i--)
          {
            var cast = casts[i];
            if (cast.BeginTime >= endTime)
            {
              // Grown on demand: a query that finds nothing allocates no list at all (Array.Empty below), and one
              // that finds its usual single match pays for four slots rather than for the night.
              result ??= [];
              result.Add(cast);
            }
            else if (history.AscendingTime)
            {
              // Ascending appends, so everything further back is at least as old as this entry.
              break;
            }
          }

          return result ?? [];
        }
      }
    }

    /*
     * Drop what no question can reach anymore. Runs under the caller's lock on the add path, once per horizon of LOG time (not
     * once per cast), and touches only these two caches — SpellRecords keeps its full session list because a buff column may ask
     * for any window of the night through GetSpellsDuring.
     */
    private void PruneCastHistoryLocked(double newestS)
    {
      if (!double.IsNaN(_nextCastPruneAtS) && newestS < _nextCastPruneAtS)
      {
        return;
      }

      // Sweep every quarter-horizon so what is retained stays close to one horizon; a sweep costs a division per cast in between.
      _nextCastPruneAtS = newestS + CastHistoryHorizonS / 4;
      var cutoff = newestS - CastHistoryHorizonS;

      lock (_playerAmbiguityCastCache)
      {
        var keep = 0;
        for (var i = 0; i < _playerAmbiguityCastCache.Count; i++)
        {
          if (_playerAmbiguityCastCache[i].BeginTime >= cutoff)
          {
            if (keep != i) _playerAmbiguityCastCache[keep] = _playerAmbiguityCastCache[i];
            keep++;
          }
        }

        if (keep < _playerAmbiguityCastCache.Count) _playerAmbiguityCastCache.RemoveRange(keep, _playerAmbiguityCastCache.Count - keep);
      }

      /*
       * Order-preserving compaction per name: keep every entry at or after the cutoff, in arrival order. Deliberately NOT
       * "RemoveRange from the front while the front is old" — these lists come from a text log and CastHistory.AscendingTime exists
       * precisely because appends are not guaranteed ascending, so a recent entry can be parked anywhere in the list. A front-only
       * sweep there is the one mistake that changes WHICH rank an abbreviation resolves to, and the board would look fine anyway.
       */
      foreach (var history in _spellNameIndex.Values)
      {
        lock (history)
        {
          var casts = history.Casts;
          var keep = 0;
          for (var i = 0; i < casts.Count; i++)
          {
            if (casts[i].BeginTime >= cutoff)
            {
              if (keep != i) casts[keep] = casts[i];
              keep++;
            }
          }

          if (keep < casts.Count) casts.RemoveRange(keep, casts.Count - keep);
        }
      }
    }

    internal void UpdateNpcSpellStats(string npc, SpellResist resist, bool isResist = false)
    {
      if (!string.IsNullOrEmpty(npc))
      {
        NpcResistStats npcStats;
        npc = npc.ToLower(null);

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
          if (CollectionUtil.BinarySearch(list, item => item.BeginTime.CompareTo(beginTime)) is var index)
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
          var index = CollectionUtil.BinarySearch(listCopy, item => item.BeginTime.CompareTo(endTime));
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
          var index = CollectionUtil.BinarySearch(listCopy, item => item.BeginTime.CompareTo(beginTime));
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



    private class RecordList
    {
      public double BeginTime { get; init; }
      public List<object> Records { get; init; }
    }

    internal readonly struct CachedCast
    {
      public readonly double BeginTime;
      public readonly SpellCast Cast;
      internal CachedCast(double beginTime, SpellCast cast) { BeginTime = beginTime; Cast = cast; }
    }

    /*
     * One ambiguous spell's cast history, plus the ONE fact about that history which lets a recent-cast query stop
     * early: whether every append landed at or after its predecessor. Tracked rather than assumed because the input
     * is a text log — timestamps can repeat, and a restored or concatenated file can hand the parser seconds out of
     * order. Three measured captures showed no violation, and a production path may not be built on that: one
     * backwards append flips this flag for good (within a session) and the caller goes back to walking the whole
     * list, which is exactly what it did before the bound existed. Losing the fast path is cheap; losing matches is
     * not, and an assumption here would lose them silently — same-looking board, wrong spell resolved.
     *
     * Equal timestamps keep the flag set: the window test is inclusive (>= endTime), so a run of casts inside one
     * second is still ascending for the purpose of "everything further back is older".
     */
    private sealed class CastHistory
    {
      internal readonly List<CachedCast> Casts = [];

      internal bool AscendingTime = true;

      internal void Add(CachedCast cast)
      {
        if (AscendingTime && Casts.Count > 0 && cast.BeginTime < Casts[^1].BeginTime)
        {
          AscendingTime = false;
        }

        Casts.Add(cast);
      }
    }
  }
}
