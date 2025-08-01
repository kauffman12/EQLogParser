using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EQLogParser
{
  internal class DamageOverlayStatsBuilder
  {
    private readonly DamageOverlayData OverlayDamageData = new();
    private readonly DamageOverlayData OverlayTankData = new();

    internal DamageOverlayStatsBuilder() { }

    internal DamageOverlayStats Build(bool reset, int mode, int maxRows, string selectedClass)
    {
      var deadFights = new HashSet<long>();
      var damage = ComputeOverlayDamageStats(OverlayDamageData, true, reset, mode, maxRows, selectedClass, deadFights);
      var tank = ComputeOverlayDamageStats(OverlayTankData, false, reset, mode, maxRows, selectedClass, deadFights);

      foreach (var id in deadFights)
      {
        DataManager.Instance.RemoveOverlayFight(id);
      }

      var result = (damage == null && tank == null) ? null : new DamageOverlayStats { DamageStats = damage, TankStats = tank };

      if (result == null)
      {
        // make sure stale data is removed
        DataManager.Instance.ResetOverlayFights();
      }

      return result;
    }

    private static CombinedStats ComputeOverlayDamageStats(DamageOverlayData data, bool dps, bool reset, int mode,
      int maxRows, string selectedClass, HashSet<long> deadFights)
    {
      CombinedStats combined = null;
      var timeout = mode == 0 ? DataManager.FightTimeout : mode;
      var now = DateTime.Now;

      if (reset)
      {
        data.DeadTotalDamage = 0;
        data.UpdateTime = 0;
        data.PetOwners = [];
        data.DeadPlayerTotals = [];
        data.TimeSegments = new TimeRange();
        data.FightName = null;
        data.DeadFightCount = 0;
        // remove anything not active and also anything that is dead
        DataManager.Instance.ResetOverlayFights(true, true);
      }

      var allDamage = data.DeadTotalDamage;
      var allTime = data.TimeSegments.TimeSegments.Count > 0 ? new TimeRange(data.TimeSegments.TimeSegments) : new TimeRange();
      var playerTotals = new Dictionary<string, OverlayPlayerTotal>();
      var playerHasPet = new Dictionary<string, bool>();
      var fightCount = mode == 0 ? 0 : data.DeadFightCount;

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
        if (!fight.Dead || mode > 0)
        {
          fightCount++;
          var theTotals = dps ? fight.PlayerDamageTotals : fight.PlayerTankTotals;
          foreach (var kv in theTotals)
          {
            data.FightName = fight.Name;
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

      if (oldestFight != null)
      {
        data.FightName = oldestFight.Name;
      }

      var totalSeconds = allTime.GetTotal();
      var diff = (now - DateTime.MinValue.AddSeconds(data.UpdateTime)).TotalSeconds;
      // added >= 0 check because this broke while testing when clocks moved an hour back in the fall
      if (data.FightName != null && totalSeconds > 0 && allDamage > 0 && diff >= 0 && diff <= timeout)
      {
        var rank = 1;
        var totalDps = (long)Math.Round(allDamage / totalSeconds, 2);
        var myIndex = -1;
        var playerName = ConfigUtil.PlayerName.Trim();
        List<PlayerStats> list = [];

        foreach (var total in playerTotals.Values.OrderByDescending(total => total.Damage))
        {
          var time = total.Range.GetTotal();
          if (time > 0 && (now - DateTime.MinValue.AddSeconds(total.UpdateTime)).TotalSeconds <= DataManager.MaxTimeout)
          {
            var playerStats = new PlayerStats
            {
              Name = playerHasPet.ContainsKey(total.Name) ? $"{total.Name} +Pets" : total.Name,
              Total = total.Damage,
              Dps = (long)Math.Round(total.Damage / time, 2),
              TotalSeconds = time,
              Rank = (ushort)rank++,
              ClassName = PlayerManager.Instance.GetPlayerClass(total.Name),
              OrigName = total.Name
            };

            if (playerStats.Name.StartsWith(playerName, StringComparison.Ordinal))
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
          list = [.. list.Take(maxRows - 1)];
          list.Add(me);
        }
        else
        {
          list = [.. list.Take(maxRows)];
        }

        combined = new CombinedStats();
        combined.StatsList.AddRange(list);
        combined.RaidStats = new PlayerStats { Total = allDamage, Dps = totalDps, TotalSeconds = totalSeconds };
        combined.TargetTitle = (fightCount > 1 ? "C(" + fightCount + "): " : "") + data.FightName;

        // these are here to support copy/paste of the parse
        combined.TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TimeFormat, combined.RaidStats.TotalSeconds);
        combined.TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TotalFormat, StatsUtil.FormatTotals(combined.RaidStats.Total),
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

    private class DamageOverlayData
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
