using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  class StatsBuilder
  {
    public const string DETAILS_FORMAT = "{0} in {1}s, {2} @ {3} DPS";
    private const string RAID_PLAYER = "Totals";

    internal static CombinedStats BuildTotalStats(List<NonPlayer> selected)
    {
      CombinedStats combined = new CombinedStats() { NpcIDs = new SortedSet<long>() };
      List<PlayerStats> dpsList = new List<PlayerStats>();
      Dictionary<string, PlayerStats> totalDamage = new Dictionary<string, PlayerStats>();
      PlayerStats raidTotals = new PlayerStats()
      {
        Name = RAID_PLAYER, Damage = 0, DPS = 0,
        BeginTimes = new Dictionary<int, DateTime>(), LastTimes = new Dictionary<int, DateTime>(), TimeDiffs = new Dictionary<int, double>()
      };

      string title = null;
      foreach (NonPlayer npc in selected)
      {
        if (npc.BeginTimeString == NonPlayer.BREAK_TIME)
        {
          continue;
        }

        combined.NpcIDs.Add(npc.ID);
        title = (title == null) ? npc.Name : title;

        foreach (string key in npc.DamageMap.Keys)
        {
          if (DataManager.Instance.IsProbablyNotAPlayer(key))
          {
            continue;
          }

          PlayerStats playerTotals;
          DamageStats npcStats = npc.DamageMap[key];

          // update player based on their pet if needed
          string player;
          string details;

          // see if there's a pet mapping, check this first
          if ((player = DataManager.Instance.GetPlayerFromPet(key)) != null)
          {
            details = "+" + key;
          }
          else if (npcStats.Owner != "" && npcStats.IsPet)
          {
            details = "+" + key;
            player = npcStats.Owner;
          }
          else if (npcStats.Owner == "" && npcStats.IsPet)
          {
            player = key;
            details = "Unassigned pet";
          }
          else
          {
            player = key;
            details = "";
          }

          if (!totalDamage.ContainsKey(player))
          {
            playerTotals = new PlayerStats()
            {
              Name = player, Damage = 0, DPS = 0, Details = details,
              BeginTimes = new Dictionary<int, DateTime>(), LastTimes = new Dictionary<int, DateTime>(), TimeDiffs = new Dictionary<int, double>()
            };
            totalDamage.Add(player, playerTotals);
            dpsList.Add(playerTotals);
          }
          else
          {
            playerTotals = totalDamage[player];
            if (details.Length > 0)
            {
              if (playerTotals.Details.Length == 0)
              {
                playerTotals.Details = details;
              }
              else if (!playerTotals.Details.Contains(details))
              {
                playerTotals.Details += ", " + details;
              }
            }
          }

          UpdateTotals(playerTotals, npcStats, npc.FightID);
          UpdateTotals(raidTotals, npcStats, npc.FightID);
        }
      }

      combined.RaidStats = raidTotals;
      combined.TimeDiff = raidTotals.TimeDiffs.Values.Sum();
      combined.Title = (selected.Count > 1 ? "Combined (" + selected.Count + "): " : "") + 
        String.Format(DETAILS_FORMAT, title, raidTotals.TimeDiffs.Values.Sum(), Utils.FormatDamage(raidTotals.Damage), Utils.FormatDamage(raidTotals.DPS));
      combined.StatsList = dpsList.OrderByDescending(item => item.Damage).ToList();

      for (int i=0; i<combined.StatsList.Count; i++)
      {
        combined.StatsList[i].Rank = i + 1;
      }

      return combined;
    }

    internal static string GetSummary(CombinedStats currentStats, List<PlayerStats> selected)
    {
      string result = "";
      List<string> list = new List<string>();

      if (selected != null && currentStats != null)
      {
        int count = 0;
        foreach (PlayerStats stats in selected.OrderByDescending(item => item.Damage))
        {
          count++;
          list.Add(String.Format("{0}. {1} = {2} @ {3} DPS", count, stats.Name, Utils.FormatDamage(stats.Damage), Utils.FormatDamage(stats.DPS)));
        }

        result = currentStats.Title + ", " + string.Join(", ", list);
      }

      return result;
    }

    private static void UpdateTotals(PlayerStats playerTotals, DamageStats npcStats, int FightID)
    {
      if (!playerTotals.BeginTimes.ContainsKey(FightID))
      {
        playerTotals.BeginTimes[FightID] = new DateTime();
        playerTotals.LastTimes[FightID] = new DateTime();
        playerTotals.TimeDiffs[FightID] = 0;
      }

      playerTotals.Damage += npcStats.Damage;
      playerTotals.Hits += npcStats.Hits;
      playerTotals.Max = (playerTotals.Max < npcStats.Max) ? npcStats.Max : playerTotals.Max;

      bool updateTime = false;
      if (playerTotals.BeginTimes[FightID] == DateTime.MinValue || playerTotals.BeginTimes[FightID] > npcStats.BeginTime)
      {
        playerTotals.BeginTimes[FightID] = npcStats.BeginTime;
        updateTime = true;
      }

      if (playerTotals.LastTimes[FightID] == DateTime.MinValue || playerTotals.LastTimes[FightID] < npcStats.LastTime)
      {
        playerTotals.LastTimes[FightID] = npcStats.LastTime;
        updateTime = true;
      }

      if (updateTime)
      {
        playerTotals.TimeDiffs[FightID] = playerTotals.LastTimes[FightID].Subtract(playerTotals.BeginTimes[FightID]).TotalSeconds;
        if (playerTotals.TimeDiffs[FightID] <= 0)
        {
          playerTotals.TimeDiffs[FightID] = 1;
        }
      }

      playerTotals.TotalSeconds = playerTotals.TimeDiffs.Values.Sum();
      playerTotals.DPS = (long) Math.Round(playerTotals.Damage / playerTotals.TotalSeconds);
      playerTotals.Avg = (long) Math.Round(Convert.ToDecimal(playerTotals.Damage) / playerTotals.Hits);
    }
  }
}
