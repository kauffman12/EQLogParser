﻿using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal class DamageStatsManager : ISummaryBuilder
  {
    internal static DamageStatsManager Instance = new();
    internal event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal event Action<StatsGenerationEvent> EventsGenerationStatus;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly Dictionary<int, byte> _damageGroupIds = [];
    private readonly ConcurrentDictionary<string, TimeRange> _playerTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _playerSubTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _playerPets = new();
    private readonly ConcurrentDictionary<string, string> _petToPlayer = new();
    private List<List<ActionGroup>> _allDamageGroups;
    private List<List<ActionGroup>> _damageGroups = [];
    private PlayerStats _raidTotals;
    private List<Fight> _selected;

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
        data.PetOwners = [];
        data.DeadPlayerTotals = [];
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
        // check in case pet mappings was updated while overlay is running
        foreach (var kv in data.PetOwners)
        {
          UpdateOverlayHasPet(kv.Key, kv.Value, playerHasPet, data.DeadPlayerTotals);
        }
      }

      // copy values from dead fights
      foreach (var kv in data.DeadPlayerTotals)
      {
        playerTotals[kv.Key] = new OverlayPlayerTotal
        {
          Damage = kv.Value.Damage,
          UpdateTime = kv.Value.UpdateTime,
          Name = kv.Value.Name,
          Range = new TimeRange(kv.Value.Range.TimeSegments)
        };
      }

      var oldestTime = data.UpdateTime;
      Fight oldestFight = null;

      foreach (var (id, fight) in DataManager.Instance.GetOverlayFights())
      {
        fightCount++;

        if (!fight.Dead || mode > 0)
        {
          var theTotals = dps ? fight.PlayerDamageTotals : fight.PlayerTankTotals;
          foreach (var kv in theTotals)
          {
            var player = dps ? UpdateOverlayHasPet(kv.Key, kv.Value.PetOwner, playerHasPet, playerTotals) : kv.Key;

            // save current state and remove dead fight at the end
            if (fight.Dead)
            {
              data.DeadTotalDamage += kv.Value.Damage;
              data.TimeSegments.Add(new TimeSegment(kv.Value.BeginTime, kv.Value.UpdateTime));
            }

            // always update so +Pets can be added before fight is dead
            if (dps)
            {
              data.PetOwners[player] = kv.Value.PetOwner;
            }

            allDamage += kv.Value.Damage;
            allTime.Add(new TimeSegment(kv.Value.BeginTime, kv.Value.UpdateTime));

            if (data.UpdateTime == 0)
            {
              data.UpdateTime = kv.Value.UpdateTime;
              oldestTime = kv.Value.UpdateTime;
              oldestFight = fight;
            }
            else
            {
              data.UpdateTime = Math.Max(data.UpdateTime, kv.Value.UpdateTime);
              if (oldestTime > kv.Value.UpdateTime)
              {
                oldestTime = kv.Value.UpdateTime;
                oldestFight = fight;
              }
            }

            if (fight.Dead)
            {
              UpdateOverlayPlayerTotals(player, data.DeadPlayerTotals, kv.Value);
            }

            UpdateOverlayPlayerTotals(player, playerTotals, kv.Value);
          }
        }

        if (fight.Dead)
        {
          deadFights.Add(id);
          data.DeadFightCount++;
        }
      }

      if (data.FightName == null && oldestFight != null)
      {
        data.FightName = oldestFight.Name;
      }

      var timeout = mode == 0 ? DataManager.FightTimeout : mode;
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
          if (time > 0 && (DateTime.Now - DateTime.MinValue.AddSeconds(total.UpdateTime)).TotalSeconds <= DataManager.MaxTimeout)
          {
            var playerStats = new PlayerStats
            {
              Name = playerHasPet.ContainsKey(total.Name) ? total.Name + " +Pets" : total.Name,
              Total = total.Damage,
              Dps = (long)Math.Round(total.Damage / time, 2),
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
        combined.RaidStats = new PlayerStats { Total = allDamage, Dps = totalDps, TotalSeconds = totalSeconds };
        combined.TargetTitle = (fightCount > 1 ? "C(" + fightCount + "): " : "") + data.FightName;

        // these are here to support copy/paste of the parse
        combined.TimeTitle = string.Format(StatsUtil.TimeFormat, combined.RaidStats.TotalSeconds);
        combined.TotalTitle = string.Format(StatsUtil.TotalFormat, StatsUtil.FormatTotals(combined.RaidStats.Total),
          dps ? " Damage " : " Tanking ", StatsUtil.FormatTotals(combined.RaidStats.Dps));
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

        if (totals.Remove(player, out var value))
        {
          totals[petOwner] = value;
          value.Name = petOwner;
        }

        player = petOwner;
      }
      else if (PlayerManager.Instance.GetPlayerFromPet(player) is { } owner && owner != Labels.Unassigned)
      {
        playerHasPet[owner] = true;

        if (totals.Remove(player, out var value))
        {
          totals[owner] = value;
          value.Name = owner;
        }

        player = owner;
      }

      return player;
    }

    internal DamageStatsManager()
    {
      DataManager.Instance.EventsClearedActiveData += (_) =>
      {
        lock (_damageGroupIds)
        {
          Reset();
        }
      };
    }

    internal int GetGroupCount()
    {
      lock (_damageGroupIds)
      {
        return _damageGroups.Count;
      }
    }

    internal void RebuildTotalStats(GenerateStatsOptions options)
    {
      lock (_damageGroupIds)
      {
        if (_damageGroups.Count > 0)
        {
          FireNewStatsEvent();
          ComputeDamageStats(options);
        }
      }
    }

    internal void BuildTotalStats(GenerateStatsOptions options)
    {
      lock (_damageGroupIds)
      {
        try
        {
          FireNewStatsEvent();
          Reset();

          _selected = [.. options.Npcs.OrderBy(sel => sel.Id)];
          var damageBlocks = new List<ActionGroup>();

          foreach (var fight in CollectionsMarshal.AsSpan(_selected))
          {
            damageBlocks.AddRange(fight.DamageBlocks);

            if (fight.GroupId > -1)
            {
              _damageGroupIds[fight.GroupId] = 1;
            }

            _raidTotals.Ranges.Add(new TimeSegment(fight.BeginDamageTime, fight.LastDamageTime));
            _raidTotals.AllRanges.Add(options.AllRanges.TimeSegments);
            StatsUtil.UpdateRaidTimeRanges(fight.DamageSegments, fight.DamageSubSegments, _playerTimeRanges, _playerSubTimeRanges);
          }

          damageBlocks.Sort((a, b) => a.BeginTime.CompareTo(b.BeginTime));

          if (damageBlocks.Count != 0)
          {
            _raidTotals.TotalSeconds = _raidTotals.Ranges.GetTotal();
            _raidTotals.MaxTime = _raidTotals.TotalSeconds;

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
                  _damageGroups.Add(newBlock);
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
                newBlock.Last().Actions.AddRange(block.Actions);
              }

              // update pet mapping
              block.Actions.ForEach(action => UpdatePetMapping(action as DamageRecord));
              lastTime = block.BeginTime;
            }

            _damageGroups.Add(newBlock);
            ComputeDamageStats(options);
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

    private void ComputeDamageStats(GenerateStatsOptions options)
    {
      lock (_damageGroupIds)
      {
        if (_raidTotals != null)
        {
          var childrenStats = new ConcurrentDictionary<string, Dictionary<string, PlayerStats>>();
          var topLevelStats = new ConcurrentDictionary<string, PlayerStats>();
          var damageValidator = new DamageValidator();
          var individualStats = new Dictionary<string, PlayerStats>();

          // always start over
          _raidTotals.Total = 0;
          var startTime = double.NaN;
          var stopTime = double.NaN;

          try
          {
            if ((options.MaxSeconds > -1 && options.MaxSeconds < _raidTotals.MaxTime && !options.MaxSeconds.Equals((long)_raidTotals.TotalSeconds)) ||
              (options.MinSeconds > 0 && options.MinSeconds <= _raidTotals.MaxTime && !options.MinSeconds.Equals((long)_raidTotals.MinTime)))
            {
              var removeFromEnd = _raidTotals.MaxTime - options.MaxSeconds;
              if (removeFromEnd > 0)
              {
                var reverse = _raidTotals.Ranges.TimeSegments.ToList();
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
                foreach (var range in _raidTotals.Ranges.TimeSegments)
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
              _allDamageGroups.ForEach(group =>
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

              _damageGroups = filteredGroups;
              _raidTotals.TotalSeconds = options.MaxSeconds - options.MinSeconds;
              _raidTotals.MinTime = options.MinSeconds;
            }
            else
            {
              _damageGroups = _allDamageGroups;
              _raidTotals.MinTime = 0;
              _raidTotals.TotalSeconds = _raidTotals.MaxTime;
            }

            var prevPlayerTimes = new Dictionary<string, double>();
            foreach (var group in CollectionsMarshal.AsSpan(_damageGroups))
            {
              foreach (var block in CollectionsMarshal.AsSpan(group))
              {
                foreach (var action in block.Actions.ToArray())
                {
                  if (action is DamageRecord record)
                  {
                    var isValid = damageValidator.IsValid(record);
                    var stats = StatsUtil.CreatePlayerStats(individualStats, record.Attacker);

                    if (record.Type == Labels.Bane && !isValid)
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

                      _raidTotals.Total += record.Total;
                      StatsUtil.UpdateStats(stats, record, isNewFrame, isAttackerPet);

                      if ((!_petToPlayer.TryGetValue(record.Attacker, out var player) && !_playerPets.ContainsKey(record.Attacker))
                      || player == Labels.Unassigned)
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
                          childrenStats[aggregateName] = [];
                        }

                        childrenStats[aggregateName][stats.Name] = stats;
                        stats.IsTopLevel = false;
                      }

                      var subStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, record.SubType, record.Type);
                      var critHits = subStats.CritHits;
                      StatsUtil.UpdateStats(subStats, record, false, isAttackerPet);

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
            StatsUtil.PopulateSpecials(_raidTotals, true);
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
                    if (_playerTimeRanges.TryGetValue(child.Name, out var range))
                    {
                      StatsUtil.UpdateAllStatsTimeRanges(child, _playerTimeRanges, _playerSubTimeRanges, startTime, stopTime);
                      timeRange.Add(range.TimeSegments);
                    }

                    expandedStats.Add(child);
                    _raidTotals.ResistCounts.TryGetValue(child.Name, out var childResists);
                    StatsUtil.UpdateCalculations(child, _raidTotals, childResists);

                    if (stats.Total > 0)
                    {
                      child.Percent = (float)Math.Round(Convert.ToDouble(child.Total) / stats.Total * 100, 2);
                    }

                    if (_raidTotals.Specials.TryGetValue(child.Name, out var special1))
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
                  StatsUtil.UpdateAllStatsTimeRanges(stats, _playerTimeRanges, _playerSubTimeRanges, startTime, stopTime);
                }

                _raidTotals.ResistCounts.TryGetValue(stats.Name, out var resists);
                StatsUtil.UpdateCalculations(stats, _raidTotals, resists);

                if (_raidTotals.Specials.TryGetValue(stats.OrigName, out var special2))
                {
                  stats.Special = special2;
                }
              }
            });

            var combined = new CombinedStats
            {
              RaidStats = _raidTotals,
              TargetTitle = TextUtils.GetTitle(_selected),
              TimeTitle = string.Format(StatsUtil.TimeFormat, _raidTotals.TotalSeconds),
              TotalTitle = string.Format(StatsUtil.TotalFormat, StatsUtil.FormatTotals(_raidTotals.Total), " Damage ", StatsUtil.FormatTotals(_raidTotals.Dps))
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
                  combined.Children.Add(combined.StatsList[i].Name, [.. children.Values.OrderByDescending(stats => stats.Total)]);
                }
              }
            }

            // generating new stats
            var genEvent = new StatsGenerationEvent
            {
              Type = Labels.DamageParse,
              State = "COMPLETED",
              CombinedStats = combined,
              Limited = damageValidator.IsDamageLimited(),
              Source = this
            };

            genEvent.Groups.AddRange(_damageGroups);
            genEvent.UniqueGroupCount = _damageGroupIds.Count;
            EventsGenerationStatus?.Invoke(genEvent);

            FireChartEvent(options, "UPDATE");
          }
          catch (Exception ex)
          {
            Log.Error(ex);
          }
        }
      }

      return;

      static void AddValue(Dictionary<long, int> dict, long key, int amount)
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
      EventsGenerationStatus?.Invoke(new StatsGenerationEvent { Type = Labels.DamageParse, State = "STARTED", Source = this });
    }

    private void FireNoDataEvent(GenerateStatsOptions options, string state)
    {
      // nothing to do
      EventsGenerationStatus?.Invoke(new StatsGenerationEvent { Type = Labels.DamageParse, State = state, Source = this });
      FireChartEvent(options, "CLEAR");
    }

    internal void FireChartEvent(GenerateStatsOptions options, string action, List<PlayerStats> selected = null)
    {
      lock (_damageGroupIds)
      {
        // reset groups
        if (options.MaxSeconds == long.MinValue && _allDamageGroups != null)
        {
          _damageGroups = _allDamageGroups;
        }

        // send update
        var de = new DataPointEvent { Action = action, Iterator = new DamageGroupCollection(_damageGroups) };

        if (selected != null)
        {
          de.Selected.AddRange(selected);
        }

        EventsUpdateDataPoint?.Invoke(_damageGroups, de);
      }
    }

    private void Reset()
    {
      _allDamageGroups = _damageGroups;
      _damageGroups.Clear();
      _damageGroupIds.Clear();
      _raidTotals = StatsUtil.CreatePlayerStats(Labels.RaidTotals);
      _playerPets.Clear();
      _petToPlayer.Clear();
      _playerTimeRanges.Clear();
      _playerSubTimeRanges.Clear();
      _selected = null;
    }

    private void UpdatePetMapping(DamageRecord damage)
    {
      var petName = PlayerManager.Instance.GetPlayerFromPet(damage.Attacker);
      if ((!string.IsNullOrEmpty(petName) && petName != Labels.Unassigned) || !string.IsNullOrEmpty(petName = damage.AttackerOwner))
      {
        if (!_playerPets.TryGetValue(petName, out var mapping))
        {
          mapping = new ConcurrentDictionary<string, byte>();
          _playerPets[petName] = mapping;
        }

        mapping[damage.Attacker] = 1;
        _petToPlayer[damage.Attacker] = petName;
      }
    }

    public StatsSummary BuildSummary(string type, CombinedStats currentStats, List<PlayerStats> selected,
      bool showPetLabel, bool showDps, bool showTotals, bool rankPlayers, bool showSpecial, bool showTime, string customTitle)
    {
      var title = "";
      var details = "";
      var list = new List<string>();
      if (currentStats != null && type == Labels.DamageParse)
      {
        if (selected?.Count > 0)
        {
          foreach (var stats in selected.OrderByDescending(item => item.Total))
          {
            var name = showPetLabel ? stats.Name : stats.Name.Replace(" +Pets", "");
            var playerFormat = rankPlayers ? string.Format(StatsUtil.PlayerRankFormat, stats.Rank, name) : string.Format(StatsUtil.PlayerFormat, name);
            var damageFormat = showDps ? string.Format(StatsUtil.TotalFormat, StatsUtil.FormatTotals(stats.Total), "", StatsUtil.FormatTotals(stats.Dps)) :
              string.Format(StatsUtil.TotalOnlyFormat, StatsUtil.FormatTotals(stats.Total));
            var timeFormat = string.Format(StatsUtil.TimeFormat, stats.TotalSeconds);

            var dps = playerFormat + damageFormat;

            if (showTime)
            {
              dps += " " + timeFormat;
            }

            if (showSpecial && !string.IsNullOrEmpty(stats.Special))
            {
              dps = string.Format(StatsUtil.SpecialFormat, dps, stats.Special);
            }

            list.Add(dps);
          }
        }

        details = list.Count > 0 ? ", " + string.Join(" | ", list) : "";
        var timeTitle = showTime ? currentStats.TimeTitle : "";
        var totals = showDps ? currentStats.TotalTitle : currentStats.TotalTitle.Split([" @"], 2, StringSplitOptions.RemoveEmptyEntries)[0];
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

