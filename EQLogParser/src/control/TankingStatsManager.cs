
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  class TankingStatsManager : ISummaryBuilder
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static TankingStatsManager Instance = new TankingStatsManager();

    internal event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal event EventHandler<StatsGenerationEvent> EventsGenerationStatus;
    private readonly Dictionary<int, byte> TankingGroupIds = new Dictionary<int, byte>();
    private readonly ConcurrentDictionary<string, TimeRange> PlayerTimeRanges = new ConcurrentDictionary<string, TimeRange>();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> PlayerSubTimeRanges = new ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>>();
    private readonly List<List<ActionBlock>> TankingGroups = new List<List<ActionBlock>>();
    private PlayerStats RaidTotals;
    private List<Fight> Selected;
    private string Title;

    internal static bool IsMelee(DamageRecord record)
    {
      return record.Type == Labels.MELEE || record.Type == Labels.MISS || record.Type == Labels.PARRY || record.Type == Labels.DODGE || record.Type == Labels.BLOCK;
    }

    internal TankingStatsManager()
    {
      lock (TankingGroupIds)
      {
        DataManager.Instance.EventsClearedActiveData += (object sender, bool e) =>
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
          FireNewStatsEvent(options);
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
          FireNewStatsEvent(options);
          Reset();

          Selected = options.Npcs.OrderBy(sel => sel.Id).ToList();
          Title = options.Name;
          var damageBlocks = new List<ActionBlock>();

          Selected.ForEach(fight =>
          {
            damageBlocks.AddRange(fight.TankingBlocks);

            if (fight.GroupId > -1)
            {
              TankingGroupIds[fight.GroupId] = 1;
            }

            RaidTotals.Ranges.Add(new TimeSegment(fight.BeginTankingTime, fight.LastTankingTime));
            StatsUtil.UpdateRaidTimeRanges(fight, PlayerTimeRanges, PlayerSubTimeRanges, true);
          });

          damageBlocks.Sort((a, b) => a.BeginTime.CompareTo(b.BeginTime));

          if (damageBlocks.Count > 0)
          {
            RaidTotals.TotalSeconds = RaidTotals.MaxTime = RaidTotals.Ranges.GetTotal();

            int rangeIndex = 0;
            double lastTime = 0;
            var newBlock = new List<ActionBlock>();
            damageBlocks.ForEach(block =>
            {
              if (RaidTotals.Ranges.TimeSegments.Count > rangeIndex && block.BeginTime > RaidTotals.Ranges.TimeSegments[rangeIndex].EndTime)
              {
                rangeIndex++;

                if (newBlock.Count > 0)
                {
                  TankingGroups.Add(newBlock);
                }

                newBlock = new List<ActionBlock>();
              }

              if (lastTime != block.BeginTime)
              {
                var copy = new ActionBlock();
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
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
          if (ex is ArgumentNullException || ex is NullReferenceException || ex is ArgumentOutOfRangeException || ex is ArgumentException || ex is OutOfMemoryException)
          {
            LOG.Error(ex);
          }
        }
      }
    }

    internal void FireChartEvent(GenerateStatsOptions options, string action, List<PlayerStats> selected = null, Predicate<object> filter = null)
    {
      lock (TankingGroupIds)
      {
        if (options.RequestChartData)
        {
          // send update
          DataPointEvent de = new DataPointEvent() { Action = action, Iterator = new TankGroupCollection(TankingGroups, options.DamageType), Filter = filter };

          if (selected != null)
          {
            de.Selected.AddRange(selected);
          }

          EventsUpdateDataPoint?.Invoke(TankingGroups, de);
        }
      }
    }

    private void FireNewStatsEvent(GenerateStatsOptions options)
    {
      if (options.RequestSummaryData)
      {
        // generating new stats
        EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent() { Type = Labels.TANKPARSE, State = "STARTED" });
      }
    }

    private void FireNoDataEvent(GenerateStatsOptions options, string state)
    {
      if (options.RequestSummaryData)
      {
        // nothing to do
        EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent() { Type = Labels.TANKPARSE, State = state });
      }

      FireChartEvent(options, "CLEAR");
    }

    private void ComputeTankingStats(GenerateStatsOptions options)
    {
      lock (TankingGroupIds)
      {
        CombinedStats combined = null;
        Dictionary<string, PlayerStats> individualStats = new Dictionary<string, PlayerStats>();

        if (RaidTotals != null)
        {
          // always start over
          RaidTotals.Total = 0;

          try
          {
            FireChartEvent(options, "UPDATE");

            if (options.RequestSummaryData)
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
                        PlayerStats stats = StatsUtil.CreatePlayerStats(individualStats, record.Defender);
                        StatsUtil.UpdateStats(stats, record);
                        PlayerSubStats subStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, record.SubType, record.Type);
                        StatsUtil.UpdateStats(subStats, record);
                      }
                    }
                  });
                });
              });

              RaidTotals.DPS = (long)Math.Round(RaidTotals.Total / RaidTotals.TotalSeconds, 2);
              Parallel.ForEach(individualStats.Values, stats =>
              {
                StatsUtil.UpdateAllStatsTimeRanges(stats, PlayerTimeRanges, PlayerSubTimeRanges);
                StatsUtil.UpdateCalculations(stats, RaidTotals);
              });

              combined = new CombinedStats
              {
                RaidStats = RaidTotals,
                TargetTitle = (Selected.Count > 1 ? "Combined (" + Selected.Count + "): " : "") + Title,
                TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TIME_FORMAT, RaidTotals.TotalSeconds),
                TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(RaidTotals.Total), " Tanked ", StatsUtil.FormatTotals(RaidTotals.DPS))
              };

              combined.StatsList.AddRange(individualStats.Values.AsParallel().OrderByDescending(item => item.Total));
              combined.FullTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, combined.TotalTitle);
              combined.ShortTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, "");

              for (int i = 0; i < combined.StatsList.Count; i++)
              {
                combined.StatsList[i].Rank = Convert.ToUInt16(i + 1);
                combined.UniqueClasses[combined.StatsList[i].ClassName] = 1;
              }
            }
          }
