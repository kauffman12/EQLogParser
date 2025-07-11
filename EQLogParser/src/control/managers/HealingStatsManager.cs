﻿using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal class HealingStatsManager
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal static HealingStatsManager Instance = new();
    internal event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal event Action<StatsGenerationEvent> EventsGenerationStatus;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _healedByHealerTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _healedBySpellTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _healerHealedTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _healerSpellTimeRanges = new();

    private readonly List<List<ActionGroup>> _healingGroups = [];
    private PlayerStats _raidTotals;
    private List<Fight> _selected;
    private TimeRange _allRanges;
    private string _title;
    private bool _isLimited;
    internal static readonly string[] Separator = [" @"];

    internal HealingStatsManager()
    {
      DataManager.Instance.EventsClearedActiveData += (_) =>
      {
        lock (_healingGroups)
        {
          Reset();
        }
      };
    }

    internal int GetGroupCount()
    {
      lock (_healingGroups)
      {
        return _healingGroups.Count;
      }
    }

    internal void RebuildTotalStats()
    {
      lock (_healingGroups)
      {
        if (_healingGroups.Count != 0)
        {
          var newOptions = new GenerateStatsOptions();
          newOptions.Npcs.AddRange(_selected);
          newOptions.AllRanges = _allRanges;
          BuildTotalStats(newOptions);
        }
      }
    }

    internal void BuildTotalStats(GenerateStatsOptions options)
    {
      lock (_healingGroups)
      {
        try
        {
          FireNewStatsEvent();
          Reset();

          _selected = [.. options.Npcs.OrderBy(sel => sel.Id)];
          _allRanges = options.AllRanges;
          _title = options.Npcs?.FirstOrDefault()?.Name;
          var healingValidator = new HealingValidator();
          _isLimited = healingValidator.IsHealingLimited();

          _raidTotals.Ranges.Add(_allRanges.TimeSegments);
          _raidTotals.AllRanges = _raidTotals.Ranges;

          if (_raidTotals.Ranges.TimeSegments.Count > 0)
          {
            var allHeals = RecordManager.Instance.GetAllHeals().ToList();
            // calculate totals first since it can modify the ranges
            _raidTotals.TotalSeconds = _raidTotals.MaxTime = _raidTotals.Ranges.GetTotal();

            foreach (var segment in CollectionsMarshal.AsSpan(_raidTotals.Ranges.TimeSegments))
            {
              var updatedHeals = new List<ActionGroup>();
              var healedByHealerTimeSegments = new Dictionary<string, Dictionary<string, TimeSegment>>();
              var healedBySpellTimeSegments = new Dictionary<string, Dictionary<string, TimeSegment>>();
              var healerHealedTimeSegments = new Dictionary<string, Dictionary<string, TimeSegment>>();
              var healerSpellTimeSegments = new Dictionary<string, Dictionary<string, TimeSegment>>();

              var currentTime = double.NaN;
              var currentSpellCounts = new Dictionary<string, HashSet<string>>();
              var previousSpellCounts = new Dictionary<double, Dictionary<string, HashSet<string>>>();
              var ignoreRecords = new Dictionary<string, byte>();
              var filtered = new List<ActionGroup>();

              var start = 1;
              if (start < allHeals.Count)
              {
                start = allHeals.FindIndex(start, special => special.Item1 >= segment.BeginTime);
                if (start > -1)
                {
                  for (var j = start; j < allHeals.Count; j++)
                  {
                    if (allHeals[j].Item1 >= segment.BeginTime && allHeals[j].Item1 <= segment.EndTime)
                    {
                      start = j;
                      // copy
                      var newBlock = new ActionGroup { BeginTime = allHeals[j].Item1 };
                      filtered.Add(newBlock);

                      if (currentSpellCounts.Count > 0)
                      {
                        previousSpellCounts[currentTime] = currentSpellCounts;
                      }

                      currentTime = allHeals[j].Item1;
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

                      if (PlayerManager.Instance.IsPetOrPlayerOrMerc(allHeals[j].Item2.Healed) ||
                        PlayerManager.IsPossiblePlayerName(allHeals[j].Item2.Healed))
                      {
                        if (healingValidator.IsValid(allHeals[j].Item1, allHeals[j].Item2, currentSpellCounts, previousSpellCounts, ignoreRecords))
                        {
                          newBlock.Actions.Add(allHeals[j].Item2);
                        }
                      }
                    }
                  }
                }
              }

              foreach (var heal in CollectionsMarshal.AsSpan(filtered))
              {
                var updatedHeal = new ActionGroup { BeginTime = heal.BeginTime };
                foreach (var action in heal.Actions.ToArray())
                {
                  if (action is HealRecord record)
                  {
                    var ignoreKey = heal.BeginTime + "|" + record.Healer + "|" + record.SubType;
                    if (!ignoreRecords.ContainsKey(ignoreKey))
                    {
                      updatedHeal.Actions.Add(record);
                      // store substats and substats2 which is based on the player that was healed
                      var key = StatsUtil.CreateRecordKey(record.Type, record.SubType);
                      StatsUtil.UpdateTimeSegments(null, healedByHealerTimeSegments, record.Healer, record.Healed, heal.BeginTime);
                      StatsUtil.UpdateTimeSegments(null, healedBySpellTimeSegments, key, record.Healed, heal.BeginTime);
                      StatsUtil.UpdateTimeSegments(null, healerHealedTimeSegments, record.Healed, record.Healer, heal.BeginTime);
                      StatsUtil.UpdateTimeSegments(null, healerSpellTimeSegments, key, record.Healer, heal.BeginTime);
                    }
                  }
                }

                if (updatedHeal.Actions.Count > 0)
                {
                  updatedHeals.Add(updatedHeal);
                }
              }

              Parallel.ForEach(healedByHealerTimeSegments, kv => StatsUtil.AddSubTimeEntry(_healedByHealerTimeRanges, kv));
              Parallel.ForEach(healedBySpellTimeSegments, kv => StatsUtil.AddSubTimeEntry(_healedBySpellTimeRanges, kv));
              Parallel.ForEach(healerHealedTimeSegments, kv => StatsUtil.AddSubTimeEntry(_healerHealedTimeRanges, kv));
              Parallel.ForEach(healerSpellTimeSegments, kv => StatsUtil.AddSubTimeEntry(_healerSpellTimeRanges, kv));

              if (updatedHeals.Count > 0)
              {
                _healingGroups.Add(updatedHeals);
              }
            }

            ComputeHealingStats();
          }
          else if (_selected == null || _selected.Count == 0)
          {
            FireNoDataEvent("NONPC");
          }
          else
          {
            FireNoDataEvent("NODATA");
          }
        }
        catch (Exception ex)
        {
          Log.Error(ex);
        }
      }
    }

    internal bool PopulateHealing(CombinedStats combined)
    {
      lock (_healingGroups)
      {
        var playerStats = combined.StatsList;
        var individualStats = new Dictionary<string, PlayerStats>();
        var totals = new Dictionary<string, long>();

        // clear out previous
        foreach (var stats in playerStats)
        {
          stats.Extra = 0;
          stats.MoreStats = null;
        }

        foreach (var group in CollectionsMarshal.AsSpan(_healingGroups))
        {
          foreach (var block in CollectionsMarshal.AsSpan(group))
          {
            foreach (var action in block.Actions.ToArray())
            {
              if (action is HealRecord record)
              {
                var stats = StatsUtil.CreatePlayerStats(individualStats, record.Healed);
                StatsUtil.UpdateStats(stats, record);

                var subStats2 = StatsUtil.CreatePlayerSubStats(stats.SubStats2, record.Healer, record.Type);
                StatsUtil.UpdateStats(subStats2, record);

                var spellStatName = record.SubType ?? Labels.SelfHeal;
                var spellStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, spellStatName, record.Type);
                StatsUtil.UpdateStats(spellStats, record);

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

        foreach (var stat in CollectionsMarshal.AsSpan(playerStats))
        {
          if (individualStats.TryGetValue(stat.Name, out var indStats))
          {
            if (totals.TryGetValue(stat.Name, out var total))
            {
              stat.Extra = total;
            }

            stat.MoreStats = indStats;
            UpdateStats(indStats, _healedBySpellTimeRanges, _healedByHealerTimeRanges);

            foreach (var subStat in CollectionsMarshal.AsSpan(indStats.SubStats))
            {
              StatsUtil.UpdateCalculations(subStat, indStats);
            }

            foreach (var subStat2 in CollectionsMarshal.AsSpan(indStats.SubStats2))
            {
              StatsUtil.UpdateCalculations(subStat2, indStats);
            }
          }
        }
      }

      return _isLimited;
    }

    private void UpdateStats(PlayerStats stats, ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> calc,
      ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> secondary)
    {
      if (calc.TryGetValue(stats.Name, out var ranges))
      {
        // base the total time range off the sub times ranges since healing doesn't have good Fight segments to work with
        var totalRange = new TimeRange();
        ranges.Values.ToList().ForEach(range => totalRange.Add(range.TimeSegments));
        stats.TotalSeconds = stats.MaxTime = totalRange.GetTotal();
      }

      StatsUtil.UpdateSubStatsTimeRanges(stats, calc);
      StatsUtil.UpdateSubStatsTimeRanges(stats, secondary);
      StatsUtil.UpdateCalculations(stats, _raidTotals);
    }

    internal void FireChartEvent(string action, List<PlayerStats> selected = null)
    {
      lock (_healingGroups)
      {
        // send update
        var de = new DataPointEvent { Action = action, Iterator = new HealGroupCollection(_healingGroups) };

        if (selected != null)
        {
          de.Selected.AddRange(selected);
        }

        EventsUpdateDataPoint?.Invoke(_healingGroups, de);
      }
    }

    private void FireNewStatsEvent()
    {
      // generating new stats
      EventsGenerationStatus?.Invoke(new StatsGenerationEvent { Type = Labels.HealParse, State = "STARTED" });
    }

    private void FireNoDataEvent(string state)
    {
      // nothing to do
      EventsGenerationStatus?.Invoke(new StatsGenerationEvent { Type = Labels.HealParse, State = state });
      FireChartEvent("CLEAR");
    }

    private void ComputeHealingStats()
    {
      lock (_healingGroups)
      {
        if (_raidTotals != null)
        {
          var individualStats = new Dictionary<string, PlayerStats>();

          // always start over
          _raidTotals.Total = 0;

          try
          {
            foreach (var group in CollectionsMarshal.AsSpan(_healingGroups))
            {
              foreach (var block in CollectionsMarshal.AsSpan(group))
              {
                foreach (var action in block.Actions.ToArray())
                {
                  if (action is HealRecord record)
                  {
                    _raidTotals.Total += record.Total;
                    var stats = StatsUtil.CreatePlayerStats(individualStats, record.Healer);
                    StatsUtil.UpdateStats(stats, record);

                    var spellStatName = record.SubType ?? Labels.SelfHeal;
                    var spellStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, spellStatName, record.Type);
                    StatsUtil.UpdateStats(spellStats, record);

                    var healedStatName = record.Healed;
                    var healedStats = StatsUtil.CreatePlayerSubStats(stats.SubStats2, healedStatName, record.Type);
                    StatsUtil.UpdateStats(healedStats, record);
                  }
                }
              }
            }

            _raidTotals.Dps = (long)Math.Round(_raidTotals.Total / _raidTotals.TotalSeconds, 2);
            StatsUtil.PopulateSpecials(_raidTotals);

            foreach (var kv in individualStats)
            {
              UpdateStats(kv.Value, _healerSpellTimeRanges, _healerHealedTimeRanges);
              if (_raidTotals.Specials.TryGetValue(kv.Value.OrigName, out var special2))
              {
                kv.Value.Special = special2;
              }
            }

            var combined = new CombinedStats
            {
              RaidStats = _raidTotals,
              TargetTitle = (_selected.Count > 1 ? "Combined (" + _selected.Count + "): " : "") + _title,
              TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TimeFormat, _raidTotals.TotalSeconds),
              TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TotalFormat, StatsUtil.FormatTotals(_raidTotals.Total), " Heals ", StatsUtil.FormatTotals(_raidTotals.Dps))
            };

            combined.StatsList.AddRange(individualStats.Values.OrderByDescending(item => item.Total));
            combined.FullTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, combined.TotalTitle);
            combined.ShortTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle);

            for (var i = 0; i < combined.StatsList.Count; i++)
            {
              combined.StatsList[i].Rank = Convert.ToUInt16(i + 1);
              combined.UniqueClasses[combined.StatsList[i].ClassName] = 1;
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
            FireChartEvent("UPDATE");
          }
          catch (Exception ex)
          {
            Log.Error(ex);
          }
        }
      }
    }

    private void Reset()
    {
      _healedByHealerTimeRanges.Clear();
      _healedBySpellTimeRanges.Clear();
      _healerHealedTimeRanges.Clear();
      _healerSpellTimeRanges.Clear();
      _healingGroups.Clear();
      _raidTotals = StatsUtil.CreatePlayerStats(Labels.RaidTotals);
      _selected = null;
      _title = "";
    }
  }
}