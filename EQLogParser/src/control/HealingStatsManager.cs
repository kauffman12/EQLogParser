using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  class HealingStatsManager : ISummaryBuilder
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static HealingStatsManager Instance = new HealingStatsManager();

    internal event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal event EventHandler<StatsGenerationEvent> EventsGenerationStatus;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> HealedByHealerTimeRanges =
      new ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>>();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> HealedBySpellTimeRanges =
      new ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>>();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> HealerHealedTimeRanges =
      new ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>>();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> HealerSpellTimeRanges =
      new ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>>();

    private readonly List<List<ActionBlock>> HealingGroups = new List<List<ActionBlock>>();
    private PlayerStats RaidTotals;
    private List<Fight> Selected;
    private string Title;

    internal HealingStatsManager()
    {
      DataManager.Instance.EventsClearedActiveData += (object sender, bool e) =>
      {
        lock (HealingGroups)
        {
          Reset();
        }
      };
    }

    internal int GetGroupCount()
    {
      lock (HealingGroups)
      {
        return HealingGroups.Count;
      }
    }

    internal void RebuildTotalStats(GenerateStatsOptions options, bool updatedAoEOption = false)
    {
      lock (HealingGroups)
      {
        if (HealingGroups.Count > 0)
        {
          if (updatedAoEOption)
          {
            var newOptions = new GenerateStatsOptions() { Name = Title, RequestChartData = options.RequestChartData, RequestSummaryData = options.RequestSummaryData };
            newOptions.Npcs.AddRange(Selected);
            BuildTotalStats(newOptions);
          }
          else
          {
            FireNewStatsEvent(options);
            ComputeHealingStats(options);
          }
        }
      }
    }

    internal void BuildTotalStats(GenerateStatsOptions options)
    {
      lock (HealingGroups)
      {
        try
        {
          FireNewStatsEvent(options);
          Reset();

          Selected = options.Npcs;
          Title = options.Name;

          Selected.ForEach(fight => RaidTotals.Ranges.Add(new TimeSegment(fight.BeginTankingTime, fight.LastTankingTime)));

          if (RaidTotals.Ranges.TimeSegments.Count > 0)
          {
            // calculate totals first since it can modify the ranges
            RaidTotals.TotalSeconds = RaidTotals.MaxTime = RaidTotals.Ranges.GetTotal();

            RaidTotals.Ranges.TimeSegments.ForEach(segment =>
            {
              var updatedHeals = new List<ActionBlock>();
              var healedByHealerTimeSegments = new Dictionary<string, Dictionary<string, TimeSegment>>();
              var healedBySpellTimeSegments = new Dictionary<string, Dictionary<string, TimeSegment>>();
              var healerHealedTimeSegments = new Dictionary<string, Dictionary<string, TimeSegment>>();
              var healerSpellTimeSegments = new Dictionary<string, Dictionary<string, TimeSegment>>();
              DataManager.Instance.GetHealsDuring(segment.BeginTime, segment.EndTime).ForEach(heal =>
              {
                var updatedHeal = new ActionBlock() { BeginTime = heal.BeginTime };
                foreach (var record in heal.Actions.Cast<HealRecord>())
                {
                  if (PlayerManager.Instance.IsPetOrPlayer(record.Healed) || PlayerManager.Instance.IsPossiblePlayerName(record.Healed))
                  {
                    bool valid = true;
                    SpellData spellData;
                    if (record.SubType != null && (spellData = DataManager.Instance.GetSpellByName(record.SubType)) != null)
                    {
                      if (spellData.Target == (byte)SpellTarget.TARGETAE || spellData.Target == (byte)SpellTarget.NEARBYPLAYERSAE ||
                        spellData.Target == (byte)SpellTarget.TARGETRINGAE)
                      {
                        valid = MainWindow.IsAoEHealingEnabled;
                      }
                    }

                    if (valid)
                    {
                      updatedHeal.Actions.Add(record);
                      // store substats and substats2 which is based on the player that was healed
                      var key = Helpers.CreateRecordKey(record.Type, record.SubType);
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
              });

              Parallel.ForEach(healedByHealerTimeSegments, kv => StatsUtil.AddSubTimeEntry(HealedByHealerTimeRanges, kv));
              Parallel.ForEach(healedBySpellTimeSegments, kv => StatsUtil.AddSubTimeEntry(HealedBySpellTimeRanges, kv));
              Parallel.ForEach(healerHealedTimeSegments, kv => StatsUtil.AddSubTimeEntry(HealerHealedTimeRanges, kv));
              Parallel.ForEach(healerSpellTimeSegments, kv => StatsUtil.AddSubTimeEntry(HealerSpellTimeRanges, kv));

              if (updatedHeals.Count > 0)
              {
                HealingGroups.Add(updatedHeals);
              }
            });

            ComputeHealingStats(options);
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

    internal void PopulateHealing(CombinedStats combined)
    {
      lock (HealingGroups)
      {
        List<PlayerStats> playerStats = combined.StatsList;
        Dictionary<string, PlayerStats> individualStats = new Dictionary<string, PlayerStats>();
        Dictionary<string, long> totals = new Dictionary<string, long>();

        HealingGroups.ForEach(group =>
        {
          group.ForEach(block =>
          {
            block.Actions.ForEach(action =>
            {
              if (action is HealRecord record)
              {
                PlayerStats stats = StatsUtil.CreatePlayerStats(individualStats, record.Healed);
                StatsUtil.UpdateStats(stats, record);

                PlayerSubStats subStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, record.Healer, record.Type);
                StatsUtil.UpdateStats(subStats, record);

                var spellStatName = record.SubType ?? Labels.SELFHEAL;
                PlayerSubStats spellStats = StatsUtil.CreatePlayerSubStats(stats.SubStats2, spellStatName, record.Type);
                StatsUtil.UpdateStats(spellStats, record);

                long value = 0;
                if (totals.ContainsKey(record.Healed))
                {
                  value = totals[record.Healed];
                }

                totals[record.Healed] = record.Total + value;
              }
            });
          });
        });

        Parallel.ForEach(playerStats, stat =>
        {
          if (individualStats.ContainsKey(stat.Name))
          {
            if (totals.ContainsKey(stat.Name))
            {
              stat.Extra = totals[stat.Name];
            }

            var indStats = individualStats[stat.Name];
            stat.SubStats2["receivedHealing"] = indStats;
            UpdateStats(indStats, HealedBySpellTimeRanges, HealedByHealerTimeRanges);

            indStats.SubStats.Values.ToList().ForEach(subStat => StatsUtil.UpdateCalculations(subStat, indStats));
            indStats.SubStats2.Values.ToList().ForEach(subStat => StatsUtil.UpdateCalculations(subStat, indStats));
          }
        });
      }
    }

    private void UpdateStats(PlayerStats stats, ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> calc,
      ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> secondary)
    {
      if (calc.TryGetValue(stats.Name, out ConcurrentDictionary<string, TimeRange> ranges))
      {
        // base the total time range off the sub times ranges since healing doesn't have good Fight segments to work with
        var totalRange = new TimeRange();
        ranges.Values.ToList().ForEach(range => totalRange.Add(range.TimeSegments));
        stats.TotalSeconds = stats.MaxTime = totalRange.GetTotal();
      }

      StatsUtil.UpdateSubStatsTimeRanges(stats, calc);
      StatsUtil.UpdateSubStatsTimeRanges(stats, secondary);
      StatsUtil.UpdateCalculations(stats, RaidTotals);
    }

    internal void FireChartEvent(GenerateStatsOptions options, string action, List<PlayerStats> selected = null, Predicate<object> filter = null)
    {
      lock (HealingGroups)
      {
        if (options.RequestChartData)
        {
          // send update
          DataPointEvent de = new DataPointEvent() { Action = action, Iterator = new HealGroupCollection(HealingGroups), Filter = filter };

          if (selected != null)
          {
            de.Selected.AddRange(selected);
          }

          EventsUpdateDataPoint?.Invoke(HealingGroups, de);
        }
      }
    }

    private void FireNewStatsEvent(GenerateStatsOptions options)
    {
      if (options.RequestSummaryData)
      {
        // generating new stats
        EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent() { Type = Labels.HEALPARSE, State = "STARTED" });
      }
    }

    private void FireNoDataEvent(GenerateStatsOptions options, string state)
    {
      if (options.RequestSummaryData)
      {
        // nothing to do
        EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent() { Type = Labels.HEALPARSE, State = state });
      }

      FireChartEvent(options, "CLEAR");
    }

    private void ComputeHealingStats(GenerateStatsOptions options)
    {
      lock (HealingGroups)
      {
        if (RaidTotals != null)
        {
          CombinedStats combined = null;
          Dictionary<string, PlayerStats> individualStats = new Dictionary<string, PlayerStats>();

          // always start over
          RaidTotals.Total = 0;

          try
          {
            FireChartEvent(options, "UPDATE");

            if (options.RequestSummaryData)
            {
              HealingGroups.ForEach(group =>
              {
                group.ForEach(block =>
                {
                  block.Actions.ForEach(action =>
                  {
                    if (action is HealRecord record)
                    {
                      RaidTotals.Total += record.Total;
                      PlayerStats stats = StatsUtil.CreatePlayerStats(individualStats, record.Healer);
                      StatsUtil.UpdateStats(stats, record);

                      var spellStatName = record.SubType ?? Labels.SELFHEAL;
                      PlayerSubStats spellStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, spellStatName, record.Type);
                      StatsUtil.UpdateStats(spellStats, record);

                      var healedStatName = record.Healed;
                      PlayerSubStats healedStats = StatsUtil.CreatePlayerSubStats(stats.SubStats2, healedStatName, record.Type);
                      StatsUtil.UpdateStats(healedStats, record);
                    }
                  });
                });
              });

              RaidTotals.DPS = (long)Math.Round(RaidTotals.Total / RaidTotals.TotalSeconds, 2);
              Parallel.ForEach(individualStats.Values, stats => UpdateStats(stats, HealerSpellTimeRanges, HealerHealedTimeRanges));

              combined = new CombinedStats
              {
                RaidStats = RaidTotals,
                TargetTitle = (Selected.Count > 1 ? "Combined (" + Selected.Count + "): " : "") + Title,
                TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TIME_FORMAT, RaidTotals.TotalSeconds),
                TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(RaidTotals.Total), " Heals ", StatsUtil.FormatTotals(RaidTotals.DPS))
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
            if (ex is ArgumentNullException || ex is NullReferenceException || ex is ArgumentOutOfRangeException || ex is ArgumentException || ex is OutOfMemoryException)
            {
              LOG.Error(ex);
            }
          }

          if (options.RequestSummaryData)
          {
            // generating new stats
            var genEvent = new StatsGenerationEvent()
            {
              Type = Labels.HEALPARSE,
              State = "COMPLETED",
              CombinedStats = combined
            };

            genEvent.Groups.AddRange(HealingGroups);
            EventsGenerationStatus?.Invoke(this, genEvent);
          }
        }
      }
    }

    private void Reset()
    {
      HealedByHealerTimeRanges.Clear();
      HealedBySpellTimeRanges.Clear();
      HealerHealedTimeRanges.Clear();
      HealerSpellTimeRanges.Clear();
      HealingGroups.Clear();
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
        if (type == Labels.HEALPARSE)
        {
          if (selected?.Count > 0)
          {
            foreach (PlayerStats stats in selected.OrderByDescending(item => item.Total))
            {
              string playerFormat = rankPlayers ? string.Format(CultureInfo.CurrentCulture, StatsUtil.PLAYER_RANK_FORMAT, stats.Rank, stats.Name) : string.Format(CultureInfo.CurrentCulture, StatsUtil.PLAYER_FORMAT, stats.Name);
              string healsFormat = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_ONLY_FORMAT, StatsUtil.FormatTotals(stats.Total));
              list.Add(playerFormat + healsFormat + " ");
            }
          }

          details = list.Count > 0 ? ", " + string.Join(" | ", list) : "";
          var timeTitle = showTime ? (" " + currentStats.TimeTitle) : "";
          title = StatsUtil.FormatTitle(customTitle ?? currentStats.TargetTitle, timeTitle, showTotals ? currentStats.TotalTitle : "");
        }
        else if (type == Labels.TOPHEALSPARSE)
        {
          if (selected?.Count == 1 && selected[0].SubStats.Count > 0)
          {
            int rank = 1;
            foreach (var stats in selected[0].SubStats.Values.OrderByDescending(stats => stats.Total).Take(10))
            {
              string abbrv = DataManager.Instance.AbbreviateSpellName(stats.Name);
              string playerFormat = rankPlayers ? string.Format(CultureInfo.CurrentCulture, StatsUtil.PLAYER_RANK_FORMAT, rank++, abbrv) : string.Format(CultureInfo.CurrentCulture, StatsUtil.PLAYER_FORMAT, abbrv);
              string healsFormat = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_ONLY_FORMAT, StatsUtil.FormatTotals(stats.Total));
              list.Add(playerFormat + healsFormat + " ");
            }

            string totalTitle = selected[0].Name + "'s Top Heals";
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