#pragma warning disable CA1031 // Do not catch general exception types
          catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
          {
            if (ex is ArgumentNullException || ex is AggregateException || ex is NullReferenceException || ex is OutOfMemoryException)
            {
              LOG.Error(ex);
            }
          }

          if (options.RequestSummaryData)
          {
            // generating new stats
            var genEvent = new StatsGenerationEvent()
            {
              Type = Labels.TANKPARSE,
              State = "COMPLETED",
              CombinedStats = combined
            };

            genEvent.Groups.AddRange(TankingGroups);
            genEvent.UniqueGroupCount = TankingGroupIds.Count;
            EventsGenerationStatus?.Invoke(this, genEvent);
          }
        }
      }
    }

    private void Reset()
    {
      PlayerTimeRanges.Clear();
      PlayerSubTimeRanges.Clear();
      TankingGroups.Clear();
      TankingGroupIds.Clear();
      RaidTotals = StatsUtil.CreatePlayerStats(Labels.RAIDTOTALS);
      Selected = null;
      Title = "";
    }

    public StatsSummary BuildSummary(string type, CombinedStats currentStats, List<PlayerStats> selected, bool showTotals, bool rankPlayers, bool _, bool showTime, string customTitle)
    {
      List<string> list = new List<string>();

      string title = "";
      string details = "";

      if (currentStats != null)
      {
        if (type == Labels.TANKPARSE)
        {
          if (selected?.Count > 0)
          {
            foreach (PlayerStats stats in selected.OrderByDescending(item => item.Total))
            {
              string playerFormat = rankPlayers ? string.Format(CultureInfo.CurrentCulture, StatsUtil.PLAYER_RANK_FORMAT, stats.Rank, stats.Name) : string.Format(CultureInfo.CurrentCulture, StatsUtil.PLAYER_FORMAT, stats.Name);
              string damageFormat = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_ONLY_FORMAT, StatsUtil.FormatTotals(stats.Total));
              list.Add(playerFormat + damageFormat + " ");
            }
          }

          details = list.Count > 0 ? ", " + string.Join(" | ", list) : "";
          var timeTitle = showTime ? (" " + currentStats.TimeTitle) : "";
          title = StatsUtil.FormatTitle(customTitle ?? currentStats.TargetTitle, timeTitle, showTotals ? currentStats.TotalTitle : "");
        }
        else if (type == Labels.RECEIVEDHEALPARSE)
        {
          if (selected?.Count == 1 && selected[0].SubStats2.TryGetValue("receivedHealing", out PlayerSubStats subStats) && subStats is PlayerStats receivedHealing)
          {
            int rank = 1;
            long totals = 0;
            foreach (var stats in receivedHealing.SubStats.Values.OrderByDescending(stats => stats.Total).Take(10))
            {
              string playerFormat = rankPlayers ? string.Format(CultureInfo.CurrentCulture, StatsUtil.PLAYER_RANK_FORMAT, rank++, stats.Name) : string.Format(CultureInfo.CurrentCulture, StatsUtil.PLAYER_FORMAT, stats.Name);
              string damageFormat = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_ONLY_FORMAT, StatsUtil.FormatTotals(stats.Total));
              list.Add(playerFormat + damageFormat + " ");
              totals += stats.Total;
            }

            var hps = (long)Math.Round(totals / currentStats.RaidStats.TotalSeconds, 2);
            string totalTitle = showTotals ? (selected[0].Name + " Received " + StatsUtil.FormatTotals(totals) + " Healing") : (selected[0].Name + " Received Healing");
            details = list.Count > 0 ? ", " + string.Join(" | ", list) : "";
            var timeTitle = showTime ? currentStats.TimeTitle : "";
            title = StatsUtil.FormatTitle(customTitle ?? currentStats.TargetTitle, timeTitle, totalTitle);
          }
        }
      }

      return new StatsSummary() { Title = title, RankedPlayers = details, };
    }
  }
}
