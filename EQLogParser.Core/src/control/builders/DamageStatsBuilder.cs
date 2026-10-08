using log4net;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EQLogParser
{
  internal class DamageStatsBuilder
  {
    internal static DamageStatsBuilder Instance = new();
    internal event Action<DataPointEvent> EventsUpdateDataPoint;
    internal event Action<StatsGenerationEvent> EventsGenerationStatus;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly object _lock = new();
    private readonly Dictionary<int, byte> _damageGroupIds = [];
    private readonly ConcurrentDictionary<string, int> _playerGroupAssignments = new();
    private readonly ConcurrentDictionary<string, TimeRange> _playerTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _playerSubTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _playerPets = new();
    private readonly ConcurrentDictionary<string, string> _petToPlayer = new();
    private List<List<ActionGroup>> _allDamageGroups;
    private List<List<ActionGroup>> _damageGroups = [];
    private PlayerStats _raidTotals;
    private List<Fight> _selected;
    private StatsGenerationEvent _lastStatsEvent;
    private string _title;

    /*
     * Only the process-wide board listens for "the log closed", and it signs up exactly once - here, not in the
     * instance constructor.
     *
     * The instance constructor used to subscribe, which was fine while this class had exactly one instance. It no
     * longer has: DerivedTotals builds a throwaway scope per calculation (the damage meter asks about once a second),
     * and a constructor subscription on a short-lived object is a permanent one - the static event holds the delegate,
     * the delegate holds the builder, and the builder holds its groups, per-player dictionaries and generated stats.
     * Measured: ten overlay calculations left twenty subscribers behind through a full GC, about 118 KB of retained
     * heap each, growing for as long as the meter stayed open (and making every later clear walk thousands of
     * lock-taking handlers). See DerivedTotalsTest.ACalculationLeavesNothingWatchingTheClearSignal.
     *
     * A scoped builder has nothing to be cleared: nobody keeps its result, and the surface painting it blanks itself
     * when the session ends. The singleton's handler is kept verbatim - reset the boards, and drop group assignments
     * only when the server of the opened log changed.
     */
    static DamageStatsBuilder()
    {
      Instance.ClearedByActiveData();
    }

    private void ClearedByActiveData()
    {
      CombatEvents.ActiveDataCleared += (bool serverChanged) =>
      {
        lock (_lock)
        {
          Reset();

          if (serverChanged)
          {
            _playerGroupAssignments.Clear();
          }
        }
      };
    }

    internal StatsGenerationEvent GetLastStats()
    {
      lock (_lock)
      {
        return _lastStatsEvent;
      }
    }

    internal void SetPlayerAssignedGroup(string playerName, int assignedGroup)
    {
      if (string.IsNullOrEmpty(playerName))
        return;

      if (assignedGroup >= 0 && assignedGroup <= 12)
      {
        _playerGroupAssignments[playerName] = assignedGroup;
      }
      else
      {
        _playerGroupAssignments.TryRemove(playerName, out _);
      }
    }

    /*
     * Traced wrapper: every build of this board gets a line naming its door and its cost (see StatsBuildTrace). The body below is
     * unchanged apart from the name — re-slicing the groups already in hand, which is what the panes' own dials ask for.
     */
    internal void RebuildTotalStats(GenerateStatsOptions options, bool reset = false)
    {
      var trace = StatsBuildTrace.Begin("damage", options.Source, "re-slice");
      try
      {
        RebuildTotalStatsCore(options, reset, trace);
      }
      finally
      {
        StatsBuildTrace.End(trace, $"window {options.MinSeconds}..{options.MaxSeconds} reset={reset}");
      }
    }

    private void RebuildTotalStatsCore(GenerateStatsOptions options, bool reset, in StatsBuildTrace.Handle trace)
    {
      var built = false;

      if (EventsGenerationStatus?.GetInvocationList().Length > 0)
      {
        lock (_lock)
        {
          if (reset)
          {
            _damageGroups = _allDamageGroups ?? [];
          }

          if (_damageGroups.Count > 0)
          {
            FireNewStatsEvent();
            ComputeDamageStats(options, trace);
            built = true;
          }
        }
      }

      // Outside the lock: the tidy waits before collecting, and it must not be holding the builder while it does.
      if (built)
      {
        GcTidyUp.Request("stats built");
      }
    }

    // Traced wrapper (see StatsBuildTrace): the door label comes in on the options, the cost and the record count go out on the line.
    internal void BuildTotalStats(GenerateStatsOptions options)
    {
      var trace = StatsBuildTrace.Begin("damage", options.Source);
      try
      {
        BuildTotalStatsCore(options, trace);
      }
      finally
      {
        StatsBuildTrace.End(trace, $"npcs={options.Npcs.Count} window {options.MinSeconds}..{options.MaxSeconds}");
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
          _title = options?.Npcs.Count > 0 ? options.Npcs[0].Name : null;

          var damageBlocks = new List<ActionGroup>();

          foreach (var fight in CollectionsMarshal.AsSpan(_selected))
          {
            damageBlocks.AddRange(fight.DamageBlocks);

            if (fight.GroupId > -1)
            {
              _damageGroupIds[fight.GroupId] = 1;
            }

            _raidTotals.Ranges.Add(new TimeSegment(fight.BeginDamageTime, fight.LastDamageTime));
            _raidTotals.AllRanges.Add(options.AllRanges.TimeSegments);
            StatsUtil.UpdateRaidTimeRanges(fight.DamageSegments, fight.DamageSubSegments, _playerTimeRanges, _playerSubTimeRanges);
          }

          damageBlocks.Sort((a, b) => a.BeginTime.CompareTo(b.BeginTime));

          if (damageBlocks.Count != 0)
          {
            _raidTotals.TotalSeconds = _raidTotals.MaxTime = _raidTotals.Ranges.GetTotal();

            var rangeIndex = 0;
            double lastTime = 0;
            var newBlock = new List<ActionGroup>();
            foreach (var block in CollectionsMarshal.AsSpan(damageBlocks))
            {
              if (_raidTotals.Ranges.TimeSegments.Count > rangeIndex && block.BeginTime > _raidTotals.Ranges.TimeSegments[rangeIndex].EndTime)
              {
                rangeIndex++;
                if (newBlock.Count > 0)
                {
                  _damageGroups.Add(newBlock);
                }

                newBlock = [];
              }

              if (!lastTime.Equals(block.BeginTime))
              {
                var copy = new ActionGroup();
                copy.Actions.AddRange(block.Actions);
                copy.BeginTime = block.BeginTime;
                newBlock.Add(copy);
              }
              else
              {
                newBlock.LastOrDefault()?.Actions?.AddRange(block.Actions);
              }

              // update pet mapping
              foreach (var action in block.Actions.OfType<DamageRecord>())
              {
                UpdatePetMapping(action);
              }
              lastTime = block.BeginTime;
            }

            _damageGroups.Add(newBlock);

            // The regrouping above copied every block of the selection; the walk below counts them. Named apart because
            // a measure/present restructure spends these two very differently (docs/DesignNotes.md -> "The damage board's golden").
            StatsBuildTrace.Stage(trace, "groups");
            ComputeDamageStats(options, trace);
          }
          else if (_selected == null || _selected.Count == 0)
          {
            FireNoDataEvent(options, "NONPC");
          }
          else
          {
            FireNoDataEvent(options, "NODATA");
          }
        }
        catch (Exception ex)
        {
          Log.Error(ex);
        }
      }
    }

    internal void FireChartEvent(string action, List<PlayerStats> selected = null, List<GroupEntry> selectedGroups = null)
    {
      lock (_lock)
      {
        // send update
        var de = new DataPointEvent { Action = action, Iterator = new DamageGroupCollection(_damageGroups) };

        if (selected is not null)
        {
          de.Selected.AddRange(selected);
        }

        if (selectedGroups is not null)
        {
          de.SelectedGroups.AddRange(selectedGroups);
        }

        EventsUpdateDataPoint?.Invoke(de);
      }
    }

    private void ComputeDamageStats(GenerateStatsOptions options, in StatsBuildTrace.Handle trace)
    {
      lock (_lock)
      {
        _lastStatsEvent = null;
        if (_raidTotals is not null)
        {
          var childrenStats = new Dictionary<string, Dictionary<string, PlayerStats>>();
          var topLevelStats = new Dictionary<string, PlayerStats>();
          var damageValidator = new DamageValidator(
            AppSettings.IsAssassinateDamageEnabled, AppSettings.IsBaneDamageEnabled, AppSettings.IsDamageShieldDamageEnabled,
            AppSettings.IsFinishingBlowDamageEnabled, AppSettings.IsHeadshotDamageEnabled, AppSettings.IsSlayUndeadDamageEnabled);
          var individualStats = new Dictionary<string, PlayerStats>();

          // always start over
          _raidTotals.Total = 0;
          _raidTotals.MaxBeginTime = double.NaN;
          _raidTotals.MinBeginTime = double.NaN;
          var startTime = double.NaN;
          var stopTime = double.NaN;

          try
          {
            if ((options.MaxSeconds > -1 && options.MaxSeconds < _raidTotals.MaxTime && !options.MaxSeconds.Equals((long)_raidTotals.TotalSeconds)) ||
              (options.MinSeconds > 0 && options.MinSeconds < _raidTotals.MaxTime))
            {
              StatsUtil.UpdateMinMaxTimes(_raidTotals, options, out startTime, out stopTime);

              var filteredGroups = new List<List<ActionGroup>>();
              _allDamageGroups.ForEach(group =>
              {
                var filteredBlocks = new List<ActionGroup>();
                group.ForEach(block =>
                {
                  if ((double.IsNaN(startTime) || block.BeginTime >= startTime) && (double.IsNaN(stopTime) || block.BeginTime <= stopTime))
                  {
                    filteredBlocks.Add(block);
                  }
                });

                if (filteredBlocks.Count > 0)
                {
                  filteredGroups.Add(filteredBlocks);
                }
              });

              _damageGroups = filteredGroups;
              _raidTotals.TotalSeconds = options.MaxSeconds - options.MinSeconds;
              _raidTotals.MinTime = options.MinSeconds;
            }
            else
            {
              _damageGroups = _allDamageGroups;
              _raidTotals.MinTime = 0;
              _raidTotals.TotalSeconds = _raidTotals.MaxTime;
            }

            StatsBuildTrace.Stage(trace, "window");

            var lastTime = double.NaN;
            var prevPlayerTimes = new Dictionary<string, double>();
            foreach (var group in CollectionsMarshal.AsSpan(_damageGroups))
            {
              foreach (var block in CollectionsMarshal.AsSpan(group))
              {
                foreach (var action in block.Actions)
                {
                  if (action is DamageRecord record)
                  {
                    var isValid = damageValidator.IsValid(record);
                    var stats = StatsUtil.CreatePlayerStats(individualStats, record.Attacker);

                    if (record.Type == Labels.Bane && !isValid)
                    {
                      stats.BaneHits++;

                      if (individualStats.TryGetValue(stats.OrigName + " +Pets", out var temp))
                      {
                        temp.BaneHits++;
                      }
                    }
                    else if (isValid)
                    {
                      var isAttackerPet = IdentityLookup.IsPet(record.Attacker);
                      var isNewFrame = StatsUtil.CheckNewFrame(prevPlayerTimes, stats.Name, block.BeginTime);

                      _raidTotals.Total += record.Total;
                      StatsUtil.UpdateDamageStats(stats, record, isNewFrame, isAttackerPet);

                      /*
                       * Whose pet is this? The record's own answer first. A derived record carries the owner this LINE said,
                       * or the owner of the charm window this fact fell inside (FightSummarySource.OwnerOf), and a name can be
                       * possessed twice in one selection — a mob charmed by one raider, unfriended, recharmed by another — where
                       * an untimed lookup answers with the LAST owner for every record of that name. Measured before this line
                       * existed: 100 damage for Firstowner and 200 for Secondowner came out as a single "Secondowner +Pets = 300",
                       * a number nobody did.
                       *
                       * The per-name map stays for records the capture never attributed (stored records from a previous session,
                       * whose AttackerOwner is empty) — it is the fallback, not the authority.
                       */
                      var player = !string.IsNullOrEmpty(record.AttackerOwner) ? record.AttackerOwner
                        : _petToPlayer.TryGetValue(record.Attacker, out var mapped) ? mapped : null;

                      if ((player is null && !_playerPets.ContainsKey(record.Attacker)) || player == Labels.Unassigned)
                      {
                        topLevelStats[record.Attacker] = stats;
                        stats.IsTopLevel = true;
                      }
                      else
                      {
                        var origName = player ?? record.Attacker;
                        var aggregateName = origName + " +Pets";
                        isNewFrame = StatsUtil.CheckNewFrame(prevPlayerTimes, aggregateName, block.BeginTime);

                        var aggregatePlayerStats = StatsUtil.CreatePlayerStats(individualStats, aggregateName, origName);
                        StatsUtil.UpdateDamageStats(aggregatePlayerStats, record, isNewFrame, isAttackerPet);
                        topLevelStats[aggregateName] = aggregatePlayerStats;

                        if (childrenStats.TryGetValue(aggregateName, out var children))
                        {
                          children.TryAdd(stats.Name, stats);
                        }
                        else
                        {
                          childrenStats[aggregateName] = new Dictionary<string, PlayerStats> { { stats.Name, stats } };
                        }

                        stats.IsTopLevel = false;
                      }

                      var subStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, record.SubType, record.Type);
                      var critHits = subStats.CritHits;
                      StatsUtil.UpdateDamageStats(subStats, record, false, isAttackerPet);

                      // don't count misses/dodges or where no damage was done
                      if (record.Total > 0)
                      {
                        var values = subStats.CritHits > critHits ? subStats.CritFreqValues : subStats.NonCritFreqValues;
                        AddValue(values, record.Total, 1);
                      }
                    }
                  }
                }

                lastTime = block.BeginTime;
              }
            }

            StatsBuildTrace.Stage(trace, "walk");

            _raidTotals.Dps = (long)Math.Round(_raidTotals.Total / _raidTotals.TotalSeconds, 2);
            StatsUtil.PopulateSpecials(_raidTotals, true);

            var expandedStats = new List<PlayerStats>();
            var uniqueClasses = new HashSet<string>();
            var playerClasses = new Dictionary<string, string>();
            foreach (var stats in individualStats.Values)
            {
              if (_playerGroupAssignments.TryGetValue(stats.OrigName, out var cachedGroup))
              {
                stats.AssignedGroup = cachedGroup;
              }

              if (topLevelStats.ContainsKey(stats.Name))
              {
                if (childrenStats.TryGetValue(stats.Name, out var children))
                {
                  var timeRange = new TimeRange();
                  foreach (var child in children.Values)
                  {
                    // update before using _playerTimeRanges
                    StatsUtil.UpdateAllStatsTimeRanges(child, _playerTimeRanges, _playerSubTimeRanges, startTime, stopTime);

                    if (_playerTimeRanges.TryGetValue(child.Name, out var range))
                    {
                      timeRange.Add(range.TimeSegments);
                      // Store time segments on player for Group View and other view modes
                      child.Ranges = new TimeRange(range.TimeSegments);
                    }

                    expandedStats.Add(child);
                    _raidTotals.ResistCounts.TryGetValue(child.Name, out var childResists);

                    StatsUtil.UpdateCalculations(child, _raidTotals, childResists);

                    if (stats.Total > 0)
                    {
                      child.Percent = (float)Math.Round(Convert.ToDouble(child.Total) / stats.Total * 100, 2);
                    }

                    if (_raidTotals.Specials.TryGetValue(child.Name, out var special1))
                    {
                      child.Special = special1;
                    }
                  }

                  var filteredTimeRange = StatsUtil.FilterTimeRange(timeRange, startTime, stopTime);
                  stats.TotalSeconds = filteredTimeRange.GetTotal();
                }
                else
                {
                  expandedStats.Add(stats);
                  StatsUtil.UpdateAllStatsTimeRanges(stats, _playerTimeRanges, _playerSubTimeRanges, startTime, stopTime);
                  // Store time segments on player for Group View and other view modes
                  if (_playerTimeRanges.TryGetValue(stats.Name, out var range))
                  {
                    stats.Ranges = new TimeRange(range.TimeSegments);
                  }
                }

                _raidTotals.ResistCounts.TryGetValue(stats.Name, out var resists);
                StatsUtil.UpdateCalculations(stats, _raidTotals, resists);

                if (_raidTotals.Specials.TryGetValue(stats.OrigName, out var special2))
                {
                  stats.Special = special2;
                }
              }

              var playerClass = PlayerRegistry.Instance.GetPlayerClass(stats.OrigName, lastTime);
              stats.ClassName = playerClass;
              playerClasses.TryAdd(stats.OrigName, playerClass);

              if (!string.IsNullOrEmpty(playerClass))
              {
                uniqueClasses.Add(playerClass);
              }
            }

            var combined = new CombinedStats
            {
              RaidStats = _raidTotals,
              TargetTitle = (_selected.Count > 1 ? "Combined (" + _selected.Count + "): " : "") + _title,
              TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TimeFormat, _raidTotals.TotalSeconds),
              TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TotalFormat, StatsUtil.FormatTotals(_raidTotals.Total),
                " Damage ", StatsUtil.FormatTotals(_raidTotals.Dps)),
              PlayerClasses = playerClasses
            };

            combined.StatsList.AddRange(topLevelStats.Values);
            combined.StatsList.Sort(static (a, b) => b.Total.CompareTo(a.Total));
            combined.FullTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, combined.TotalTitle);
            combined.ShortTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle);
            combined.ExpandedStatsList.AddRange(expandedStats);
            combined.ExpandedStatsList.Sort(static (a, b) => b.Total.CompareTo(a.Total));
            combined.UniqueClasses.AddRange(uniqueClasses);
            combined.UniqueClasses.Sort(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < combined.ExpandedStatsList.Count; i++)
            {
              combined.ExpandedStatsList[i].Rank = Convert.ToUInt16(i + 1);
              if (combined.StatsList.Count > i)
              {
                var stats = combined.StatsList[i];
                stats.Rank = Convert.ToUInt16(i + 1);

                if (childrenStats.TryGetValue(stats.Name, out var children))
                {
                  var sortedChildren = new List<PlayerStats>(children.Values);
                  sortedChildren.Sort(static (a, b) => b.Total.CompareTo(a.Total));
                  combined.Children.Add(stats.Name, sortedChildren);
                }
              }
            }

            // Everything from here down is arithmetic and lists over what survived the walk: O(names on the board), not
            // O(records). It is the half a "one row changed" refresh would run on its own.
            StatsBuildTrace.Stage(trace, "present");

            // generating new stats
            var genEvent = new StatsGenerationEvent
            {
              Type = Labels.DamageParse,
              State = "COMPLETED",
              CombinedStats = combined,
              Limited = damageValidator.IsDamageLimited()
            };

            genEvent.Groups.AddRange(_damageGroups);
            genEvent.UniqueGroupCount = _damageGroupIds.Count;
            EventsGenerationStatus?.Invoke(genEvent);
            _lastStatsEvent = genEvent;
            FireChartEvent("UPDATE");
          }
          catch (Exception ex)
          {
            Log.Error(ex);
          }
        }
      }

      return;

      static void AddValue(Dictionary<long, int> dict, long key, int amount)
      {
        if (!dict.TryAdd(key, amount))
        {
          dict[key] += amount;
        }
      }
    }

    private void FireNewStatsEvent()
    {
      // generating new stats
      EventsGenerationStatus?.Invoke(new StatsGenerationEvent { Type = Labels.DamageParse, State = "STARTED" });
    }

    private void FireNoDataEvent(GenerateStatsOptions options, string state)
    {
      // nothing to do
      EventsGenerationStatus?.Invoke(new StatsGenerationEvent { Type = Labels.DamageParse, State = state });
      FireChartEvent("CLEAR");
    }

    private void Reset()
    {
      _allDamageGroups = _damageGroups;
      _damageGroups.Clear();
      _damageGroupIds.Clear();
      _raidTotals = StatsUtil.CreatePlayerStats(Labels.RaidTotals);
      _playerPets.Clear();
      _petToPlayer.Clear();
      _playerTimeRanges.Clear();
      _playerSubTimeRanges.Clear();
      _selected = null;
      _title = "";
    }

    private void UpdatePetMapping(DamageRecord damage)
    {
      // The owner the record itself carries wins; the untimed lookup is the fallback for records that carry none (see the
      // fold in ComputeDamageStats, which does the same and is where an untimed answer would move damage to the wrong person).
      var petName = !string.IsNullOrEmpty(damage.AttackerOwner) ? damage.AttackerOwner : IdentityLookup.OwnerOf(damage.Attacker);
      if ((!string.IsNullOrEmpty(petName) && petName != Labels.Unassigned))
      {
        if (!_playerPets.TryGetValue(petName, out var mapping))
        {
          mapping = new ConcurrentDictionary<string, byte>();
          _playerPets[petName] = mapping;
        }

        mapping[damage.Attacker] = 1;
        _petToPlayer[damage.Attacker] = petName;
      }
    }
  }
}

