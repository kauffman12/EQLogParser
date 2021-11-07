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

    internal static CombinedStats ComputeOverlayStats(int mode, int maxRows, string selectedClass)
    {
      CombinedStats combined = null;

      var allDamage = 0L;
      var allTime = new TimeRange();
      var playerTotals = new Dictionary<string, OverlayPlayerTotal>();
      var playerHasPet = new Dictionary<string, bool>();
      var updateTime = 0d;
      var oldestTime = 0d;
      var fights = DataManager.Instance.GetOverlayFights();
      Fight oldestFight = null;
      bool baneEnabled = MainWindow.IsBaneDamageEnabled;

      // clear out anything pending in the queue
      DamageLineParser.CheckSlainQueue(DateUtil.ToDouble(DateTime.Now.AddSeconds(-3)));

      if (fights.Count > 0)
      {
        oldestFight = fights[0];
        foreach (var fight in fights.Where(fight => !fight.Dead || mode > 0))
        {
          foreach (var keypair in fight.PlayerTotals)
          {
            var player = keypair.Key;
            if (!string.IsNullOrEmpty(keypair.Value.PetOwner))
            {
              player = keypair.Value.PetOwner;
              playerHasPet[player] = true;
            }
            else if (PlayerManager.Instance.GetPlayerFromPet(player) is string owner && owner != Labels.UNASSIGNED)
            {
              player = owner;
              playerHasPet[player] = true;
            }

            allDamage += baneEnabled ? keypair.Value.DamageWithBane : keypair.Value.Damage;
            allTime.Add(new TimeSegment(keypair.Value.BeginTime, fight.LastDamageTime));

            if (updateTime == 0)
            {
              updateTime = keypair.Value.UpdateTime;
              oldestTime = keypair.Value.UpdateTime;
              oldestFight = fight;
            }
            else
            {
              updateTime = Math.Max(updateTime, keypair.Value.UpdateTime);
              if (oldestTime > keypair.Value.UpdateTime)
              {
                oldestTime = keypair.Value.UpdateTime;
                oldestFight = fight;
              }
            }

            if (playerTotals.TryGetValue(player, out OverlayPlayerTotal total))
            {
              total.Damage += baneEnabled ? keypair.Value.DamageWithBane : keypair.Value.Damage;
              total.Range.Add(new TimeSegment(keypair.Value.BeginTime, keypair.Value.UpdateTime));
              total.UpdateTime = Math.Max(total.UpdateTime, keypair.Value.UpdateTime);
            }
            else
            {
              playerTotals[player] = new OverlayPlayerTotal
              {
                Name = player,
                Damage = baneEnabled ? keypair.Value.DamageWithBane : keypair.Value.Damage,
                Range = new TimeRange(new TimeSegment(keypair.Value.BeginTime, keypair.Value.UpdateTime)),
                UpdateTime = keypair.Value.UpdateTime
              };
            }
          }
        }

        var timeout = mode == 0 ? DataManager.FIGHTTIMEOUT : mode;
        var totalSeconds = allTime.GetTotal();
        if (oldestFight != null && totalSeconds > 0 && allDamage > 0 && (DateTime.Now - DateTime.MinValue.AddSeconds(updateTime)).TotalSeconds <= timeout)
        {
          int rank = 1;
          var list = new List<PlayerStats>();
          var totalDps = (long)Math.Round(allDamage / totalSeconds, 2);
          int myIndex = -1;

          foreach (var total in playerTotals.Values.OrderByDescending(total => total.Damage))
          {
            var time = total.Range.GetTotal();
            if (time > 0 && (DateTime.Now - DateTime.MinValue.AddSeconds(total.UpdateTime)).TotalSeconds <= DataManager.MAXTIMEOUT)
            {
              PlayerStats playerStats = new PlayerStats()
              {
                Name = playerHasPet.ContainsKey(total.Name) ? total.Name + " +Pets" : total.Name,
                Total = total.Damage,
                DPS = (long)Math.Round(total.Damage / time, 2),
                TotalSeconds = time,
                Rank = (ushort)rank++,
                ClassName = PlayerManager.Instance.GetPlayerClass(total.Name),
                OrigName = total.Name
              };

              if (playerStats.Name.StartsWith(ConfigUtil.PlayerName, StringComparison.Ordinal))
              {
                myIndex = list.Count;
              }

              if (myIndex == list.Count || selectedClass == Properties.Resources.ANY_CLASS || selectedClass == playerStats.ClassName)
              {
                list.Add(playerStats);
              }
            }
          }

          if (myIndex > (maxRows - 1))
          {
            var me = list[myIndex];
            list = list.Take(maxRows - 1).ToList();
            list.Add(me);
          }
          else
          {
            list = list.Take(maxRows).ToList();
          }

          combined = new CombinedStats();
          combined.StatsList.AddRange(list);
          combined.RaidStats = new PlayerStats { Total = allDamage, DPS = totalDps, TotalSeconds = totalSeconds };
          combined.TargetTitle = (fights.Count > 1 ? "C(" + fights.Count + "): " : "") + oldestFight.Name;

          // these are here to support copy/paste of the parse
          combined.TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TIME_FORMAT, combined.RaidStats.TotalSeconds);
          combined.TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(combined.RaidStats.Total),
            " Damage ", StatsUtil.FormatTotals(combined.RaidStats.DPS));
        }
      }

      return combined;
    }

    internal static Dictionary<string, List<HitFreqChartData>> GetHitFreqValues(PlayerStats selected, CombinedStats damageStats)
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
      lock (DamageGroupIds)
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

          Selected = options.Npcs.OrderBy(sel => sel.Id).ToList();
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
        catch (Exception ex)
        {
          LOG.Error(ex);
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
                      var stats = StatsUtil.CreatePlayerStats(individualStats, record.Attacker);

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
                        bool isAttackerPet = PlayerManager.Instance.IsVerifiedPet(record.Attacker);
                        StatsUtil.UpdateStats(stats, record, isAttackerPet);

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
                          StatsUtil.UpdateStats(aggregatePlayerStats, record, isAttackerPet);
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
                        StatsUtil.UpdateStats(subStats, record, isAttackerPet);

                        // dont count misses/dodges or where no damage was done
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
                  combined.StatsList[i].TooltipText = "Hello";
                  combined.UniqueClasses[combined.StatsList[i].ClassName] = 1;

                  if (childrenStats.TryGetValue(combined.StatsList[i].Name, out Dictionary<string, PlayerStats> children))
                  {
                    combined.Children.Add(combined.StatsList[i].Name, children.Values.OrderByDescending(stats => stats.Total).ToList());
                  }
                }
              }
            }
          }
          catch (Exception ex)
          {
            LOG.Error(ex);
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

    public StatsSummary BuildSummary(string type, CombinedStats currentStats, List<PlayerStats> selected, bool showTotals, bool rankPlayers, bool showSpecial, bool showTime, string customTitle)
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
        var timeTitle = showTime ? currentStats.TimeTitle : "";
        title = StatsUtil.FormatTitle(customTitle ?? currentStats.TargetTitle, timeTitle, showTotals ? currentStats.TotalTitle : "");
      }

      return new StatsSummary { Title = title, RankedPlayers = details };
    }
  }
}

