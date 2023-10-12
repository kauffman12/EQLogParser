
using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace EQLogParser
{
  class TankingStatsManager : ISummaryBuilder
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    internal static TankingStatsManager Instance = new();

    internal event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal event EventHandler<StatsGenerationEvent> EventsGenerationStatus;
    private readonly Dictionary<int, byte> TankingGroupIds = new();
    private readonly ConcurrentDictionary<string, TimeRange> PlayerTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> PlayerSubTimeRanges = new();
    private readonly List<List<ActionGroup>> TankingGroups = new();
    private PlayerStats RaidTotals;
    private List<Fight> Selected;
    private string Title;

    internal static bool IsMelee(DamageRecord record)
    {
      return record.Type == Labels.MELEE || record.Type == Labels.MISS || record.Type == Labels.PARRY || record.Type == Labels.DODGE ||
        record.Type == Labels.BLOCK || record.Type == Labels.INVULNERABLE || record.Type == Labels.RIPOSTE;
    }

    internal TankingStatsManager()
    {
      lock (TankingGroupIds)
      {
        DataManager.Instance.EventsClearedActiveData += (sender, e) =>
        {
          Reset();
        };
      }
    }

    internal int GetGroupCount()
    {
      lock (TankingGroupIds)
      {
        return TankingGroups.Count;
      }
    }

    internal void RebuildTotalStats(GenerateStatsOptions options)
    {
      lock (TankingGroupIds)
      {
        if (TankingGroups.Count > 0)
        {
          FireNewStatsEvent();
          ComputeTankingStats(options);
        }
      }
    }

    internal void BuildTotalStats(GenerateStatsOptions options)
    {
      lock (TankingGroupIds)
      {
        try
        {
          FireNewStatsEvent();
          Reset();

          Selected = options.Npcs.OrderBy(sel => sel.Id).ToList();
          Title = options.Npcs?.FirstOrDefault()?.Name;
          var damageBlocks = new List<ActionGroup>();

          Selected.ForEach(fight =>
          {
            damageBlocks.AddRange(fight.TankingBlocks);

            if (fight.GroupId > -1)
            {
              TankingGroupIds[fight.GroupId] = 1;
            }

            RaidTotals.Ranges.Add(new TimeSegment(fight.BeginTankingTime, fight.LastTankingTime));
            StatsUtil.UpdateRaidTimeRanges(fight.TankSegments, fight.TankSubSegments, PlayerTimeRanges, PlayerSubTimeRanges);
          });

          damageBlocks.Sort((a, b) => a.BeginTime.CompareTo(b.BeginTime));

          if (damageBlocks.Count > 0)
          {
            RaidTotals.TotalSeconds = RaidTotals.MaxTime = RaidTotals.Ranges.GetTotal();

            var rangeIndex = 0;
            double lastTime = 0;
            var newBlock = new List<ActionGroup>();
            damageBlocks.ForEach(block =>
            {
              if (RaidTotals.Ranges.TimeSegments.Count > rangeIndex && block.BeginTime > RaidTotals.Ranges.TimeSegments[rangeIndex].EndTime)
              {
                rangeIndex++;

                if (newBlock.Count > 0)
                {
                  TankingGroups.Add(newBlock);
                }

                newBlock = new List<ActionGroup>();
              }

              if (lastTime != block.BeginTime)
              {
                var copy = new ActionGroup();
                copy.Actions.AddRange(block.Actions);
                copy.BeginTime = block.BeginTime;
                newBlock.Add(copy);
              }
              else
              {
                newBlock.Last().Actions.AddRange(block.Actions);
              }
            });

            TankingGroups.Add(newBlock);
            ComputeTankingStats(options);
          }
          else if (Selected == null || Selected.Count == 0)
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
      lock (TankingGroupIds)
      {
        // send update
        var de = new DataPointEvent { Action = action, Iterator = new TankGroupCollection(TankingGroups, options.DamageType) };

        if (selected != null)
        {
          de.Selected.AddRange(selected);
        }

        EventsUpdateDataPoint?.Invoke(TankingGroups, de);
      }
    }

    private void FireNewStatsEvent()
    {
      // generating new stats
      EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent { Type = Labels.TANK_PARSE, State = "STARTED" });
    }

    private void FireNoDataEvent(GenerateStatsOptions options, string state)
    {
      // nothing to do
      EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent { Type = Labels.TANK_PARSE, State = state });
      FireChartEvent(options, "CLEAR");
    }

    private void ComputeTankingStats(GenerateStatsOptions options)
    {
      lock (TankingGroupIds)
      {
        var individualStats = new Dictionary<string, PlayerStats>();

        if (RaidTotals != null)
        {
          // always start over
          RaidTotals.Total = 0;

          try
          {
            TankingGroups.ForEach(group =>
            {
              group.ForEach(block =>
              {
                block.Actions.ForEach(action =>
                {
                  if (action is DamageRecord record)
                  {
                    if (options.DamageType == 0 || (options.DamageType == 1 && IsMelee(record)) || (options.DamageType == 2 && !IsMelee(record)))
                    {
                      RaidTotals.Total += record.Total;
                      var stats = StatsUtil.CreatePlayerStats(individualStats, record.Defender);
                      StatsUtil.UpdateStats(stats, record);
                      var subStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, record.SubType, record.Type);

                      var critHits = subStats.CritHits;
                      StatsUtil.UpdateStats(subStats, record);

                      // dont count misses/dodges or where no damage was done
                      if (record.Total > 0)
                      {
                        var values = subStats.CritHits > critHits ? subStats.CritFreqValues : subStats.NonCritFreqValues;
                        AddValue(values, record.Total, 1);
                      }
                    }
                  }
                });
              });
            });

            RaidTotals.DPS = (long)Math.Round(RaidTotals.Total / RaidTotals.TotalSeconds, 2);
            StatsUtil.PopulateSpecials(RaidTotals);
            Parallel.ForEach(individualStats.Values, stats =>
            {
              StatsUtil.UpdateAllStatsTimeRanges(stats, PlayerTimeRanges, PlayerSubTimeRanges);
              StatsUtil.UpdateCalculations(stats, RaidTotals);
              if (RaidTotals.Specials.TryGetValue(stats.OrigName, out var special2))
              {
                stats.Special = special2;
              }
            });

            var combined = new CombinedStats
            {
              RaidStats = RaidTotals,
              TargetTitle = (Selected.Count > 1 ? "Combined (" + Selected.Count + "): " : "") + Title,
              TimeTitle = string.Format(StatsUtil.TIME_FORMAT, RaidTotals.TotalSeconds),
              TotalTitle = string.Format(StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(RaidTotals.Total), " Tanked ", StatsUtil.FormatTotals(RaidTotals.DPS))
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
              Type = Labels.TANK_PARSE,
              State = "COMPLETED",
              CombinedStats = combined
            };

            genEvent.Groups.AddRange(TankingGroups);
            genEvent.UniqueGroupCount = TankingGroupIds.Count;
            EventsGenerationStatus?.Invoke(this, genEvent);
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
      PlayerTimeRanges.Clear();
      PlayerSubTimeRanges.Clear();
      TankingGroups.Clear();
      TankingGroupIds.Clear();
      RaidTotals = StatsUtil.CreatePlayerStats(Labels.RAID_TOTALS);
      Selected = null;
      Title = "";
    }

    public StatsSummary BuildSummary(string type, CombinedStats currentStats, List<PlayerStats> selected, bool _, bool showDPS, bool showTotals,
      bool rankPlayers, bool __, bool showTime, string customTitle)
    {
      var list = new List<string>();

      var title = "";
      var details = "";

      if (currentStats != null)
      {
        if (type == Labels.TANK_PARSE)
        {
          if (selected?.Count > 0)
          {
            foreach (var stats in selected.OrderByDescending(item => item.Total))
            {
              var playerFormat = rankPlayers ? string.Format(StatsUtil.PLAYER_RANK_FORMAT, stats.Rank, stats.Name) : string.Format(StatsUtil.PLAYER_FORMAT, stats.Name);
              var damageFormat = string.Format(StatsUtil.TOTAL_ONLY_FORMAT, StatsUtil.FormatTotals(stats.Total));
              list.Add(playerFormat + damageFormat);
            }
          }

          details = list.Count > 0 ? ", " + string.Join(" | ", list) : "";
          var timeTitle = showTime ? (" " + currentStats.TimeTitle) : "";
          var totals = showDPS ? currentStats.TotalTitle : currentStats.TotalTitle.Split(new[] { " @" }, 2, StringSplitOptions.RemoveEmptyEntries)[0];
          title = StatsUtil.FormatTitle(customTitle ?? currentStats.TargetTitle, timeTitle, showTotals ? totals : "");
        }
        else if (type == Labels.RECEIVED_HEAL_PARSE)
        {
          if (selected?.Count == 1 && selected[0].MoreStats != null)
          {
            var rank = 1;
            long totals = 0;
            foreach (var stats in selected[0].MoreStats.SubStats2.OrderByDescending(stats => stats.Total).Take(10))
            {
              var playerFormat = rankPlayers ? string.Format(StatsUtil.PLAYER_RANK_FORMAT, rank++, stats.Name) : string.Format(StatsUtil.PLAYER_FORMAT, stats.Name);
              var damageFormat = string.Format(StatsUtil.TOTAL_ONLY_FORMAT, StatsUtil.FormatTotals(stats.Total));
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
