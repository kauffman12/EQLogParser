using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace EQLogParser
{
  class DamageStatsManager : ISummaryBuilder
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static DamageStatsManager Instance = new DamageStatsManager();

    internal event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal event EventHandler<StatsGenerationEvent> EventsGenerationStatus;

    internal List<List<ActionBlock>> DamageGroups = new List<List<ActionBlock>>();
    internal Dictionary<int, byte> DamageGroupIds = new Dictionary<int, byte>();

    private PlayerStats RaidTotals;
    private readonly ConcurrentDictionary<string, byte> PlayerHasPet = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, string> PetToPlayer = new ConcurrentDictionary<string, string>();
    private readonly List<IAction> Resists = new List<IAction>();
    private List<Fight> Selected;
    private string Title;

    internal DamageStatsManager()
    {
      DataManager.Instance.EventsClearedActiveData += (object sender, bool e) =>
      {
        lock (DamageGroups)
        {
          DamageGroups.Clear();
          RaidTotals = null;
          PlayerHasPet.Clear();
          PetToPlayer.Clear();
          Resists.Clear();
          Selected = null;
          Title = "";
        }
      };
    }

    internal void BuildTotalStats(GenerateStatsOptions options)
    {
      lock (DamageGroups)
      {
        Selected = options.Npcs;
        Title = options.Name;

        try
        {
          FireNewStatsEvent(options);

          RaidTotals = StatsUtil.CreatePlayerStats(Labels.RAID);
          DamageGroups.Clear();
          DamageGroupIds.Clear();
          PlayerHasPet.Clear();
          PetToPlayer.Clear();
          Resists.Clear();

          var damageBlocks = new List<ActionBlock>();
          Selected.ForEach(fight =>
          {
            StatsUtil.UpdateTimeDiffs(RaidTotals, fight);
            damageBlocks.AddRange(fight.DamageBlocks);

            if (fight.GroupId > -1)
            {
              DamageGroupIds[fight.GroupId] = 1;
            }
          });

          damageBlocks.Sort((a, b) => a.BeginTime.CompareTo(b.BeginTime));

          if (damageBlocks.Count > 0)
          {
            RaidTotals.TotalSeconds = RaidTotals.TimeDiffs.Sum();

            var newBlock = new List<ActionBlock>();
            var timeIndex = 0;

            damageBlocks.ForEach(block =>
            {
              if (block.BeginTime > RaidTotals.LastTimes[timeIndex])
              {
                timeIndex++;

                if (newBlock.Count > 0)
                {
                  DamageGroups.Add(newBlock);
                }

                newBlock = new List<ActionBlock>();
              }

              newBlock.Add(block);

              block.Actions.ForEach(action =>
              {
                DamageRecord damage = action as DamageRecord;
                // see if there's a pet mapping, check this first
                string pname = PlayerManager.Instance.GetPlayerFromPet(damage.Attacker);
                if (!string.IsNullOrEmpty(pname) || !string.IsNullOrEmpty(pname = damage.AttackerOwner))
                {
                  PlayerHasPet[pname] = 1;
                  PetToPlayer[damage.Attacker] = pname;
                }
              });
            });

            DamageGroups.Add(newBlock);

            for (int i=0; i<RaidTotals.BeginTimes.Count && i<RaidTotals.LastTimes.Count; i++)
            {
              var group = DataManager.Instance.GetResistsDuring(RaidTotals.BeginTimes[i], RaidTotals.LastTimes[i]);
              group.ForEach(block => Resists.AddRange(block.Actions));
            }

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
        catch (ArgumentNullException ne)
        {
          LOG.Error(ne);
        }
        catch (NullReferenceException nr)
        {
          LOG.Error(nr);
        }
        catch (ArgumentOutOfRangeException aor)
        {
          LOG.Error(aor);
        }
        catch (ArgumentException ae)
        {
          LOG.Error(ae);
        }
        catch(OutOfMemoryException oem)
        {
          LOG.Error(oem);
        }
      }
    }

    internal void RebuildTotalStats(GenerateStatsOptions options)
    {
      if (DamageGroups.Count > 0)
      {
        FireNewStatsEvent(options);
        ComputeDamageStats(options);
      }
    }

    internal OverlayDamageStats ComputeOverlayDamageStats(DamageRecord record, double beginTime, int damageSelectionMode, OverlayDamageStats overlayStats = null)
    {
      if (overlayStats == null)
      {
        overlayStats = new OverlayDamageStats
        {
          RaidStats = new PlayerStats()
        };

        overlayStats.RaidStats.BeginTime = beginTime;
      }
      else
      {
        overlayStats.RaidStats = overlayStats.RaidStats;
      }

      if ((damageSelectionMode == 0 && overlayStats.UniqueNpcs.Count == 0) || beginTime - overlayStats.RaidStats.LastTime > DataManager.FIGHT_TIMEOUT)
      {
        overlayStats.RaidStats.Total = 0;
        overlayStats.RaidStats.BeginTime = beginTime;
        overlayStats.UniqueNpcs.Clear();
        overlayStats.TopLevelStats.Clear();
        overlayStats.AggregateStats.Clear();
        overlayStats.IndividualStats.Clear();
      }

      overlayStats.RaidStats.LastTime = beginTime;
      overlayStats.RaidStats.TotalSeconds = overlayStats.RaidStats.LastTime - overlayStats.RaidStats.BeginTime + 1;

      if (record != null && (record.Type != Labels.BANE || MainWindow.IsBaneDamageEnabled))
      {
        overlayStats.UniqueNpcs[record.Defender] = 1;
        overlayStats.RaidStats.Total += record.Total;

        // see if there's a pet mapping, check this first
        string pname = PlayerManager.Instance.GetPlayerFromPet(record.Attacker);
        if (pname != null || !string.IsNullOrEmpty(pname = record.AttackerOwner))
        {
          PlayerHasPet[pname] = 1;
          PetToPlayer[record.Attacker] = pname;
        }

        bool isPet = PetToPlayer.TryGetValue(record.Attacker, out string player);
        bool needAggregate = isPet || (!isPet && PlayerHasPet.ContainsKey(record.Attacker) && overlayStats.TopLevelStats.ContainsKey(record.Attacker + " +Pets"));

        if (!needAggregate || player == Labels.UNASSIGNED)
        {
          // not a pet
          PlayerStats stats = StatsUtil.CreatePlayerStats(overlayStats.IndividualStats, record.Attacker);
          StatsUtil.UpdateStats(stats, record, beginTime);
          overlayStats.TopLevelStats[record.Attacker] = stats;
          stats.TotalSeconds = stats.LastTime - stats.BeginTime + 1;
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

          StatsUtil.UpdateStats(aggregatePlayerStats, record, beginTime);
          aggregatePlayerStats.TotalSeconds = aggregatePlayerStats.LastTime - aggregatePlayerStats.BeginTime + 1;
        }

        overlayStats.RaidStats.DPS = (long)Math.Round(overlayStats.RaidStats.Total / overlayStats.RaidStats.TotalSeconds, 2);

        var list = overlayStats.TopLevelStats.Values.OrderByDescending(item => item.Total).ToList();
        int found = list.FindIndex(stats => stats.Name.StartsWith(ConfigUtil.PlayerName, StringComparison.Ordinal));

        int renumber;
        if (found > 4)
        {
          var you = list[found];
          you.Rank = Convert.ToUInt16(found + 1);
          overlayStats.StatsList.Clear();
          overlayStats.StatsList.AddRange(list.Take(4));
          overlayStats.StatsList.Add(you);
          renumber = overlayStats.StatsList.Count - 1;
        }
        else
        {
          overlayStats.StatsList.Clear();
          overlayStats.StatsList.AddRange(list.Take(5));
          renumber = overlayStats.StatsList.Count;
        }

        for (int i = 0; i < renumber; i++)
        {
          overlayStats.StatsList[i].Rank = Convert.ToUInt16(i + 1);
        }

        // only calculate the top few
        Parallel.ForEach(overlayStats.StatsList, top => StatsUtil.UpdateCalculations(top, overlayStats.RaidStats));
        overlayStats.TargetTitle = (overlayStats.UniqueNpcs.Count > 1 ? "C(" + overlayStats.UniqueNpcs.Count + "): " : "") + record.Defender;
        overlayStats.TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TIME_FORMAT, overlayStats.RaidStats.TotalSeconds);
        overlayStats.TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(overlayStats.RaidStats.Total), " Damage ", StatsUtil.FormatTotals(overlayStats.RaidStats.DPS));
      }

      return overlayStats;
    }

    internal Dictionary<string, List<HitFreqChartData>> GetHitFreqValues(PlayerStats selected, CombinedStats damageStats)
    {
      Dictionary<string, List<HitFreqChartData>> results = new Dictionary<string, List<HitFreqChartData>>();

      if (damageStats != null)
      {
        // get chart data for player and pets if available
        List<PlayerStats> list = new List<PlayerStats>();
        if (damageStats.Children.ContainsKey(selected.Name))
        {
          list.AddRange(damageStats.Children[selected.Name]);
        }
        else
        {
          list.Add(selected);
        }

        list.ForEach(stat =>
        {
          results[stat.Name] = new List<HitFreqChartData>();
          foreach (string type in stat.SubStats.Keys)
          {
            HitFreqChartData chartData = new HitFreqChartData() { HitType = stat.SubStats[type].Name };

            // add crits
            chartData.CritXValues.AddRange(stat.SubStats[type].CritFreqValues.Keys.OrderBy(key => key));
            chartData.CritXValues.ForEach(damage => chartData.CritYValues.Add(stat.SubStats[type].CritFreqValues[damage]));

            // add non crits
            chartData.NonCritXValues.AddRange(stat.SubStats[type].NonCritFreqValues.Keys.OrderBy(key => key));
            chartData.NonCritXValues.ForEach(damage => chartData.NonCritYValues.Add(stat.SubStats[type].NonCritFreqValues[damage]));
            results[stat.Name].Add(chartData);
          }
        });
      }

      return results;
    }

    internal void FireSelectionEvent(GenerateStatsOptions options, List<PlayerStats> selected)
    {
      FireChartEvent(options, "SELECT", selected);
    }

    internal void FireUpdateEvent(GenerateStatsOptions options, List<PlayerStats> selected = null, Predicate<object> filter = null)
    {
      FireChartEvent(options, "UPDATE", selected, filter);
    }

    internal void FireFilterEvent(GenerateStatsOptions options, Predicate<object> filter)
    {
      FireChartEvent(options, "FILTER", null, filter);
    }

    private void FireCompletedEvent(GenerateStatsOptions options, CombinedStats combined, List<List<ActionBlock>> groups)
    {
      if (options.RequestSummaryData)
      {
        // generating new stats
        var genEvent = new StatsGenerationEvent()
        {
          Type = Labels.DAMAGEPARSE,
          State = "COMPLETED",
          CombinedStats = combined
        };

        genEvent.Groups.AddRange(groups);
        genEvent.UniqueGroupCount = DamageGroupIds.Count;
        EventsGenerationStatus?.Invoke(this, genEvent);
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
      lock (DamageGroups)
      {
        if (options.RequestChartData)
        {
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

    private void ComputeDamageStats(GenerateStatsOptions options)
    {
      lock (DamageGroups)
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

          try
          {
            FireUpdateEvent(options);

            if (options.RequestSummaryData)
            {
              DamageGroups.ForEach(group =>
              {
                // keep track of time range as well as the players that have been updated
                Dictionary<string, PlayerSubStats> allStats = new Dictionary<string, PlayerSubStats>();

                int found = -1;
                if (MainWindow.IsIgnoreIntialPullDamageEnabled)
                {
                  // ignore initial low activity time
                  double previousDps = 0;
                  long rolling = 0;
                  for (int i = 0; group.Count >= 10 && i < 10; i++)
                  {
                    if (previousDps == 0)
                    {
                      rolling = group[i].Actions.Sum(test => (test as DamageRecord).Total);
                      previousDps = rolling / 1.0;
                    }
                    else
                    {
                      double theTime = group[i].BeginTime - group[0].BeginTime + 1;
                      if (theTime > 12.0)
                      {
                        break;
                      }

                      rolling += group[i].Actions.Sum(test => (test as DamageRecord).Total);
                      double currentDps = rolling / (theTime);
                      if (currentDps / previousDps > 1.75)
                      {
                        found = i - 1;
                        break;
                      }
                      else
                      {
                        previousDps = currentDps;
                      }
                    }
                  }
                }

                var goodGroups = found > -1 ? group.GetRange(found, group.Count - found) : group;

                goodGroups.ForEach(block =>
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
                        StatsUtil.UpdateStats(stats, record, block.BeginTime);
                        allStats[record.Attacker] = stats;

                        if (!PetToPlayer.TryGetValue(record.Attacker, out string player) && !PlayerHasPet.ContainsKey(record.Attacker))
                        {
                          // not a pet
                          topLevelStats[record.Attacker] = stats;
                        }
                        else
                        {
                          string origName = player ?? record.Attacker;
                          string aggregateName = (player == Labels.UNASSIGNED) ? origName : origName + " +Pets";

                          PlayerStats aggregatePlayerStats = StatsUtil.CreatePlayerStats(individualStats, aggregateName, origName);
                          StatsUtil.UpdateStats(aggregatePlayerStats, record, block.BeginTime);
                          allStats[aggregateName] = aggregatePlayerStats;
                          topLevelStats[aggregateName] = aggregatePlayerStats;

                          if (!childrenStats.TryGetValue(aggregateName, out Dictionary<string, PlayerStats> children))
                          {
                            childrenStats[aggregateName] = new Dictionary<string, PlayerStats>();
                          }

                          childrenStats[aggregateName][stats.Name] = stats;
                        }

                        PlayerSubStats subStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, record.SubType, record.Type);
                        UpdateSubStats(subStats, record, block.BeginTime);
                        allStats[stats.Name + "=" + Helpers.CreateRecordKey(record.Type, record.SubType)] = subStats;
                      }
                    }
                  });
                });

                foreach(var stats in allStats.Values)
                {
                  stats.TotalSeconds += stats.LastTime - stats.BeginTime + 1;
                  stats.BeginTime = double.NaN;
                }
              });

              RaidTotals.DPS = (long)Math.Round(RaidTotals.Total / RaidTotals.TotalSeconds, 2);

              // add up resists
              var resistCounts = Resists.Cast<ResistRecord>().GroupBy(x => x.Spell).ToDictionary(g => g.Key, g => g.ToList().Count);

              // get special field
              var specials = StatsUtil.GetSpecials(RaidTotals);

              var expandedStats = new ConcurrentBag<PlayerStats>();
              Parallel.ForEach(individualStats.Values, stats =>
              {
                if (topLevelStats.TryGetValue(stats.Name, out PlayerStats topLevel))
                {
                  if (childrenStats.TryGetValue(stats.Name, out Dictionary<string, PlayerStats> children))
                  {
                    foreach (var child in children.Values)
                    {
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
                  }
                  else
                  {
                    expandedStats.Add(stats);
                  }

                  StatsUtil.UpdateCalculations(stats, RaidTotals, resistCounts);

                  if (specials.TryGetValue(stats.OrigName, out string special2))
                  {
                    stats.Special = special2;
                  }
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
          catch (ArgumentNullException anx)
          {
            LOG.Error(anx);
          }
          catch (AggregateException agx)
          {
            LOG.Error(agx);
          }
          catch (NullReferenceException nre)
          {
            LOG.Error(nre);
          }

          FireCompletedEvent(options, combined, DamageGroups);
        }
      }
    }

    public StatsSummary BuildSummary(string type, CombinedStats currentStats, List<PlayerStats> selected, bool showTotals, bool rankPlayers, bool showSpecial)
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

            var dps = playerFormat + damageFormat + " " + timeFormat;

            if (showSpecial && !string.IsNullOrEmpty(stats.Special))
            {
              dps = string.Format(CultureInfo.CurrentCulture, StatsUtil.SPECIAL_FORMAT, dps, stats.Special);
            }

            list.Add(dps);
          }
        }

        details = list.Count > 0 ? ", " + string.Join(", ", list) : "";
        title = StatsUtil.FormatTitle(currentStats.TargetTitle, currentStats.TimeTitle, showTotals ? currentStats.TotalTitle : "");
      }

      return new StatsSummary() { Title = title, RankedPlayers = details };
    }

    private static void UpdateSubStats(PlayerSubStats subStats, DamageRecord record, double beginTime)
    {
      uint critHits = subStats.CritHits;
      StatsUtil.UpdateStats(subStats, record, beginTime);

      // dont count misses or where no damage was done
      if (record.Total > 0)
      {
        Dictionary<long, int> values = subStats.CritHits > critHits ? subStats.CritFreqValues : subStats.NonCritFreqValues;
        Helpers.LongIntAddHelper.Add(values, record.Total, 1);
      }
    }
  }
}

