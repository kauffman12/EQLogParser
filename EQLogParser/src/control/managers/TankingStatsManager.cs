
using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal class TankingStatsManager : ISummaryBuilder
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal static TankingStatsManager Instance = new();
    internal event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal event Action<StatsGenerationEvent> EventsGenerationStatus;
    private readonly Dictionary<int, byte> _tankingGroupIds = [];
    private readonly ConcurrentDictionary<string, TimeRange> _playerTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _playerSubTimeRanges = new();
    private readonly List<List<ActionGroup>> _tankingGroups = [];
    private PlayerStats _raidTotals;
    private List<Fight> _selected;
    private string _title;

    internal static bool IsMelee(DamageRecord record)
    {
      return record.Type is Labels.Melee or Labels.Miss or Labels.Parry or Labels.Dodge or Labels.Block or Labels.Invulnerable or Labels.Riposte;
    }

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

    internal int GetGroupCount()
    {
      lock (_tankingGroupIds)
      {
        return _tankingGroups.Count;
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
                newBlock.LastOrDefault()?.Actions.AddRange(block.Actions);
              }

              lastTime = block.BeginTime;
            }

            _tankingGroups.Add(newBlock);
            ComputeTankingStats(options);
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

    internal void FireChartEvent(GenerateStatsOptions options, string action, List<PlayerStats> selected = null)
    {
      lock (_tankingGroupIds)
      {
        // send update
        var de = new DataPointEvent { Action = action, Iterator = new TankGroupCollection(_tankingGroups, options.DamageType) };

        if (selected != null)
        {
          de.Selected.AddRange(selected);
        }

        EventsUpdateDataPoint?.Invoke(_tankingGroups, de);
      }
    }

    private void FireNewStatsEvent()
    {
      // generating new stats
      EventsGenerationStatus?.Invoke(new StatsGenerationEvent { Type = Labels.TankParse, State = "STARTED", Source = this });
    }

    private void FireNoDataEvent(GenerateStatsOptions options, string state)
    {
      // nothing to do
      EventsGenerationStatus?.Invoke(new StatsGenerationEvent { Type = Labels.TankParse, State = state, Source = this });
      FireChartEvent(options, "CLEAR");
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

          try
          {
            foreach (var group in CollectionsMarshal.AsSpan(_tankingGroups))
            {
              foreach (var block in CollectionsMarshal.AsSpan(group))
              {
                foreach (var action in block.Actions.ToArray())
                {
                  if (action is DamageRecord record)
                  {
                    if (options.DamageType == 0 || (options.DamageType == 1 && IsMelee(record)) || (options.DamageType == 2 && !IsMelee(record)))
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
              StatsUtil.UpdateAllStatsTimeRanges(stats, _playerTimeRanges, _playerSubTimeRanges);
              StatsUtil.UpdateCalculations(stats, _raidTotals);
              if (_raidTotals.Specials.TryGetValue(stats.OrigName, out var special2))
              {
                stats.Special = special2;
              }
            });

            var combined = new CombinedStats
            {
              RaidStats = _raidTotals,
              TargetTitle = (_selected.Count > 1 ? "Combined (" + _selected.Count + "): " : "") + _title,
              TimeTitle = string.Format(StatsUtil.TimeFormat, _raidTotals.TotalSeconds),
              TotalTitle = string.Format(StatsUtil.TotalFormat, StatsUtil.FormatTotals(_raidTotals.Total), " Tanked ", StatsUtil.FormatTotals(_raidTotals.Dps))
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
              CombinedStats = combined,
              Source = this
            };

            genEvent.Groups.AddRange(_tankingGroups);
            genEvent.UniqueGroupCount = _tankingGroupIds.Count;
            EventsGenerationStatus?.Invoke(genEvent);
            FireChartEvent(options, "UPDATE");
          }
          catch (Exception ex)
          {
            Log.Error(ex);
          }
        }
      }

      void AddValue(Dictionary<long, int> dict, long key, int amount)
      {
        if (!dict.TryAdd(key, amount))
        {
          dict[key] += amount;
        }
      }
    }

    private void Reset()
    {
      _playerTimeRanges.Clear();
      _playerSubTimeRanges.Clear();
      _tankingGroups.Clear();
      _tankingGroupIds.Clear();
      _raidTotals = StatsUtil.CreatePlayerStats(Labels.RaidTotals);
      _selected = null;
      _title = "";
    }

    public StatsSummary BuildSummary(string type, CombinedStats currentStats, List<PlayerStats> selected, bool _, bool showDps, bool showTotals,
      bool rankPlayers, bool __, bool showTime, string customTitle)
    {
      var title = "";
      var details = "";
      var list = new List<string>();
      if (currentStats != null)
      {
        if (type == Labels.TankParse)
        {
          if (selected?.Count > 0)
          {
            foreach (var stats in selected.OrderByDescending(item => item.Total))
            {
              var playerFormat = rankPlayers ? string.Format(StatsUtil.PlayerRankFormat, stats.Rank, stats.Name) : string.Format(StatsUtil.PlayerFormat, stats.Name);
              var damageFormat = string.Format(StatsUtil.TotalOnlyFormat, StatsUtil.FormatTotals(stats.Total));
              list.Add(playerFormat + damageFormat);
            }
          }

          details = list.Count > 0 ? ", " + string.Join(" | ", list) : "";
          var timeTitle = showTime ? (" " + currentStats.TimeTitle) : "";
          var totals = showDps ? currentStats.TotalTitle : currentStats.TotalTitle.Split([" @"], 2, StringSplitOptions.RemoveEmptyEntries)[0];
          title = StatsUtil.FormatTitle(customTitle ?? currentStats.TargetTitle, timeTitle, showTotals ? totals : "");
        }
        else if (type == Labels.ReceivedHealParse)
        {
          if (selected?.Count == 1 && selected[0].MoreStats != null)
          {
            var rank = 1;
            long totals = 0;
            foreach (var stats in selected[0].MoreStats.SubStats2.OrderByDescending(stats => stats.Total).Take(10))
            {
              var playerFormat = rankPlayers ? string.Format(StatsUtil.PlayerRankFormat, rank++, stats.Name) : string.Format(StatsUtil.PlayerFormat, stats.Name);
              var damageFormat = string.Format(StatsUtil.TotalOnlyFormat, StatsUtil.FormatTotals(stats.Total));
              list.Add(playerFormat + damageFormat);
              totals += stats.Total;
            }

            var totalTitle = showTotals ? (selected[0].Name + " Received " + StatsUtil.FormatTotals(totals) + " Healing") : (selected[0].Name + " Received Healing");
            details = list.Count > 0 ? ", " + string.Join(" | ", list) : "";
            var timeTitle = showTime ? currentStats.TimeTitle : "";
            title = StatsUtil.FormatTitle(customTitle ?? currentStats.TargetTitle, timeTitle, totalTitle);
          }
        }
      }

      return new StatsSummary { Title = title, RankedPlayers = details, };
    }
  }
}
