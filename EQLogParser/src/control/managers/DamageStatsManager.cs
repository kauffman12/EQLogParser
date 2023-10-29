using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace EQLogParser
{
  class DamageStatsManager : ISummaryBuilder
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal static DamageStatsManager Instance = new();

    internal event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal event EventHandler<StatsGenerationEvent> EventsGenerationStatus;

    private readonly Dictionary<int, byte> DamageGroupIds = new();
    private readonly ConcurrentDictionary<string, TimeRange> PlayerTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> PlayerSubTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> PlayerPets = new();
    private readonly ConcurrentDictionary<string, string> PetToPlayer = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> ResistCounts = new();
    private List<List<ActionGroup>> AllDamageGroups;
    private List<List<ActionGroup>> DamageGroups = new();
    private PlayerStats RaidTotals;
    private List<Fight> Selected;
    private string Title;

    private static readonly OverlayData OverlayDamageData = new();
    private static readonly OverlayData OverlayTankData = new();

    internal static DamageOverlayStats ComputeOverlayStats(bool reset, int mode, int maxRows, string selectedClass)
    {
      // clear out anything pending in the queue
      DamageLineParser.CheckSlainQueue(DateUtil.ToDouble(DateTime.Now.AddSeconds(-3)));

      var deadFights = new HashSet<long>();
      var damage = ComputeOverlayDamageStats(OverlayDamageData, true, reset, mode, maxRows, selectedClass, deadFights);
      var tank = ComputeOverlayDamageStats(OverlayTankData, false, reset, mode, maxRows, selectedClass, deadFights);
      deadFights.ToList().ForEach(id => DataManager.Instance.RemoveOverlayFight(id));
      return (damage == null && tank == null) ? null : new DamageOverlayStats { DamageStats = damage, TankStats = tank };
    }

    private static CombinedStats ComputeOverlayDamageStats(OverlayData data, bool dps, bool reset, int mode,
      int maxRows, string selectedClass, HashSet<long> deadFights)
    {
      CombinedStats combined = null;

      if (reset)
      {
        data.DeadTotalDamage = 0;
        data.UpdateTime = 0;
        data.PetOwners = new Dictionary<string, string>();
        data.DeadPlayerTotals = new Dictionary<string, OverlayPlayerTotal>();
        data.TimeSegments = new TimeRange();
        data.FightName = null;
        data.DeadFightCount = 0;
      }

      var allDamage = data.DeadTotalDamage;
      var allTime = data.TimeSegments.TimeSegments.Count > 0 ? new TimeRange(data.TimeSegments.TimeSegments) : new TimeRange();
      var playerTotals = new Dictionary<string, OverlayPlayerTotal>();
      var playerHasPet = new Dictionary<string, bool>();
      var fightCount = data.DeadFightCount;

      if (dps)
      {
        // check incase pet mappings was updated while overlay is running
        foreach (var keypair in data.PetOwners)
        {
          UpdateOverlayHasPet(keypair.Key, keypair.Value, playerHasPet, data.DeadPlayerTotals);
        }
      }

      // copy values from dead fights
      foreach (var keypair in data.DeadPlayerTotals)
      {
        playerTotals[keypair.Key] = new OverlayPlayerTotal
        {
          Damage = keypair.Value.Damage,
          UpdateTime = keypair.Value.UpdateTime,
          Name = keypair.Value.Name,
          Range = new TimeRange(keypair.Value.Range.TimeSegments)
        };
      }

      var oldestTime = data.UpdateTime;
      Fight oldestFight = null;

      foreach (var fightinfo in DataManager.Instance.GetOverlayFights())
      {
        var fight = fightinfo.Value;
        fightCount++;

        if (!fight.Dead || mode > 0)
        {
          var theTotals = dps ? fight.PlayerDamageTotals : fight.PlayerTankTotals;
          foreach (var keypair in theTotals)
          {
            var player = dps ? UpdateOverlayHasPet(keypair.Key, keypair.Value.PetOwner, playerHasPet, playerTotals) : keypair.Key;

            // save current state and remove dead fight at the end
            if (fight.Dead)
            {
              data.DeadTotalDamage += keypair.Value.Damage;
              data.TimeSegments.Add(new TimeSegment(keypair.Value.BeginTime, keypair.Value.UpdateTime));
            }

            // always update so +Pets can be added before fight is dead
            if (dps)
            {
              data.PetOwners[player] = keypair.Value.PetOwner;
            }

            allDamage += keypair.Value.Damage;
            allTime.Add(new TimeSegment(keypair.Value.BeginTime, keypair.Value.UpdateTime));

            if (data.UpdateTime == 0)
            {
              data.UpdateTime = keypair.Value.UpdateTime;
              oldestTime = keypair.Value.UpdateTime;
              oldestFight = fight;
            }
            else
            {
              data.UpdateTime = Math.Max(data.UpdateTime, keypair.Value.UpdateTime);
              if (oldestTime > keypair.Value.UpdateTime)
              {
                oldestTime = keypair.Value.UpdateTime;
                oldestFight = fight;
              }
            }

            if (fight.Dead)
            {
              UpdateOverlayPlayerTotals(player, data.DeadPlayerTotals, keypair.Value);
            }

            UpdateOverlayPlayerTotals(player, playerTotals, keypair.Value);
          }
        }

        if (fight.Dead)
        {
          deadFights.Add(fightinfo.Key);
          data.DeadFightCount++;
        }
      }

      if (data.FightName == null && oldestFight != null)
      {
        data.FightName = oldestFight.Name;
      }

      var timeout = mode == 0 ? DataManager.FIGHT_IMEOUT : mode;
      var totalSeconds = allTime.GetTotal();
      var diff = (DateTime.Now - DateTime.MinValue.AddSeconds(data.UpdateTime)).TotalSeconds;
      // added >= 0 check because this broke while testing when clocks moved an hour back in the fall
      if (data.FightName != null && totalSeconds > 0 && allDamage > 0 && diff >= 0 && diff <= timeout)
      {
        var rank = 1;
        var list = new List<PlayerStats>();
        var totalDps = (long)Math.Round(allDamage / totalSeconds, 2);
        var myIndex = -1;

        foreach (var total in playerTotals.Values.OrderByDescending(total => total.Damage))
        {
          var time = total.Range.GetTotal();
          if (time > 0 && (DateTime.Now - DateTime.MinValue.AddSeconds(total.UpdateTime)).TotalSeconds <= DataManager.MAX_TIMEOUT)
          {
            var playerStats = new PlayerStats
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

            if (myIndex == list.Count || selectedClass == Resource.ANY_CLASS || selectedClass == playerStats.ClassName)
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
        combined.TargetTitle = (fightCount > 1 ? "C(" + fightCount + "): " : "") + data.FightName;

        // these are here to support copy/paste of the parse
        combined.TimeTitle = string.Format(StatsUtil.TIME_FORMAT, combined.RaidStats.TotalSeconds);
        combined.TotalTitle = string.Format(StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(combined.RaidStats.Total),
          dps ? " Damage " : " Tanking ", StatsUtil.FormatTotals(combined.RaidStats.DPS));
      }

      return combined;
    }

    private static void UpdateOverlayPlayerTotals(string player, Dictionary<string, OverlayPlayerTotal> playerTotals, FightTotalDamage fightDmg)
    {
      if (playerTotals.TryGetValue(player, out var total))
      {
        total.Damage += fightDmg.Damage;
        total.Range.Add(new TimeSegment(fightDmg.BeginTime, fightDmg.UpdateTime));
        total.UpdateTime = Math.Max(total.UpdateTime, fightDmg.UpdateTime);
      }
      else
      {
        playerTotals[player] = new OverlayPlayerTotal
        {
          Name = player,
          Damage = fightDmg.Damage,
          Range = new TimeRange(new TimeSegment(fightDmg.BeginTime, fightDmg.UpdateTime)),
          UpdateTime = fightDmg.UpdateTime
        };
      }
    }

    private static string UpdateOverlayHasPet(string player, string petOwner, Dictionary<string, bool> playerHasPet, Dictionary<string, OverlayPlayerTotal> totals)
    {
      if (!string.IsNullOrEmpty(petOwner))
      {
        playerHasPet[petOwner] = true;

        if (totals.TryGetValue(player, out var value))
        {
          totals.Remove(player);
          totals[petOwner] = value;
          value.Name = petOwner;
        }

        player = petOwner;
      }
      else if (PlayerManager.Instance.GetPlayerFromPet(player) is { } owner && owner != Labels.UNASSIGNED)
      {
        playerHasPet[owner] = true;

        if (totals.TryGetValue(player, out var value))
        {
          totals.Remove(player);
          totals[owner] = value;
          value.Name = owner;
        }

        player = owner;
      }

      return player;
    }

    internal DamageStatsManager()
    {
      DataManager.Instance.EventsClearedActiveData += (_, _) =>
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
          FireNewStatsEvent();
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
          FireNewStatsEvent();
          Reset();

          Selected = options.Npcs.OrderBy(sel => sel.Id).ToList();
          Title = options.Npcs?.FirstOrDefault()?.Name;
          var damageBlocks = new List<ActionGroup>();

          Selected.ForEach(fight =>
          {
            damageBlocks.AddRange(fight.DamageBlocks);

            if (fight.GroupId > -1)
            {
              DamageGroupIds[fight.GroupId] = 1;
            }

            RaidTotals.Ranges.Add(new TimeSegment(fight.BeginDamageTime, fight.LastDamageTime));
            StatsUtil.UpdateRaidTimeRanges(fight.DamageSegments, fight.DamageSubSegments, PlayerTimeRanges, PlayerSubTimeRanges);
          });

          damageBlocks.Sort((a, b) => a.BeginTime.CompareTo(b.BeginTime));

          if (damageBlocks.Count > 0)
          {
            RaidTotals.TotalSeconds = RaidTotals.Ranges.GetTotal();
            RaidTotals.MaxTime = RaidTotals.TotalSeconds;

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
                  DamageGroups.Add(newBlock);
                }

                newBlock = new List<ActionGroup>();
              }

              if (!StatsUtil.DoubleEquals(lastTime, block.BeginTime))
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

              // update pet mapping
              block.Actions.ForEach(action => UpdatePetMapping(action as DamageRecord));
              lastTime = block.BeginTime;
            });

            DamageGroups.Add(newBlock);

            Parallel.ForEach(RaidTotals.Ranges.TimeSegments, segment =>
            {
              foreach (ref var block in DataManager.Instance.GetResistsDuring(segment.BeginTime, segment.EndTime).ToArray().AsSpan())
              {
                foreach (var action in block.Actions)
                {
                  if (action is ResistRecord record)
                  {
                    if (!ResistCounts.TryGetValue(record.Attacker, out var perPlayer))
                    {
                      perPlayer = new ConcurrentDictionary<string, int>();
                      ResistCounts[record.Attacker] = perPlayer;
                    }

                    if (perPlayer.TryGetValue(record.Spell, out var currentCount))
                    {
                      perPlayer[record.Spell] = currentCount + 1;
                    }
                    else
                    {
                      perPlayer[record.Spell] = 1;
                    }
                  }
                }
              }
            });

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
          Log.Error(ex);
        }
      }
    }

    private void ComputeDamageStats(GenerateStatsOptions options)
    {
      lock (DamageGroupIds)
      {
        if (RaidTotals != null)
        {
          var childrenStats = new ConcurrentDictionary<string, Dictionary<string, PlayerStats>>();
          var topLevelStats = new ConcurrentDictionary<string, PlayerStats>();
          var damageValidator = new DamageValidator();
          var individualStats = new Dictionary<string, PlayerStats>();

          // always start over
          RaidTotals.Total = 0;
          var startTime = double.NaN;
          var stopTime = double.NaN;

          try
          {
            if ((options.MaxSeconds > -1 && options.MaxSeconds < RaidTotals.MaxTime && !StatsUtil.DoubleEquals(options.MaxSeconds, RaidTotals.TotalSeconds)) ||
              (options.MinSeconds > 0 && options.MinSeconds <= RaidTotals.MaxTime && !StatsUtil.DoubleEquals(options.MinSeconds, RaidTotals.MinTime)))
            {
              var removeFromEnd = RaidTotals.MaxTime - options.MaxSeconds;
              if (removeFromEnd > 0)
              {
                var reverse = RaidTotals.Ranges.TimeSegments.ToList();
                reverse.Reverse();
                foreach (var range in reverse)
                {
                  if (range.Total >= removeFromEnd)
                  {
                    stopTime = range.EndTime - removeFromEnd;
                    break;
                  }

                  removeFromEnd -= range.Total;
                }
              }

              var removeFromStart = (double)options.MinSeconds;
              if (removeFromStart > 0)
              {
                foreach (var range in RaidTotals.Ranges.TimeSegments)
                {
                  if (range.Total >= removeFromStart)
                  {
                    startTime = range.BeginTime + removeFromStart;
                    break;
                  }

                  removeFromStart -= range.Total;
                }
              }

              var filteredGroups = new List<List<ActionGroup>>();
              AllDamageGroups.ForEach(group =>
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
              });

              DamageGroups = filteredGroups;
              RaidTotals.TotalSeconds = options.MaxSeconds - options.MinSeconds;
              RaidTotals.MinTime = options.MinSeconds;
            }
            else
            {
              DamageGroups = AllDamageGroups;
              RaidTotals.MinTime = 0;
              RaidTotals.TotalSeconds = RaidTotals.MaxTime;
            }

            var prevPlayerTimes = new Dictionary<string, double>();
            DamageGroups.ForEach(group =>
            {
              group.ForEach(block =>
              {
                block.Actions.ForEach(action =>
                {
                  if (action is DamageRecord record)
                  {
                    var isValid = damageValidator.IsValid(record);
                    var stats = StatsUtil.CreatePlayerStats(individualStats, record.Attacker);

                    if (record.Type == Labels.BANE && !isValid)
                    {
                      stats.BaneHits++;

                      if (individualStats.TryGetValue(stats.OrigName + " +Pets", out var temp))
                      {
                        temp.BaneHits++;
                      }
                    }
                    else if (isValid)
                    {
                      var isAttackerPet = PlayerManager.Instance.IsVerifiedPet(record.Attacker);
                      var isNewFrame = CheckNewFrame(prevPlayerTimes, stats.Name, block.BeginTime);

                      RaidTotals.Total += record.Total;
                      StatsUtil.UpdateStats(stats, record, isNewFrame, isAttackerPet);

                      if ((!PetToPlayer.TryGetValue(record.Attacker, out var player) && !PlayerPets.ContainsKey(record.Attacker))
                      || player == Labels.UNASSIGNED)
                      {
                        topLevelStats[record.Attacker] = stats;
                        stats.IsTopLevel = true;
                      }
                      else
                      {
                        var origName = player ?? record.Attacker;
                        var aggregateName = origName + " +Pets";
                        isNewFrame = CheckNewFrame(prevPlayerTimes, aggregateName, block.BeginTime);

                        var aggregatePlayerStats = StatsUtil.CreatePlayerStats(individualStats, aggregateName, origName);
                        StatsUtil.UpdateStats(aggregatePlayerStats, record, isNewFrame, isAttackerPet);
                        topLevelStats[aggregateName] = aggregatePlayerStats;

                        if (!childrenStats.TryGetValue(aggregateName, out _))
                        {
                          childrenStats[aggregateName] = new Dictionary<string, PlayerStats>();
                        }

                        childrenStats[aggregateName][stats.Name] = stats;
                        stats.IsTopLevel = false;
                      }

                      var subStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, record.SubType, record.Type);
                      var critHits = subStats.CritHits;
                      StatsUtil.UpdateStats(subStats, record, false, isAttackerPet);

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
            var expandedStats = new ConcurrentBag<PlayerStats>();

            Parallel.ForEach(individualStats.Values, stats =>
            {
              if (topLevelStats.ContainsKey(stats.Name))
              {
                if (childrenStats.TryGetValue(stats.Name, out var children))
                {
                  var timeRange = new TimeRange();
                  foreach (var child in children.Values)
                  {
                    if (PlayerTimeRanges.TryGetValue(child.Name, out var range))
                    {
                      StatsUtil.UpdateAllStatsTimeRanges(child, PlayerTimeRanges, PlayerSubTimeRanges, startTime, stopTime);
                      timeRange.Add(range.TimeSegments);
                    }

                    expandedStats.Add(child);
                    ResistCounts.TryGetValue(child.Name, out var childResists);
                    StatsUtil.UpdateCalculations(child, RaidTotals, childResists);

                    if (stats.Total > 0)
                    {
                      child.Percent = (float)Math.Round(Convert.ToDouble(child.Total) / stats.Total * 100, 2);
                    }

                    if (RaidTotals.Specials.TryGetValue(child.Name, out var special1))
                    {
                      child.Special = special1;
                    }
                  }

                  var filteredTimeRange = StatsUtil.FilterTimeRange(timeRange, startTime, stopTime);
                  stats.TotalSeconds = filteredTimeRange.GetTotal();
                }
                else
                {
                  expandedStats.Add(stats);
                  StatsUtil.UpdateAllStatsTimeRanges(stats, PlayerTimeRanges, PlayerSubTimeRanges, startTime, stopTime);
                }

                ResistCounts.TryGetValue(stats.Name, out var resists);
                StatsUtil.UpdateCalculations(stats, RaidTotals, resists);

                if (RaidTotals.Specials.TryGetValue(stats.OrigName, out var special2))
                {
                  stats.Special = special2;
                }
              }
            });

            var combined = new CombinedStats
            {
              RaidStats = RaidTotals,
              TargetTitle = (Selected.Count > 1 ? "Combined (" + Selected.Count + "): " : "") + Title,
              TimeTitle = string.Format(StatsUtil.TIME_FORMAT, RaidTotals.TotalSeconds),
              TotalTitle = string.Format(StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(RaidTotals.Total), " Damage ", StatsUtil.FormatTotals(RaidTotals.DPS))
            };

            combined.StatsList.AddRange(topLevelStats.Values.AsParallel().OrderByDescending(item => item.Total));
            combined.FullTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, combined.TotalTitle);
            combined.ShortTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle);
            combined.ExpandedStatsList.AddRange(expandedStats.AsParallel().OrderByDescending(item => item.Total));

            for (var i = 0; i < combined.ExpandedStatsList.Count; i++)
            {
              combined.ExpandedStatsList[i].Rank = Convert.ToUInt16(i + 1);
              if (combined.StatsList.Count > i)
              {
                combined.StatsList[i].Rank = Convert.ToUInt16(i + 1);
                combined.UniqueClasses[combined.StatsList[i].ClassName] = 1;

                if (childrenStats.TryGetValue(combined.StatsList[i].Name, out var children))
                {
                  combined.Children.Add(combined.StatsList[i].Name, children.Values.OrderByDescending(stats => stats.Total).ToList());
                }
              }
            }

            // generating new stats
            var genEvent = new StatsGenerationEvent
            {
              Type = Labels.DAMAGE_PARSE,
              State = "COMPLETED",
              CombinedStats = combined,
              Limited = damageValidator.IsDamageLimited()
            };

            genEvent.Groups.AddRange(DamageGroups);
            genEvent.UniqueGroupCount = DamageGroupIds.Count;
            EventsGenerationStatus?.Invoke(this, genEvent);

            FireChartEvent(options, "UPDATE");
          }
          catch (Exception ex)
          {
            Log.Error(ex);
          }
        }
      }

      return;

      void AddValue(Dictionary<long, int> dict, long key, int amount)
      {
        if (!dict.TryAdd(key, amount))
        {
          dict[key] += amount;
        }
      }
    }

    private static bool CheckNewFrame(Dictionary<string, double> prevPlayerTimes, string name, double beginTime)
    {
      if (!prevPlayerTimes.TryGetValue(name, out var prevTime))
      {
        prevPlayerTimes[name] = beginTime;
        prevTime = beginTime;
      }

      var newFrame = beginTime > prevTime;

      if (newFrame)
      {
        prevPlayerTimes[name] = beginTime;
      }

      return newFrame;
    }

    private void FireNewStatsEvent()
    {
      // generating new stats
      EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent { Type = Labels.DAMAGE_PARSE, State = "STARTED" });
    }

    private void FireNoDataEvent(GenerateStatsOptions options, string state)
    {
      // nothing to do
      EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent { Type = Labels.DAMAGE_PARSE, State = state });
      FireChartEvent(options, "CLEAR");
    }

    internal void FireChartEvent(GenerateStatsOptions options, string action, List<PlayerStats> selected = null)
    {
      lock (DamageGroupIds)
      {
        // reset groups
        if (options.MaxSeconds == long.MinValue && AllDamageGroups != null)
        {
          DamageGroups = AllDamageGroups;
        }

        // send update
        var de = new DataPointEvent { Action = action, Iterator = new DamageGroupCollection(DamageGroups) };

        if (selected != null)
        {
          de.Selected.AddRange(selected);
        }

        EventsUpdateDataPoint?.Invoke(DamageGroups, de);
      }
    }

    private void Reset()
    {
      AllDamageGroups = DamageGroups;
      DamageGroups.Clear();
      DamageGroupIds.Clear();
      RaidTotals = StatsUtil.CreatePlayerStats(Labels.RAID_TOTALS);
      PlayerPets.Clear();
      PetToPlayer.Clear();
      ResistCounts.Clear();
      PlayerTimeRanges.Clear();
      PlayerSubTimeRanges.Clear();
      Selected = null;
      Title = "";
    }

    private void UpdatePetMapping(DamageRecord damage)
    {
      var pname = PlayerManager.Instance.GetPlayerFromPet(damage.Attacker);
      if ((!string.IsNullOrEmpty(pname) && pname != Labels.UNASSIGNED) || !string.IsNullOrEmpty(pname = damage.AttackerOwner))
      {
        if (!PlayerPets.TryGetValue(pname, out var mapping))
        {
          mapping = new ConcurrentDictionary<string, byte>();
          PlayerPets[pname] = mapping;
        }

        mapping[damage.Attacker] = 1;
        PetToPlayer[damage.Attacker] = pname;
      }
    }

    public StatsSummary BuildSummary(string type, CombinedStats currentStats, List<PlayerStats> selected,
      bool showPetLabel, bool showDps, bool showTotals, bool rankPlayers, bool showSpecial, bool showTime, string customTitle)
    {
      var list = new List<string>();

      var title = "";
      var details = "";

      if (currentStats != null && type == Labels.DAMAGE_PARSE)
      {
        if (selected?.Count > 0)
        {
          foreach (var stats in selected.OrderByDescending(item => item.Total))
          {
            var name = showPetLabel ? stats.Name : stats.Name.Replace(" +Pets", "");
            var playerFormat = rankPlayers ? string.Format(StatsUtil.PLAYER_RANK_FORMAT, stats.Rank, name) : string.Format(StatsUtil.PLAYER_FORMAT, name);
            var damageFormat = showDps ? string.Format(StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(stats.Total), "", StatsUtil.FormatTotals(stats.DPS)) :
              string.Format(StatsUtil.TOTAL_ONLY_FORMAT, StatsUtil.FormatTotals(stats.Total));
            var timeFormat = string.Format(StatsUtil.TIME_FORMAT, stats.TotalSeconds);

            var dps = playerFormat + damageFormat;

            if (showTime)
            {
              dps += " " + timeFormat;
            }

            if (showSpecial && !string.IsNullOrEmpty(stats.Special))
            {
              dps = string.Format(StatsUtil.SPECIAL_FORMAT, dps, stats.Special);
            }

            list.Add(dps);
          }
        }

        details = list.Count > 0 ? ", " + string.Join(" | ", list) : "";
        var timeTitle = showTime ? currentStats.TimeTitle : "";
        var totals = showDps ? currentStats.TotalTitle : currentStats.TotalTitle.Split(new[] { " @" }, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        title = StatsUtil.FormatTitle(customTitle ?? currentStats.TargetTitle, timeTitle, showTotals ? totals : "");
      }

      return new StatsSummary { Title = title, RankedPlayers = details };
    }

    private class OverlayData
    {
      public long DeadTotalDamage { get; set; }
      public Dictionary<string, string> PetOwners { get; set; }
      public Dictionary<string, OverlayPlayerTotal> DeadPlayerTotals { get; set; }
      public TimeRange TimeSegments { get; set; }
      public string FightName { get; set; }
      public double UpdateTime { get; set; }
      public int DeadFightCount { get; set; }
    }
  }
}

