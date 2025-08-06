
using log4net;
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
  internal class TankingStatsManager
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal static TankingStatsManager Instance = new();
    internal event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal event Action<StatsGenerationEvent> EventsGenerationStatus;
    private readonly Dictionary<int, byte> _tankingGroupIds = [];
    private readonly ConcurrentDictionary<string, TimeRange> _playerTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _playerSubTimeRanges = new();
    private List<List<ActionGroup>> _allTankingGroups;
    private List<List<ActionGroup>> _tankingGroups = [];
    private PlayerStats _raidTotals;
    private List<Fight> _selected;
    private string _title;

    internal TankingStatsManager()
    {
      lock (_tankingGroupIds)
      {
        DataManager.Instance.EventsClearedActiveData += (_) =>
        {
          Reset();
        };
      }
    }

    internal void RebuildTotalStats(GenerateStatsOptions options)
    {
      lock (_tankingGroupIds)
      {
        if (_tankingGroups.Count > 0)
        {
          FireNewStatsEvent();
          ComputeTankingStats(options);
        }
      }
    }

    internal void BuildTotalStats(GenerateStatsOptions options)
    {
      lock (_tankingGroupIds)
      {
        try
        {
          FireNewStatsEvent();
          Reset();

          _selected = [.. options.Npcs.OrderBy(sel => sel.Id)];
          _title = options.Npcs?.FirstOrDefault()?.Name;
          var damageBlocks = new List<ActionGroup>();

          foreach (var fight in CollectionsMarshal.AsSpan(_selected))
          {
            damageBlocks.AddRange(fight.TankingBlocks);

            if (fight.GroupId > -1)
            {
              _tankingGroupIds[fight.GroupId] = 1;
            }

            _raidTotals.Ranges.Add(new TimeSegment(fight.BeginTankingTime, fight.LastTankingTime));
            _raidTotals.AllRanges.Add(options.AllRanges.TimeSegments);
            StatsUtil.UpdateRaidTimeRanges(fight.TankSegments, fight.TankSubSegments, _playerTimeRanges, _playerSubTimeRanges);
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
                  _tankingGroups.Add(newBlock);
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

              lastTime = block.BeginTime;
            }

            _tankingGroups.Add(newBlock);
            ComputeTankingStats(options);
          }
          else if (_selected == null || _selected.Count == 0)
          {
            FireNoDataEvent("NONPC", options.DamageType);
          }
          else
          {
            FireNoDataEvent("NODATA", options.DamageType);
          }
        }
        catch (Exception ex)
        {
          Log.Error(ex);
        }
      }
    }

    internal void FireChartEvent(string action, int damageType, List<PlayerStats> selected = null, bool reset = false)
    {
      lock (_tankingGroupIds)
      {
        // this happens when summary window is hidden so reset any time interval
        if (reset)
        {
          _tankingGroups = _allTankingGroups ?? [];
        }

        // send update
        var de = new DataPointEvent { Action = action, Iterator = new TankGroupCollection(_tankingGroups, damageType) };

        if (selected != null)
        {
          de.Selected.AddRange(selected);
        }

        EventsUpdateDataPoint?.Invoke(_tankingGroups, de);
      }
    }

    private void ComputeTankingStats(GenerateStatsOptions options)
    {
      lock (_tankingGroupIds)
      {
        var individualStats = new Dictionary<string, PlayerStats>();

        if (_raidTotals != null)
        {
          // always start over
          _raidTotals.Total = 0;
          _raidTotals.MaxBeginTime = double.NaN;
          _raidTotals.MinBeginTime = double.NaN;
          var startTime = double.NaN;
          var stopTime = double.NaN;

          try
          {
            // dont do max/min check to raid totals like damage stats manager so changing damage type will work correctly
            if ((options.MaxSeconds > -1 && options.MaxSeconds < _raidTotals.MaxTime) || (options.MinSeconds > 0 && options.MinSeconds < _raidTotals.MaxTime))
            {
              StatsUtil.UpdateMinMaxTimes(_raidTotals, options, out startTime, out stopTime);

              var filteredGroups = new List<List<ActionGroup>>();
              foreach (var group in _allTankingGroups)
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
              }

              _tankingGroups = filteredGroups;
              _raidTotals.TotalSeconds = options.MaxSeconds - options.MinSeconds;
              _raidTotals.MinTime = options.MinSeconds;
            }
            else
            {
              _tankingGroups = _allTankingGroups;
              _raidTotals.MinTime = 0;
              _raidTotals.TotalSeconds = _raidTotals.MaxTime;
            }

            foreach (var group in CollectionsMarshal.AsSpan(_tankingGroups))
            {
              foreach (var block in CollectionsMarshal.AsSpan(group))
              {
                foreach (var action in block.Actions.ToArray())
                {
                  if (action is DamageRecord record)
                  {
                    if (options.DamageType == 0 || (options.DamageType == 1 && StatsUtil.IsMelee(record)) || (options.DamageType == 2 && !StatsUtil.IsMelee(record)))
                    {
                      _raidTotals.Total += record.Total;
                      var stats = StatsUtil.CreatePlayerStats(individualStats, record.Defender);
                      StatsUtil.UpdateStats(stats, record);
                      var subStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, record.SubType, record.Type);

                      var critHits = subStats.CritHits;
                      StatsUtil.UpdateStats(subStats, record);

                      // don't count misses/dodges or where no damage was done
                      if (record.Total > 0)
                      {
                        var values = subStats.CritHits > critHits ? subStats.CritFreqValues : subStats.NonCritFreqValues;
                        AddValue(values, record.Total, 1);
                      }
                    }
                  }
                }
              }
            }

            _raidTotals.Dps = (long)Math.Round(_raidTotals.Total / _raidTotals.TotalSeconds, 2);
            StatsUtil.PopulateSpecials(_raidTotals);

            Parallel.ForEach(individualStats.Values, stats =>
            {
              // update before using _playerTimeRanges
              StatsUtil.UpdateAllStatsTimeRanges(stats, _playerTimeRanges, _playerSubTimeRanges, startTime, stopTime);
              StatsUtil.UpdateCalculations(stats, _raidTotals);

              if (_raidTotals.Specials.TryGetValue(stats.OrigName, out var special2))
              {
                stats.Special = special2;
              }

              var timeRange = new TimeRange();
              if (_playerTimeRanges.TryGetValue(stats.Name, out var range))
              {
                timeRange.Add(range.TimeSegments);
              }

              var filteredTimeRange = StatsUtil.FilterTimeRange(timeRange, startTime, stopTime);
              stats.TotalSeconds = filteredTimeRange.GetTotal();
            });

            var combined = new CombinedStats
            {
              RaidStats = _raidTotals,
              TargetTitle = (_selected.Count > 1 ? "Combined (" + _selected.Count + "): " : "") + _title,
              TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TimeFormat, _raidTotals.TotalSeconds),
              TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TotalFormat, StatsUtil.FormatTotals(_raidTotals.Total), " Tanked ", StatsUtil.FormatTotals(_raidTotals.Dps))
            };

            combined.StatsList.AddRange(individualStats.Values.AsParallel().OrderByDescending(item => item.Total));
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
              Type = Labels.TankParse,
              State = "COMPLETED",
              CombinedStats = combined
            };

            genEvent.Groups.AddRange(_tankingGroups);
            genEvent.UniqueGroupCount = _tankingGroupIds.Count;
            EventsGenerationStatus?.Invoke(genEvent);
            FireChartEvent("UPDATE", options.DamageType);
          }
          catch (Exception ex)
          {
            Log.Error(ex);
          }
        }
      }

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
      EventsGenerationStatus?.Invoke(new StatsGenerationEvent { Type = Labels.TankParse, State = "STARTED" });
    }

    private void FireNoDataEvent(string state, int damageType)
    {
      // nothing to do
      EventsGenerationStatus?.Invoke(new StatsGenerationEvent { Type = Labels.TankParse, State = state });
      FireChartEvent("CLEAR", damageType);
    }

    private void Reset()
    {
      _allTankingGroups = _tankingGroups;
      _tankingGroups.Clear();
      _tankingGroupIds.Clear();
      _raidTotals = StatsUtil.CreatePlayerStats(Labels.RaidTotals);
      _playerTimeRanges.Clear();
      _playerSubTimeRanges.Clear();
      _selected = null;
      _title = "";
    }
  }
}
