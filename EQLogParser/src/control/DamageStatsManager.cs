using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EQLogParser
{
  class DamageStatsManager : ISummaryBuilder
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static DamageStatsManager Instance = new DamageStatsManager();

    internal event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal event EventHandler<StatsGenerationEvent> EventsGenerationStatus;

    private readonly Dictionary<int, byte> DamageGroupIds = new Dictionary<int, byte>();
    private readonly ConcurrentDictionary<string, TimeRange> PlayerTimeRanges = new ConcurrentDictionary<string, TimeRange>();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> PlayerSubTimeRanges = new ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>>();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> PlayerPets = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
    private readonly ConcurrentDictionary<string, string> PetToPlayer = new ConcurrentDictionary<string, string>();
    private readonly List<IAction> Resists = new List<IAction>();
    private List<List<ActionBlock>> AllDamageGroups;
    private List<List<ActionBlock>> DamageGroups = new List<List<ActionBlock>>();
    private PlayerStats RaidTotals;
    private List<Fight> Selected;
    private string Title;

    internal DamageStatsManager()
    {
      DataManager.Instance.EventsClearedActiveData += (object sender, bool e) => 
      {
        lock (DamageGroupIds)
        {
          Reset();
        }
      };
    }

    internal int GetGroupCount()
    {
      lock (DamageGroupIds)
      {
        return DamageGroups.Count;
      }
    }

    internal void RebuildTotalStats(GenerateStatsOptions options)
    {
      lock(DamageGroups)
      {
        if (DamageGroups.Count > 0)
        {
          FireNewStatsEvent(options);
          ComputeDamageStats(options);
        }
      }
    }

    internal void BuildTotalStats(GenerateStatsOptions options)
    {
      lock (DamageGroupIds)
      {
        try
        {
          FireNewStatsEvent(options);
          Reset();

          Selected = options.Npcs;
          Title = options.Name;
          var damageBlocks = new List<ActionBlock>();

          Selected.ForEach(fight =>
          {
            damageBlocks.AddRange(fight.DamageBlocks);

            if (fight.GroupId > -1)
            {
              DamageGroupIds[fight.GroupId] = 1;
            }

            RaidTotals.Ranges.Add(new TimeSegment(fight.BeginDamageTime, fight.LastDamageTime));
            StatsUtil.UpdateRaidTimeRanges(fight, PlayerTimeRanges, PlayerSubTimeRanges);
          });

          damageBlocks.Sort((a, b) => a.BeginTime.CompareTo(b.BeginTime));

          if (damageBlocks.Count > 0)
          {
            RaidTotals.TotalSeconds = RaidTotals.Ranges.GetTotal();
            RaidTotals.MaxTime = RaidTotals.TotalSeconds;

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
                  DamageGroups.Add(newBlock);
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

              // update pet mapping
              block.Actions.ForEach(action => UpdatePetMapping(action as DamageRecord));
              lastTime = block.BeginTime;
            });

            DamageGroups.Add(newBlock);
            RaidTotals.Ranges.TimeSegments.ForEach(segment => DataManager.Instance.GetResistsDuring(segment.BeginTime, segment.EndTime).ForEach(block => Resists.AddRange(block.Actions)));
            ComputeDamageStats(options);
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

    internal void ComputeOverlayDamageStats(DamageRecord record, double beginTime, int timeout, OverlayDamageStats overlayStats = null)
    {
      try
      {
        // set current time
        overlayStats.LastTime = beginTime;

        if (record != null && (record.Type != Labels.BANE || MainWindow.IsBaneDamageEnabled))
        {
          overlayStats.RaidStats.Total += record.Total;

          var raidTimeRange = new TimeRange();
          overlayStats.InactiveFights.ForEach(fight => raidTimeRange.Add(new TimeSegment(Math.Max(fight.BeginDamageTime, overlayStats.BeginTime), fight.LastDamageTime)));
          overlayStats.ActiveFights.ForEach(fight => raidTimeRange.Add(new TimeSegment(Math.Max(fight.BeginDamageTime, overlayStats.BeginTime), fight.LastDamageTime)));
          overlayStats.RaidStats.TotalSeconds = Math.Max(raidTimeRange.GetTotal(), overlayStats.RaidStats.TotalSeconds);

           // update pets
          UpdatePetMapping(record);

          bool isPet = PetToPlayer.TryGetValue(record.Attacker, out string player);
          bool needAggregate = isPet || (!isPet && PlayerPets.ContainsKey(record.Attacker) && overlayStats.TopLevelStats.ContainsKey(record.Attacker + " +Pets"));

          if (!needAggregate)
          {
            // not a pet
            PlayerStats stats = StatsUtil.CreatePlayerStats(overlayStats.IndividualStats, record.Attacker);
            overlayStats.TopLevelStats[record.Attacker] = stats;
            StatsUtil.UpdateStats(stats, record);
            stats.LastTime = beginTime;
          }
          else
          {
            string origName = player ?? record.Attacker;
            string aggregateName = origName + " +Pets";

            PlayerStats aggregatePlayerStats;
            aggregatePlayerStats = StatsUtil.CreatePlayerStats(overlayStats.IndividualStats, aggregateName, origName);
            overlayStats.TopLevelStats[aggregateName] = aggregatePlayerStats;

            if (overlayStats.TopLevelStats.ContainsKey(origName))
            {
              var origPlayer = overlayStats.TopLevelStats[origName];
              StatsUtil.MergeStats(aggregatePlayerStats, origPlayer);
              overlayStats.TopLevelStats.Remove(origName);
              overlayStats.IndividualStats.Remove(origName);
            }

            if (record.Attacker != origName && overlayStats.TopLevelStats.ContainsKey(record.Attacker))
            {
              var origPet = overlayStats.TopLevelStats[record.Attacker];
              StatsUtil.MergeStats(aggregatePlayerStats, origPet);
              overlayStats.TopLevelStats.Remove(record.Attacker);
              overlayStats.IndividualStats.Remove(record.Attacker);
            }

            StatsUtil.UpdateStats(aggregatePlayerStats, record);
            aggregatePlayerStats.LastTime = beginTime;
          }

          overlayStats.RaidStats.DPS = (long)Math.Round(overlayStats.RaidStats.Total / overlayStats.RaidStats.TotalSeconds, 2);

          var list = overlayStats.TopLevelStats.Values.OrderByDescending(item => item.Total).ToList();
          int found = list.FindIndex(stats => stats.Name.StartsWith(ConfigUtil.PlayerName, StringComparison.Ordinal));
          var you = found > -1 ? list[found] : null;

          int renumber;
          if (found > 4)
          {
            you.Rank = Convert.ToUInt16(found + 1);
            overlayStats.StatsList.Clear();
            overlayStats.StatsList.AddRange(list.Where(stats => (stats != null && stats == you) || beginTime - stats.LastTime <= timeout).Take(4));
            overlayStats.StatsList.Add(you);
            renumber = overlayStats.StatsList.Count - 1;
          }
          else
          {
            overlayStats.StatsList.Clear();
            overlayStats.StatsList.AddRange(list.Where(stats => (stats != null && stats == you) || beginTime - stats.LastTime <= timeout).Take(5));
            renumber = overlayStats.StatsList.Count;
          }

          for (int i=0; i<overlayStats.StatsList.Count; i++)
          {
            if (i < renumber)
            {
              overlayStats.StatsList[i].Rank = Convert.ToUInt16(i + 1);
            }

            // only update time if damage changed
            if (overlayStats.StatsList[i].LastTime == beginTime && overlayStats.StatsList[i].CalcTime != beginTime)
            {
              var timeRange = new TimeRange();
              if (PlayerPets.TryGetValue(overlayStats.StatsList[i].OrigName, out ConcurrentDictionary<string, byte> mapping))
              {
                mapping.Keys.ToList().ForEach(key =>
                {
                  AddSegments(timeRange, overlayStats.InactiveFights, key, overlayStats.BeginTime);
                  AddSegments(timeRange, overlayStats.ActiveFights, key, overlayStats.BeginTime);
                });
              }

              AddSegments(timeRange, overlayStats.InactiveFights, overlayStats.StatsList[i].OrigName, overlayStats.BeginTime);
              AddSegments(timeRange, overlayStats.ActiveFights, overlayStats.StatsList[i].OrigName, overlayStats.BeginTime);
              overlayStats.StatsList[i].TotalSeconds = Math.Max(timeRange.GetTotal(), overlayStats.StatsList[i].TotalSeconds);
              overlayStats.StatsList[i].CalcTime = beginTime;
            }

            StatsUtil.UpdateCalculations(overlayStats.StatsList[i], overlayStats.RaidStats);
          }

          var count = overlayStats.InactiveFights.Count + overlayStats.ActiveFights.Count;
          overlayStats.TargetTitle = (count > 1 ? "C(" + count + "): " : "") + record.Defender;
          overlayStats.TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TIME_FORMAT, overlayStats.RaidStats.TotalSeconds);
          overlayStats.TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(overlayStats.RaidStats.Total), 
            " Damage ", StatsUtil.FormatTotals(overlayStats.RaidStats.DPS));
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

      void AddSegments(TimeRange range, List<Fight> fights, string key, double start)
      {
        fights.ForEach(fight =>
        {
          if (fight.DamageSegments.TryGetValue(key, out TimeSegment segment) && segment.EndTime >= start)
          {
            range.Add(new TimeSegment(Math.Max(segment.BeginTime, start), segment.EndTime));
          }
        });
      }
    }

    internal Dictionary<string, List<HitFreqChartData>> GetHitFreqValues(PlayerStats selected, CombinedStats damageStats)
    {
      Dictionary<string, List<HitFreqChartData>> results = new Dictionary<string, List<HitFreqChartData>>();

      // get chart data for player and pets if available
      if (damageStats?.Children.ContainsKey(selected.Name) == true)
      {
        damageStats?.Children[selected.Name].ForEach(stats => AddStats(stats));
      }
      else
      {
        AddStats(selected);
      }

      return results;

      void AddStats(PlayerStats stats)
      {
        results[stats.Name] = new List<HitFreqChartData>();
        foreach (string type in stats.SubStats.Keys)
        {
          HitFreqChartData chartData = new HitFreqChartData() { HitType = stats.SubStats[type].Name };

          // add crits
          chartData.CritXValues.AddRange(stats.SubStats[type].CritFreqValues.Keys.OrderBy(key => key));
          chartData.CritXValues.ForEach(damage => chartData.CritYValues.Add(stats.SubStats[type].CritFreqValues[damage]));

          // add non crits
          chartData.NonCritXValues.AddRange(stats.SubStats[type].NonCritFreqValues.Keys.OrderBy(key => key));
          chartData.NonCritXValues.ForEach(damage => chartData.NonCritYValues.Add(stats.SubStats[type].NonCritFreqValues[damage]));
          results[stats.Name].Add(chartData);
        }
      }
    }

    private void ComputeDamageStats(GenerateStatsOptions options)
    {
      lock (DamageGroupIds)
      {
        if (RaidTotals != null)
        {
          CombinedStats combined = null;
          ConcurrentDictionary<string, Dictionary<string, PlayerStats>> childrenStats = new ConcurrentDictionary<string, Dictionary<string, PlayerStats>>();
          ConcurrentDictionary<string, PlayerStats> topLevelStats = new ConcurrentDictionary<string, PlayerStats>();
          ConcurrentDictionary<string, PlayerStats> aggregateStats = new ConcurrentDictionary<string, PlayerStats>();
          Dictionary<string, PlayerStats> individualStats = new Dictionary<string, PlayerStats>();

          // always start over
          RaidTotals.Total = 0;
          double stopTime = -1;

          try
          {
            FireChartEvent(options, "UPDATE");

            if (options.RequestSummaryData)
            {
              if (options.MaxSeconds > -1 && options.MaxSeconds <= RaidTotals.MaxTime && options.MaxSeconds != RaidTotals.TotalSeconds)
              {
                var filteredGroups = new List<List<ActionBlock>>();
                AllDamageGroups.ForEach(group =>
                {
                  var filteredBlocks = new List<ActionBlock>();
                  filteredGroups.Add(filteredBlocks);
                  group.ForEach(block =>
                  {
                    stopTime = stopTime == -1 ? block.BeginTime + options.MaxSeconds : stopTime;
                    if (block.BeginTime <= stopTime)
                    {
                      filteredBlocks.Add(block);
                    }
                  });
                });

                DamageGroups = filteredGroups;
                RaidTotals.TotalSeconds = options.MaxSeconds;
              }
              else
              {
                DamageGroups = AllDamageGroups;
              }

              DamageGroups.ForEach(group =>
              {
                group.ForEach(block =>
                {
                  block.Actions.ForEach(action =>
                  {
                    if (action is DamageRecord record)
                    {
                      PlayerStats stats = StatsUtil.CreatePlayerStats(individualStats, record.Attacker);

                      if (!MainWindow.IsBaneDamageEnabled && record.Type == Labels.BANE)
                      {
                        stats.BaneHits++;

                        if (individualStats.TryGetValue(stats.OrigName + " +Pets", out PlayerStats temp))
                        {
                          temp.BaneHits++;
                        }
                      }
                      else
                      {
                        RaidTotals.Total += record.Total;
                        StatsUtil.UpdateStats(stats, record);

                        if ((!PetToPlayer.TryGetValue(record.Attacker, out string player) && !PlayerPets.ContainsKey(record.Attacker)) || player == Labels.UNASSIGNED)
                        {
                          topLevelStats[record.Attacker] = stats;
                          stats.IsTopLevel = true;
                        }
                        else
                        {
                          string origName = player ?? record.Attacker;
                          string aggregateName = origName + " +Pets";

                          PlayerStats aggregatePlayerStats = StatsUtil.CreatePlayerStats(individualStats, aggregateName, origName);
                          StatsUtil.UpdateStats(aggregatePlayerStats, record);
                          topLevelStats[aggregateName] = aggregatePlayerStats;

                          if (!childrenStats.TryGetValue(aggregateName, out Dictionary<string, PlayerStats> children))
                          {
                            childrenStats[aggregateName] = new Dictionary<string, PlayerStats>();
                          }

                          
                          childrenStats[aggregateName][stats.Name] = stats;
                          stats.IsTopLevel = false;
                        }

                        PlayerSubStats subStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, record.SubType, record.Type);

                        uint critHits = subStats.CritHits;
                        StatsUtil.UpdateStats(subStats, record);

                        // dont count misses or where no damage was done
                        if (record.Total > 0)
                        {
                          Dictionary<long, int> values = subStats.CritHits > critHits ? subStats.CritFreqValues : subStats.NonCritFreqValues;
                          Helpers.LongIntAddHelper.Add(values, record.Total, 1);
                        }
                      }
                    }
                  });
                });
              });

              RaidTotals.DPS = (long)Math.Round(RaidTotals.Total / RaidTotals.TotalSeconds, 2);
              var resistCounts = Resists.Cast<ResistRecord>().GroupBy(x => x.Spell).ToDictionary(g => g.Key, g => g.ToList().Count);
              var specials = StatsUtil.GetSpecials(RaidTotals);
              var expandedStats = new ConcurrentBag<PlayerStats>();

              individualStats.Values.AsParallel().Where(stats => topLevelStats.ContainsKey(stats.Name)).ForAll(stats =>
              {
                if (childrenStats.TryGetValue(stats.Name, out Dictionary<string, PlayerStats> children))
                {
                  var timeRange = new TimeRange();
                  foreach (var child in children.Values)
                  {
                    if (PlayerTimeRanges.TryGetValue(child.Name, out TimeRange range))
                    {
                      StatsUtil.UpdateAllStatsTimeRanges(child, PlayerTimeRanges, PlayerSubTimeRanges, stopTime);
                      timeRange.Add(range.TimeSegments);
                    }

                    expandedStats.Add(child);
                    StatsUtil.UpdateCalculations(child, RaidTotals, resistCounts);

                    if (stats.Total > 0)
                    {
                      child.Percent = Math.Round(Convert.ToDouble(child.Total) / stats.Total * 100, 2);
                    }

                    if (specials.TryGetValue(child.Name, out string special1))
                    {
                      child.Special = special1;
                    }
                  }

                  var filteredTimeRange = StatsUtil.FilterMaxTime(timeRange, stopTime);
                  stats.TotalSeconds = filteredTimeRange.GetTotal();
                }
                else
                {
                  expandedStats.Add(stats);
                  StatsUtil.UpdateAllStatsTimeRanges(stats, PlayerTimeRanges, PlayerSubTimeRanges, stopTime);
                }

                StatsUtil.UpdateCalculations(stats, RaidTotals, resistCounts);

                if (specials.TryGetValue(stats.OrigName, out string special2))
                {
                  stats.Special = special2;
                }
              });

              combined = new CombinedStats
              {
                RaidStats = RaidTotals,
                TargetTitle = (Selected.Count > 1 ? "Combined (" + Selected.Count + "): " : "") + Title,
                TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TIME_FORMAT, RaidTotals.TotalSeconds),
                TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(RaidTotals.Total), " Damage ", StatsUtil.FormatTotals(RaidTotals.DPS))
              };

              combined.StatsList.AddRange(topLevelStats.Values.AsParallel().OrderByDescending(item => item.Total));
              combined.FullTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, combined.TotalTitle);
              combined.ShortTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, "");
              combined.ExpandedStatsList.AddRange(expandedStats.AsParallel().OrderByDescending(item => item.Total));

              for (int i = 0; i < combined.ExpandedStatsList.Count; i++)
              {
                combined.ExpandedStatsList[i].Rank = Convert.ToUInt16(i + 1);
                if (combined.StatsList.Count > i)
                {
                  combined.StatsList[i].Rank = Convert.ToUInt16(i + 1);
                  combined.UniqueClasses[combined.StatsList[i].ClassName] = 1;

                  if (childrenStats.TryGetValue(combined.StatsList[i].Name, out Dictionary<string, PlayerStats> children))
                  {
                    combined.Children.Add(combined.StatsList[i].Name, children.Values.OrderByDescending(stats => stats.Total).ToList());
                  }
                }
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
              Type = Labels.DAMAGEPARSE,
              State = "COMPLETED",
              CombinedStats = combined
            };

            genEvent.Groups.AddRange(DamageGroups);
            genEvent.UniqueGroupCount = DamageGroupIds.Count;
            EventsGenerationStatus?.Invoke(this, genEvent);
          }
        }
      }
    }

    private void FireNewStatsEvent(GenerateStatsOptions options)
    {
      if (options.RequestSummaryData)
      {
        // generating new stats
        EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent() { Type = Labels.DAMAGEPARSE, State = "STARTED" });
      }
    }

    private void FireNoDataEvent(GenerateStatsOptions options, string state)
    {
      if (options.RequestSummaryData)
      {
        // nothing to do
        EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent() { Type = Labels.DAMAGEPARSE, State = state });
      }

      FireChartEvent(options, "CLEAR");
    }

    internal void FireChartEvent(GenerateStatsOptions options, string action, List<PlayerStats> selected = null, Predicate<object> filter = null)
    {
      lock (DamageGroupIds)
      {
        if (options.RequestChartData)
        {
          // reset groups
          if (options.MaxSeconds == long.MinValue && AllDamageGroups != null)
          {
            DamageGroups = AllDamageGroups;
          }

          // send update
          DataPointEvent de = new DataPointEvent() { Action = action, Iterator = new DamageGroupCollection(DamageGroups), Filter = filter };

          if (selected != null)
          {
            de.Selected.AddRange(selected);
          }

          EventsUpdateDataPoint?.Invoke(DamageGroups, de);
        }
      }
    }

    private void Reset()
    {
      AllDamageGroups = DamageGroups;
      DamageGroups.Clear();
      DamageGroupIds.Clear();
      RaidTotals = StatsUtil.CreatePlayerStats(Labels.RAIDTOTALS);
      PlayerPets.Clear();
      PetToPlayer.Clear();
      Resists.Clear();
      PlayerTimeRanges.Clear();
      PlayerSubTimeRanges.Clear();
      Selected = null;
      Title = "";
    }

    private void UpdatePetMapping(DamageRecord damage)
    {
      string pname = PlayerManager.Instance.GetPlayerFromPet(damage.Attacker);
      if ((!string.IsNullOrEmpty(pname) && pname != Labels.UNASSIGNED) || !string.IsNullOrEmpty(pname = damage.AttackerOwner))
      {
        if (!PlayerPets.TryGetValue(pname, out ConcurrentDictionary<string, byte> mapping))
        {
          mapping = new ConcurrentDictionary<string, byte>();
          PlayerPets[pname] = mapping;
        }

        mapping[damage.Attacker] = 1;
        PetToPlayer[damage.Attacker] = pname;
      }
    }

    public StatsSummary BuildSummary(string type, CombinedStats currentStats, List<PlayerStats> selected, bool showTotals, bool rankPlayers, bool showSpecial, bool showTime)
    {
      List<string> list = new List<string>();

      string title = "";
      string details = "";

      if (currentStats != null && type == Labels.DAMAGEPARSE)
      {
        if (selected?.Count > 0)
        {
          foreach (PlayerStats stats in selected.OrderByDescending(item => item.Total))
          {
            string playerFormat = rankPlayers ? string.Format(CultureInfo.CurrentCulture, StatsUtil.PLAYER_RANK_FORMAT, stats.Rank, stats.Name) : string.Format(CultureInfo.CurrentCulture, StatsUtil.PLAYER_FORMAT, stats.Name);
            string damageFormat = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(stats.Total), "", StatsUtil.FormatTotals(stats.DPS));
            string timeFormat = string.Format(CultureInfo.CurrentCulture, StatsUtil.TIME_FORMAT, stats.TotalSeconds);

            var dps = playerFormat + damageFormat;

            if (showTime)
            {
              dps += " " + timeFormat;
            }

            if (showSpecial && !string.IsNullOrEmpty(stats.Special))
            {
              dps = string.Format(CultureInfo.CurrentCulture, StatsUtil.SPECIAL_FORMAT, dps, stats.Special);
            }

            list.Add(dps);
          }
        }

        details = list.Count > 0 ? ", " + string.Join(" | ", list) : "";
        var timeTitle = showTime ? (" " + currentStats.TimeTitle) : "";
        title = StatsUtil.FormatTitle(currentStats.TargetTitle, timeTitle, showTotals ? currentStats.TotalTitle : "");
      }

      return new StatsSummary { Title = title, RankedPlayers = details };
    }
  }
}

