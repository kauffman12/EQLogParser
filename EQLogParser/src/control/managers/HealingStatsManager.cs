using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    private readonly List<List<ActionGroup>> HealingGroups = new List<List<ActionGroup>>();
    private PlayerStats RaidTotals;
    private List<Fight> Selected;
    private string Title;
    private bool IsLimited = false;

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

    internal void RebuildTotalStats()
    {
      lock (HealingGroups)
      {
        if (HealingGroups.Count > 0)
        {
          var newOptions = new GenerateStatsOptions();
          newOptions.Npcs.AddRange(Selected);
          BuildTotalStats(newOptions);
        }
      }
    }

    internal void BuildTotalStats(GenerateStatsOptions options)
    {
      lock (HealingGroups)
      {
        try
        {
          FireNewStatsEvent();
          Reset();

          Selected = options.Npcs.OrderBy(sel => sel.Id).ToList();
          Title = options.Npcs?.FirstOrDefault()?.Name;
          var healingValidator = new HealingValidator();
          IsLimited = healingValidator.IsHealingLimited();
          Selected.ForEach(fight => RaidTotals.Ranges.Add(new TimeSegment(fight.BeginTime, fight.LastTime)));

          if (RaidTotals.Ranges.TimeSegments.Count > 0)
          {
            // calculate totals first since it can modify the ranges
            RaidTotals.TotalSeconds = RaidTotals.MaxTime = RaidTotals.Ranges.GetTotal();

            RaidTotals.Ranges.TimeSegments.ForEach(segment =>
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
              DataManager.Instance.GetHealsDuring(segment.BeginTime, segment.EndTime).ForEach(heal =>
              {
                // copy
                var newBlock = new ActionGroup { BeginTime = heal.BeginTime };
                filtered.Add(newBlock);

                if (currentSpellCounts.Count > 0)
                {
                  previousSpellCounts[currentTime] = currentSpellCounts;
                }

                currentTime = heal.BeginTime;
                currentSpellCounts = new Dictionary<string, HashSet<string>>();

                foreach (var timeKey in previousSpellCounts.Keys.ToList())
                {
                  if (previousSpellCounts.ContainsKey(timeKey))
                  {
                    if (!double.IsNaN(currentTime) && (currentTime - timeKey) > 7)
                    {
                      previousSpellCounts.Remove(timeKey);
                    }
                  }
                }

                foreach (var record in heal.Actions.Cast<HealRecord>())
                {
                  if (PlayerManager.Instance.IsPetOrPlayerOrMerc(record.Healed) || PlayerManager.IsPossiblePlayerName(record.Healed))
                  {
                    if (healingValidator.IsValid(heal, record, currentSpellCounts, previousSpellCounts, ignoreRecords))
                    {
                      newBlock.Actions.Add(record);
                    }
                  }
                }
              });

              filtered.ForEach(heal =>
              {
                var updatedHeal = new ActionGroup() { BeginTime = heal.BeginTime };
                foreach (var record in heal.Actions.Cast<HealRecord>())
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

            ComputeHealingStats();
          }
          else if (Selected == null || Selected.Count == 0)
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
          LOG.Error(ex);
        }
      }
    }

    internal bool PopulateHealing(CombinedStats combined)
    {
      lock (HealingGroups)
      {
        var playerStats = combined.StatsList;
        var individualStats = new Dictionary<string, PlayerStats>();
        var totals = new Dictionary<string, long>();

        // clear out previous
        playerStats.ForEach(stats =>
        {
          stats.Extra = 0;
          stats.MoreStats = null;
        });

        HealingGroups.ForEach(group =>
        {
          group.ForEach(block =>
          {
            block.Actions.ForEach(action =>
            {
              if (action is HealRecord record)
              {
                var stats = StatsUtil.CreatePlayerStats(individualStats, record.Healed);
                StatsUtil.UpdateStats(stats, record);

                var subStats2 = StatsUtil.CreatePlayerSubStats(stats.SubStats2, record.Healer, record.Type);
                StatsUtil.UpdateStats(subStats2, record);

                var spellStatName = record.SubType ?? Labels.SELFHEAL;
                var spellStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, spellStatName, record.Type);
                StatsUtil.UpdateStats(spellStats, record);

                long value = 0;
                if (totals.TryGetValue(record.Healed, out var total))
                {
                  value = total;
                }

                totals[record.Healed] = record.Total + value;
              }
            });
          });
        });

        Parallel.ForEach(playerStats, stat =>
        {
          if (individualStats.TryGetValue(stat.Name, out var indStats))
          {
            if (totals.TryGetValue(stat.Name, out var total))
            {
              stat.Extra = total;
            }

            stat.MoreStats = indStats;

            UpdateStats(indStats, HealedBySpellTimeRanges, HealedByHealerTimeRanges);

            foreach (ref var subStat in indStats.SubStats.ToArray().AsSpan())
            {
              StatsUtil.UpdateCalculations(subStat, indStats);
            }

            foreach (ref var subStat2 in indStats.SubStats2.ToArray().AsSpan())
            {
              StatsUtil.UpdateCalculations(subStat2, indStats);
            }
          }
        });
      }

      return IsLimited;
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
      StatsUtil.UpdateCalculations(stats, RaidTotals);
    }

    internal void FireChartEvent(string action, List<PlayerStats> selected = null)
    {
      lock (HealingGroups)
      {
        // send update
        var de = new DataPointEvent { Action = action, Iterator = new HealGroupCollection(HealingGroups) };

        if (selected != null)
        {
          de.Selected.AddRange(selected);
        }

        EventsUpdateDataPoint?.Invoke(HealingGroups, de);
      }
    }

    private void FireNewStatsEvent()
    {
      // generating new stats
      EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent() { Type = Labels.HEALPARSE, State = "STARTED" });
    }

    private void FireNoDataEvent(string state)
    {
      // nothing to do
      EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent() { Type = Labels.HEALPARSE, State = state });

      FireChartEvent("CLEAR");
    }

    private void ComputeHealingStats()
    {
      lock (HealingGroups)
      {
        if (RaidTotals != null)
        {
          CombinedStats combined = null;
          var individualStats = new Dictionary<string, PlayerStats>();

          // always start over
          RaidTotals.Total = 0;

          try
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
                    var stats = StatsUtil.CreatePlayerStats(individualStats, record.Healer);
                    StatsUtil.UpdateStats(stats, record);

                    var spellStatName = record.SubType ?? Labels.SELFHEAL;
                    var spellStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, spellStatName, record.Type);
                    StatsUtil.UpdateStats(spellStats, record);

                    var healedStatName = record.Healed;
                    var healedStats = StatsUtil.CreatePlayerSubStats(stats.SubStats2, healedStatName, record.Type);
                    StatsUtil.UpdateStats(healedStats, record);
                  }
                });
              });
            });

            RaidTotals.DPS = (long)Math.Round(RaidTotals.Total / RaidTotals.TotalSeconds, 2);
            StatsUtil.PopulateSpecials(RaidTotals);
            Parallel.ForEach(individualStats.Values, stats =>
            {
              UpdateStats(stats, HealerSpellTimeRanges, HealerHealedTimeRanges);
              if (RaidTotals.Specials.TryGetValue(stats.OrigName, out var special2))
              {
                stats.Special = special2;
              }
            });

            combined = new CombinedStats
            {
              RaidStats = RaidTotals,
              TargetTitle = (Selected.Count > 1 ? "Combined (" + Selected.Count + "): " : "") + Title,
              TimeTitle = string.Format(StatsUtil.TIME_FORMAT, RaidTotals.TotalSeconds),
              TotalTitle = string.Format(StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(RaidTotals.Total), " Heals ", StatsUtil.FormatTotals(RaidTotals.DPS))
            };

            combined.StatsList.AddRange(individualStats.Values.AsParallel().OrderByDescending(item => item.Total));
            combined.FullTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, combined.TotalTitle);
            combined.ShortTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, "");

            for (var i = 0; i < combined.StatsList.Count; i++)
            {
              combined.StatsList[i].Rank = Convert.ToUInt16(i + 1);
              combined.UniqueClasses[combined.StatsList[i].ClassName] = 1;
            }

            // generating new stats
            var genEvent = new StatsGenerationEvent()
            {
              Type = Labels.HEALPARSE,
              State = "COMPLETED",
              CombinedStats = combined,
              Limited = IsLimited
            };

            genEvent.Groups.AddRange(HealingGroups);
            EventsGenerationStatus?.Invoke(this, genEvent);
            FireChartEvent("UPDATE");
          }
          catch (Exception ex)
          {
            LOG.Error(ex);
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

    public StatsSummary BuildSummary(string type, CombinedStats currentStats, List<PlayerStats> selected, bool _, bool showDPS, bool showTotals,
      bool rankPlayers, bool __, bool showTime, string customTitle)
    {
      var list = new List<string>();

      var title = "";
      var details = "";

      if (currentStats != null)
      {
        if (type == Labels.HEALPARSE)
        {
          if (selected?.Count > 0)
          {
            foreach (var stats in selected.OrderByDescending(item => item.Total))
            {
              var playerFormat = rankPlayers ? string.Format(StatsUtil.PLAYER_RANK_FORMAT, stats.Rank, stats.Name) : string.Format(StatsUtil.PLAYER_FORMAT, stats.Name);
              var healsFormat = string.Format(StatsUtil.TOTAL_ONLY_FORMAT, StatsUtil.FormatTotals(stats.Total));
              list.Add(playerFormat + healsFormat);
            }
          }

          details = list.Count > 0 ? ", " + string.Join(" | ", list) : "";
          var timeTitle = showTime ? (" " + currentStats.TimeTitle) : "";
          var totals = showDPS ? currentStats.TotalTitle : currentStats.TotalTitle.Split(new string[] { " @" }, 2, StringSplitOptions.RemoveEmptyEntries)[0];
          title = StatsUtil.FormatTitle(customTitle ?? currentStats.TargetTitle, timeTitle, showTotals ? totals : "");
        }
        else if (type == Labels.TOPHEALSPARSE)
        {
          if (selected?.Count == 1 && selected[0].SubStats.Count > 0)
          {
            var rank = 1;
            foreach (var stats in selected[0].SubStats.OrderByDescending(stats => stats.Total).Take(10))
            {
              var abbrv = DataManager.Instance.AbbreviateSpellName(stats.Name);
              var playerFormat = rankPlayers ? string.Format(StatsUtil.PLAYER_RANK_FORMAT, rank++, abbrv) : string.Format(StatsUtil.PLAYER_FORMAT, abbrv);
              var healsFormat = string.Format(StatsUtil.TOTAL_ONLY_FORMAT, StatsUtil.FormatTotals(stats.Total));
              list.Add(playerFormat + healsFormat);
            }

            var totalTitle = selected[0].Name + "'s Top Heals";
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