/*
 * Annotations only: this project compiles with nullable disabled, and this file annotates (a block slot that may not be open yet,
 * a spell-count map that exists only when the AE setting asks for one). One line, no behaviour; docs/CodingStandards.md → Nullable Reference Types.
 */
#nullable enable annotations

using log4net;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EQLogParser
{
  internal class HealingStatsBuilder
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal static HealingStatsBuilder Instance = new();
    internal event Action<DataPointEvent> EventsUpdateDataPoint;
    internal event Action<StatsGenerationEvent> EventsGenerationStatus;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _healedByHealerTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _healedBySpellTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _healedByHealerSpellTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _healerHealedTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _healerSpellTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _healerHealedSpellTimeRanges = new();

    /*
     * "healer|healed" — the key the "who healed whom" breakdown is filed under — composed once per PAIR per build instead of
     * once per heal record. Same law as DamageStatsBuilder's pet-row memo: a night has hundreds of healer/healed pairs and
     * millions of heals, so a concatenation in the walk is garbage by the megabyte for a string the walk already knew.
     */
    private readonly Dictionary<(string Healer, string Healed), string> _healerHealedKeys = [];
    private readonly List<List<ActionGroup>> _healingGroups = [];
    private ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _allHealedByHealerTimeRanges;
    private ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _allHealedBySpellTimeRanges;
    private ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _allHealedByHealerSpellTimeRanges;
    private List<List<ActionGroup>> _allHealingGroups;
    private PlayerStats _raidTotals;
    private List<Fight> _selected;
    private TimeRange _allRanges;
    private StatsGenerationEvent _lastStatsEvent;
    private string _title;
    private bool _isLimited;

    /*
     * The clear signal belongs to the singleton, not to an instance - the same law DamageStatsBuilder documents with
     * its measurement. Nothing builds a throwaway healing scope today, which is exactly why this is written the safe
     * way now: the day someone does (a heal column on the meter is the obvious candidate), an instance subscription
     * would leak one board per refresh and nobody would think to look here.
     */
    static HealingStatsBuilder()
    {
      Instance.ClearedByActiveData();
    }

    private void ClearedByActiveData() => CombatEvents.ActiveDataCleared += (_) =>
    {
      lock (_lock)
      {
        Reset(true);
      }
    };

    internal StatsGenerationEvent GetLastStats()
    {
      lock (_lock)
      {
        return _lastStatsEvent;
      }
    }

    internal void RebuildTotalStats(GenerateStatsOptions options)
    {
      var built = false;

      lock (_lock)
      {
        if (_healingGroups.Count != 0)
        {
          options.Npcs.AddRange(_selected);
          options.AllRanges = _allRanges;
          BuildTotalStats(options);
          built = true;
        }
      }

      // Outside the lock: the tidy waits before collecting, and it must not be holding the builder while it does.
      if (built)
      {
        GcTidyUp.Request("stats built");
      }
    }

    /*
     * Traced wrapper (see StatsBuildTrace). RebuildTotalStats needs none of its own: it delegates here on the same thread with the
     * same options, so the door label rides along and one line covers both — tracing the nesting would print a build twice and warn
     * about an overlap that is really one caller.
     *
     * The detail names where the records came from, which is the difference between "this board describes the clicked selection"
     * and "this board describes every heal in the record store": options.Heals null means the store.
     */
    internal void BuildTotalStats(GenerateStatsOptions options)
    {
      var trace = StatsBuildTrace.Begin("healing", options.Source);
      try
      {
        BuildTotalStatsCore(options, trace);
      }
      finally
      {
        StatsBuildTrace.End(trace, options.Heals is null
          ? $"records from the store (window {options.MinSeconds}..{options.MaxSeconds})"
          : $"{options.Heals.Count:N0} materialized heal(s), window {options.MinSeconds}..{options.MaxSeconds}");
      }
    }

    private void BuildTotalStatsCore(GenerateStatsOptions options, in StatsBuildTrace.Handle trace)
    {
      lock (_lock)
      {
        try
        {
          FireNewStatsEvent();
          Reset();

          _lastStatsEvent = null;
          _selected = [.. options.Npcs];
          _selected.Sort(static (a, b) => a.Id.CompareTo(b.Id));

          /*
           * The title comes from the SORTED selection, never from `options.Npcs[0]`. The sort above is the legacy law - a
           * selection's fights are counted in time order, and row ids are handed out in the fight list's own display order
           * (`published[i].Id = i + 1`), so the lowest id IS the first row of the list. Reading element zero of the caller's
           * raw input instead made the title a property of however the grid happened to enumerate its selection: a whole-capture
           * select-all then printed a different NPC every time (field report, `EQLogParser.log`: three select-alls over identical
           * rows, three titles), while legacy printed the top row 100% of the time because its selection arrived in list order.
           */
          _title = _selected.Count > 0 ? _selected[0].Name : null;

          var healingValidator = new HealingValidator(
            AppSettings.IsAoEHealingEnabled, AppSettings.IsHealingSwarmPetsEnabled);
          _isLimited = healingValidator.IsHealingLimited();

          // rebuild of stats after turning off AOE healing, etc is more
          // complex so can't just call compute again. save settings like
          // selected and allRanges so they can be applied again on rebuild
          _allRanges = options.AllRanges;
          _raidTotals.Ranges.Add(options.AllRanges.TimeSegments);
          _raidTotals.AllRanges.Add(options.AllRanges.TimeSegments);
          _raidTotals.MaxBeginTime = double.NaN;
          _raidTotals.MinBeginTime = double.NaN;

          if (_raidTotals.Ranges.TimeSegments.Count > 0)
          {
            /*
             * The board's one source, and the seam the derivation feeds: options.Heals is null for every caller that
             * wants the whole open capture, and a materialized list when a derived fight selection is being summarized —
             * where a NON-NULL BUT EMPTY list means "this selection healed nothing", and must not fall through to the
             * whole capture (that inversion is how a selection ends up displaying somebody else's numbers). The null arm
             * reads the capture's heal rows through
             * HealRecordSource rather than a list of live records (see there: the store copy cost 29 MB for two
             * readers). Nothing else about this method differs between the two — same window, same HealingValidator
             * filters, same grouping — which is what makes the two boards comparable.
             */
            var allHeals = options.Heals ?? HealRecordSource.All();
            // calculate totals first since it can modify the ranges
            _raidTotals.TotalSeconds = _raidTotals.MaxTime = _raidTotals.Ranges.GetTotal();

            var startTime = double.NaN;
            var stopTime = double.NaN;
            if ((options.MaxSeconds > -1 && options.MaxSeconds < _raidTotals.MaxTime) || (options.MinSeconds > 0 && options.MinSeconds < _raidTotals.MaxTime))
            {
              StatsUtil.UpdateMinMaxTimes(_raidTotals, options, out startTime, out stopTime);
              _raidTotals.TotalSeconds = options.MaxSeconds - options.MinSeconds;
              _raidTotals.MinTime = options.MinSeconds;
            }

            /*
             * One cursor for every segment. The selection's segments are ascending and merged (`TimeRange.Add`), the heal
             * list is time ascending by law, so a scan that restarts at index 0 for each segment — which is what
             * `FindIndex(0, …)` did — walked the capture once PER SEGMENT: 4,452 segments over 403,740 heals measured
             * 2,756 ms of a 2,945 ms healing build. The cursor carries forward instead, and the inner loop stops at the
             * segment's end rather than at the end of the list.
             */
            var cursor = 0;

            /*
             * Two things this pass used to pay per heal LINE, on a setting most players never touch:
             *
             *   - `currentSpellCounts = []` allocated a fresh dictionary for every record (a farm night: 2,670,809 of them), and the
             *     history dictionary it is filed into was rebuilt per record too. Both exist only to serve group-AE/MGB filtering, which
             *     is the ONLY writer into them, and it runs only when "Count AoE healing" is OFF (`HealingValidator.TracksGroupAe`).
             *   - pass 2 built a composite ignore-key STRING per record to ask a collection that could only be empty in that same case.
             *
             * They are now allocated once per segment, and only when the setting asks. Same rules, same order, nothing counted
             * differently - just no garbage for a question nobody is asking (docs/DesignNotes.md -> "Where a board build's time actually goes").
             */
            var tracksGroupAe = healingValidator.TracksGroupAe;

            /*
             * "Is the name being healed one of ours?" is three registry lookups plus a name-shape scan, and it was asked once per heal
             * line - 2.65 M times over a farm night, for a few hundred distinct names. The answer cannot change inside one build (the
             * roster is written between builds, not during a scan), so it is asked once per name. A name the registry learns WHILE this
             * build runs used to be counted from that moment on and not before — an answer that depends on thread timing — and the memo
             * makes it one answer for the whole pass, which is the reproducible direction.
             */
            var oursByName = new Dictionary<string, bool>();

            foreach (var segment in CollectionsMarshal.AsSpan(_raidTotals.Ranges.TimeSegments))
            {
              var beginTime = segment.BeginTime;
              var endTime = segment.EndTime;

              if (!double.IsNaN(startTime))
              {
                if (startTime > endTime)
                {
                  continue;
                }

                if (startTime > beginTime)
                {
                  beginTime = startTime;
                }
              }

              if (!double.IsNaN(stopTime))
              {
                if (stopTime < beginTime)
                {
                  continue;
                }

                if (stopTime < endTime)
                {
                  endTime = stopTime;
                }
              }

              /*
               * Pass 1 keeps the records this segment accepts — as PAIRS. It used to wrap each one in its own `ActionGroup`, then wrap the
               * survivors in a SECOND `ActionGroup` one loop later: two objects and two lists per heal line for a group that is one record
               * wide, because the log writes one heal per line and per second gets many lines.
               *
               * The passes stay separate ON PURPOSE. A group-AE sighting late in a segment marks records EARLIER in the same second (same
               * healer and spell) as ignored, and pass 2 asks that question after all of pass 1 has answered it. Merging the two loops would
               * keep heals the current builder drops, so `kept` carries pass 1's order exactly.
               */
              List<(double Time, HealRecord Record)> kept = null;

              Dictionary<string, HashSet<string>> currentSpellCounts = null;
              Dictionary<double, Dictionary<string, HashSet<string>>> previousSpellCounts = null;
              Dictionary<string, byte> ignoreRecords = null;
              var currentTime = double.NaN;

              // The cursor sits on the first heal this segment could still want; advancing it is the whole cost of moving between
              // segments. A segment starting past the last heal leaves nothing to do, exactly as FindIndex returning -1 used to.
              while (cursor < allHeals.Count && allHeals[cursor].Item1 < beginTime) cursor++;

              for (var j = cursor; j < allHeals.Count && allHeals[j].Item1 <= endTime; j++)
              {
                var healTime = allHeals[j].Item1;
                var record = allHeals[j].Item2;

                if (tracksGroupAe)
                {
                  currentSpellCounts ??= [];
                  previousSpellCounts ??= [];
                  ignoreRecords ??= [];

                  if (currentSpellCounts.Count > 0)
                  {
                    previousSpellCounts[currentTime] = currentSpellCounts;
                  }

                  currentTime = healTime;
                  currentSpellCounts = [];

                  foreach (var timeKey in previousSpellCounts.Keys)
                  {
                    if (previousSpellCounts.ContainsKey(timeKey))
                    {
                      if (!double.IsNaN(currentTime) && (currentTime - timeKey) > 7)
                      {
                        previousSpellCounts.Remove(timeKey);
                      }
                    }
                  }
                }

                if (!CountedAsOurs(oursByName, record.Healed))
                {
                  continue;
                }

                // With the AE counting off, the short overload runs the same swarm-pet rule and writes nothing.
                var accepted = tracksGroupAe
                  ? healingValidator.IsValid(healTime, record, currentSpellCounts, previousSpellCounts, ignoreRecords)
                  : healingValidator.IsValid(healTime, record, ignoreRecords);

                if (accepted)
                {
                  (kept ??= []).Add((healTime, record));
                }
              }

              if (kept is null)
              {
                // Nothing in this segment counted: no groups, no time-segment maps, no merge.
                continue;
              }

              /*
               * One BLOCK per SECOND, not one per record. The old shape allocated an `ActionGroup` (plus the backing array its
               * `Actions` list needed on first Add) for every heal line: 2,647,774 of them on a whole-capture select-all over a night's
               * capture, all retained until the next build. Nothing downstream reads a block as one-record-wide — every consumer walks
               * segment → block → `block.BeginTime` filter → actions (`RecordGroupCollection` for the chart, this file's re-window pass,
               * the board's own rollup), so bundling records that share a second keeps the time each action is reported at, its order,
               * and the action stream itself identical. That is why the healing golden does not move by one byte: the `groups=` count it
               * freezes is the SEGMENT count, which this never touched.
               */
              var updatedHeals = new List<ActionGroup>();
              ActionGroup? openBlock = null;
              var healedByHealerTimeSegments = new Dictionary<string, Dictionary<string, TimeSegment>>();
              var healedBySpellTimeSegments = new Dictionary<string, Dictionary<string, TimeSegment>>();
              var healedByHealerSpellsTimeSegments = new Dictionary<string, Dictionary<string, TimeSegment>>();
              var healerHealedTimeSegments = new Dictionary<string, Dictionary<string, TimeSegment>>();
              var healerSpellTimeSegments = new Dictionary<string, Dictionary<string, TimeSegment>>();
              var healerHealedSpellTimeSegments = new Dictionary<string, Dictionary<string, TimeSegment>>();

              /*
               * Pass 2: turn the kept pairs into the groups the board reads, and file the time segments each pane asks for. Six maps per
               * record because six questions are asked downstream (healer->healed, healer->spell, healer+healed->spell, and the same three
               * transposed); the composite `healer|healed` key used to be concatenated TWICE per record — once is enough, and it is the only
               * string this pass needs beyond the record's own.
               */
              foreach (var (healTime, record) in kept)
              {
                if (ignoreRecords is not null &&
                  ignoreRecords.ContainsKey(healTime + "|" + record.Healer + "|" + record.SubType))
                {
                  continue;
                }

                // `kept` is time ascending (the cursor scan), so equal seconds are consecutive: one comparison decides the block.
                if (openBlock is null || openBlock.BeginTime != healTime)
                {
                  openBlock = new ActionGroup { BeginTime = healTime };
                  updatedHeals.Add(openBlock);
                }

                openBlock.Actions.Add(record);

                var spellNameKey = StatsUtil.CreateRecordKey(record.Type, record.SubType);
                var healerHealedKey = HealerHealedKey(record.Healer, record.Healed);

                // store substats and substats2 which is based on the player that was healed
                StatsUtil.UpdateTimeSegments(null, healedByHealerTimeSegments, record.Healer, record.Healed, healTime);
                StatsUtil.UpdateTimeSegments(null, healedBySpellTimeSegments, spellNameKey, record.Healed, healTime);
                StatsUtil.UpdateTimeSegments(null, healedByHealerSpellsTimeSegments, spellNameKey, healerHealedKey, healTime);
                StatsUtil.UpdateTimeSegments(null, healerHealedTimeSegments, record.Healed, record.Healer, healTime);
                StatsUtil.UpdateTimeSegments(null, healerSpellTimeSegments, spellNameKey, record.Healer, healTime);
                StatsUtil.UpdateTimeSegments(null, healerHealedSpellTimeSegments, spellNameKey, healerHealedKey, healTime);
              }

              /*
               * Merged sequentially. These were `Parallel.ForEach` — SIX dispatches per selection segment, ~26,700 of them over a farm
               * night whose selection holds 4,452 segments — to fan out dictionaries keyed by the healers active inside one second, i.e. a
               * handful of entries each. The dispatch cost what the work never paid back.
               */
              foreach (var kv in healedByHealerTimeSegments) StatsUtil.AddSubTimeEntry(_healedByHealerTimeRanges, kv);
              foreach (var kv in healedBySpellTimeSegments) StatsUtil.AddSubTimeEntry(_healedBySpellTimeRanges, kv);
              foreach (var kv in healedByHealerSpellsTimeSegments) StatsUtil.AddSubTimeEntry(_healedByHealerSpellTimeRanges, kv);
              foreach (var kv in healerHealedTimeSegments) StatsUtil.AddSubTimeEntry(_healerHealedTimeRanges, kv);
              foreach (var kv in healerSpellTimeSegments) StatsUtil.AddSubTimeEntry(_healerSpellTimeRanges, kv);
              foreach (var kv in healerHealedSpellTimeSegments) StatsUtil.AddSubTimeEntry(_healerHealedSpellTimeRanges, kv);

              if (updatedHeals.Count > 0)
              {
                _healingGroups.Add(updatedHeals);
              }
            }

            StatsBuildTrace.Stage(trace, "window");

            if (double.IsNaN(_raidTotals.MaxBeginTime) && double.IsNaN(_raidTotals.MinBeginTime))
            {
              // save for use by populate healing but only on initial build
              _allHealingGroups = _healingGroups;
              _allHealedByHealerTimeRanges = _healedByHealerTimeRanges;
              _allHealedBySpellTimeRanges = _healedBySpellTimeRanges;
              _allHealedByHealerSpellTimeRanges = _healedByHealerSpellTimeRanges;
            }

            ComputeHealingStats(options, trace);
          }
          else if (_selected == null || _selected.Count == 0)
          {
            // only clear if it's the initial or full load
            if (double.IsNaN(_raidTotals.MaxBeginTime) && double.IsNaN(_raidTotals.MinBeginTime))
            {
              _allHealingGroups = null;
              _allHealedByHealerTimeRanges = null;
              _allHealedBySpellTimeRanges = null;
              _allHealedByHealerSpellTimeRanges = null;
            }
            FireNoDataEvent(options, "NONPC");
          }
          else
          {
            // only clear if it's the initial or full load
            if (double.IsNaN(_raidTotals.MaxBeginTime) && double.IsNaN(_raidTotals.MinBeginTime))
            {
              _allHealingGroups = null;
              _allHealedByHealerTimeRanges = null;
              _allHealedBySpellTimeRanges = null;
              _allHealedByHealerSpellTimeRanges = null;
            }
            FireNoDataEvent(options, "NODATA");
          }
        }
        catch (Exception ex)
        {
          Log.Error(ex);
          if (StatsBuildTrace.FailFast)
            throw;   // tests: a swallowed builder throw is an empty board, not a failed run
        }
      }
    }

    internal bool PopulateHealing(CombinedStats combined)
    {
      lock (_lock)
      {
        var raidTotals = combined.RaidStats;
        var playerStats = combined.StatsList;
        var individualStats = new Dictionary<string, PlayerStats>();
        var totals = new Dictionary<string, long>();

        // shouldn't happen
        if (_allHealingGroups == null)
        {
          return false;
        }

        // clear out previous
        foreach (var stats in playerStats)
        {
          stats.Extra = 0;
          stats.MoreStats = null;
        }

        foreach (var group in CollectionsMarshal.AsSpan(_allHealingGroups))
        {
          foreach (var block in CollectionsMarshal.AsSpan(group))
          {
            if ((double.IsNaN(raidTotals.MinBeginTime) || block.BeginTime >= raidTotals.MinBeginTime)
              && (double.IsNaN(raidTotals.MaxBeginTime) || block.BeginTime <= raidTotals.MaxBeginTime))
            {
              foreach (var action in block.Actions)
              {
                if (action is HealRecord record)
                {
                  var stats = StatsUtil.CreatePlayerStats(individualStats, record.Healed);
                  StatsUtil.UpdateHealStats(stats, record);

                  var subStats2 = stats.SubStat2Of(record.Healer, record.Type);
                  StatsUtil.UpdateHealStats(subStats2, record);

                  var spellStatName = record.SubType ?? Labels.SelfHeal;
                  var spellStats = stats.SubStatOf(spellStatName, record.Type);
                  StatsUtil.UpdateHealStats(spellStats, record);

                  var subStats3 = subStats2.SubSubStatOf(spellStatName, HealerHealedKey(record.Healer, record.Healed));
                  StatsUtil.UpdateHealStats(subStats3, record);

                  long value = 0;
                  if (totals.TryGetValue(record.Healed, out var total))
                  {
                    value = total;
                  }

                  totals[record.Healed] = record.Total + value;
                }
              }
            }
          }
        }

        foreach (var stat in CollectionsMarshal.AsSpan(playerStats))
        {
          if (individualStats.TryGetValue(stat.Name, out var indStats))
          {
            if (totals.TryGetValue(stat.Name, out var total))
            {
              stat.Extra = total;
            }

            stat.MoreStats = indStats;
            UpdateStats(combined.RaidStats, indStats, _allHealedBySpellTimeRanges, _allHealedByHealerTimeRanges,
              _allHealedByHealerSpellTimeRanges, true, raidTotals.MinBeginTime, raidTotals.MaxBeginTime);

            foreach (var subStat in CollectionsMarshal.AsSpan(indStats.SubStats))
            {
              StatsUtil.UpdateCalculations(subStat, indStats);
            }

            foreach (var subStat2 in CollectionsMarshal.AsSpan(indStats.SubStats2))
            {
              StatsUtil.UpdateCalculations(subStat2, indStats);

              foreach (var subSubStats in CollectionsMarshal.AsSpan(subStat2.SubSubStats))
              {
                StatsUtil.UpdateCalculations(subSubStats, indStats);
              }
            }
          }
        }
      }

      return _isLimited;
    }

    private static void UpdateStats(PlayerStats raidTotals, PlayerStats stats,
      ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> healedBySpell,
      ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> healedByHealer,
      ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> healedByHealerSpells,
      bool populate,
      double minTime = double.NaN, double maxTime = double.NaN)
    {
      if (healedBySpell.TryGetValue(stats.Name, out var ranges))
      {
        // base the total time range off the sub times ranges since healing doesn't have good Fight segments to work with
        var totalRange = new TimeRange();
        foreach (var kv in ranges)
        {
          totalRange.Add(kv.Value.TimeSegments);
        }

        var filteredRange = StatsUtil.FilterTimeRange(totalRange, minTime, maxTime);
        stats.TotalSeconds = filteredRange.GetTotal();
      }

      StatsUtil.UpdateSubStatsTimeRanges(stats, healedBySpell, minTime, maxTime);
      StatsUtil.UpdateSubStatsTimeRanges(stats, healedByHealer, minTime, maxTime);

      foreach (var subStat2 in stats.SubStats2)
      {
        var subSubStatKey = populate ? subStat2.Key + "|" + stats.Name : stats.Name + "|" + subStat2.Key;
        if (healedByHealerSpells.TryGetValue(subSubStatKey, out ranges))
        {
          StatsUtil.UpdateSubStat(subStat2.SubSubStats, ranges, minTime, maxTime);
        }
      }

      StatsUtil.UpdateCalculations(stats, raidTotals);
    }

    internal void FireChartEvent(string action, List<PlayerStats> selected = null)
    {
      lock (_lock)
      {
        // send update
        var de = new DataPointEvent { Action = action, Iterator = new HealGroupCollection(_healingGroups) };

        if (selected != null)
        {
          de.Selected.AddRange(selected);
        }

        EventsUpdateDataPoint?.Invoke(de);
      }
    }

    private void FireNewStatsEvent()
    {
      // generating new stats
      EventsGenerationStatus?.Invoke(new StatsGenerationEvent { Type = Labels.HealParse, State = "STARTED" });
    }

    private void FireNoDataEvent(GenerateStatsOptions options, string state)
    {
      // nothing to do
      EventsGenerationStatus?.Invoke(new StatsGenerationEvent { Type = Labels.HealParse, State = state });
      FireChartEvent("CLEAR");
    }

    /*
     * The board's gate on who can be healed INTO a row: verified player, verified pet or mercenary, or a name that looks like a
     * person. Memoized per build for the reason named at the call site — see there. A null/empty name is asked directly rather than
     * cached, because there is nothing to key it under and the answer is always no anyway.
     */
    private static bool CountedAsOurs(Dictionary<string, bool> memo, string name)
    {
      if (string.IsNullOrEmpty(name)) return false;

      if (memo.TryGetValue(name, out var cached)) return cached;

      var ours = PlayerRegistry.Instance.IsPetOrPlayerOrMerc(name) || PlayerRegistry.IsPossiblePlayerName(name);
      memo[name] = ours;
      return ours;
    }

    private void ComputeHealingStats(GenerateStatsOptions options, in StatsBuildTrace.Handle trace)
    {
      lock (_lock)
      {
        _lastStatsEvent = null;
        if (_raidTotals != null)
        {
          var individualStats = new Dictionary<string, PlayerStats>();
          // always start over
          _raidTotals.Total = 0;
          var lastTime = double.NaN;

          try
          {
            foreach (var group in CollectionsMarshal.AsSpan(_healingGroups))
            {
              foreach (var block in CollectionsMarshal.AsSpan(group))
              {
                foreach (var action in block.Actions)
                {
                  if (action is HealRecord record)
                  {
                    _raidTotals.Total += record.Total;
                    var stats = StatsUtil.CreatePlayerStats(individualStats, record.Healer);
                    StatsUtil.UpdateHealStats(stats, record);

                    var spellStatName = record.SubType ?? Labels.SelfHeal;
                    var spellStats = stats.SubStatOf(spellStatName, record.Type);
                    StatsUtil.UpdateHealStats(spellStats, record);

                    var healedStatName = record.Healed;
                    var healedStats = stats.SubStat2Of(healedStatName, record.Type);
                    StatsUtil.UpdateHealStats(healedStats, record);

                    var subStats3 = healedStats.SubSubStatOf(spellStatName, HealerHealedKey(record.Healer, record.Healed));
                    StatsUtil.UpdateHealStats(subStats3, record);
                  }
                }

                lastTime = block.BeginTime;
              }
            }

            StatsBuildTrace.Stage(trace, "walk");

            _raidTotals.Dps = (long)Math.Round(_raidTotals.Total / _raidTotals.TotalSeconds, 2);
            StatsUtil.PopulateSpecials(_raidTotals);

            var uniqueClasses = new HashSet<string>();
            var playerClasses = new Dictionary<string, string>();
            foreach (var stats in individualStats.Values)
            {
              if (_raidTotals.Specials.TryGetValue(stats.OrigName, out var special2))
              {
                stats.Special = special2;
              }

              UpdateStats(_raidTotals, stats, _healerSpellTimeRanges, _healerHealedTimeRanges, _healerHealedSpellTimeRanges, false);
              var playerClass = PlayerRegistry.Instance.GetPlayerClass(stats.OrigName, lastTime);
              stats.ClassName = playerClass;
              playerClasses.TryAdd(stats.OrigName, playerClass);

              if (!string.IsNullOrEmpty(playerClass))
              {
                uniqueClasses.Add(playerClass);
              }
            }

            /*
             * Same diagnostic split as DamageStatsBuilder: the loop above touches EVERY name on the board (mobs included),
             * copying each name's time ranges and asking the registry for a class, while what follows is O(players) summary
             * arithmetic. They scale differently - 12k names on a group night cost ~55 ms here as well.
             */
            StatsBuildTrace.Stage(trace, "totals");

            var combined = new CombinedStats
            {
              RaidStats = _raidTotals,
              TargetTitle = (_selected.Count > 1 ? "Combined (" + _selected.Count + "): " : "") + _title,
              TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TimeFormat, _raidTotals.TotalSeconds),
              TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TotalFormat, StatsUtil.FormatTotals(_raidTotals.Total),
                " Heals ", StatsUtil.FormatTotals(_raidTotals.Dps)),
              PlayerClasses = playerClasses
            };

            combined.StatsList.AddRange(individualStats.Values);
            combined.StatsList.Sort(static (a, b) => b.Total.CompareTo(a.Total));
            combined.FullTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, combined.TotalTitle);
            combined.ShortTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle);
            combined.UniqueClasses.AddRange(uniqueClasses);
            combined.UniqueClasses.Sort();

            for (var i = 0; i < combined.StatsList.Count; i++)
            {
              combined.StatsList[i].Rank = Convert.ToUInt16(i + 1);
            }

            // generating new stats
            var genEvent = new StatsGenerationEvent
            {
              Type = Labels.HealParse,
              State = "COMPLETED",
              CombinedStats = combined,
              Limited = _isLimited
            };

            genEvent.Groups.AddRange(_healingGroups);
            EventsGenerationStatus?.Invoke(genEvent);
            _lastStatsEvent = genEvent;
            FireChartEvent("UPDATE");
            StatsBuildTrace.Stage(trace, "present");
          }
          catch (Exception ex)
          {
            Log.Error(ex);
            if (StatsBuildTrace.FailFast)
              throw;   // tests: a swallowed builder throw is an empty board, not a failed run
          }
        }
      }
    }

    /* The pair's "healer|healed" key, from the memo: exactly the string the concatenation produced, built once. */
    private string HealerHealedKey(string healer, string healed)
    {
      if (!_healerHealedKeys.TryGetValue((healer, healed), out var key)) _healerHealedKeys[(healer, healed)] = key = healer + "|" + healed;
      return key;
    }

    private void Reset(bool clear = false)
    {
      if (clear)
      {
        _allHealingGroups = null;
        _allHealedByHealerTimeRanges = null;
        _allHealedBySpellTimeRanges = null;
        _allHealedByHealerSpellTimeRanges = null;
      }

      _healedByHealerTimeRanges.Clear();
      _healedBySpellTimeRanges.Clear();
      _healedByHealerSpellTimeRanges.Clear();
      _healerHealedTimeRanges.Clear();
      _healerSpellTimeRanges.Clear();
      _healerHealedSpellTimeRanges.Clear();
      _healerHealedKeys.Clear();
      _healingGroups.Clear();
      _raidTotals = StatsUtil.CreatePlayerStats(Labels.RaidTotals);
      _selected = null;
      _title = "";
    }
  }
}
