using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  class StatsBuilder
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public const string DETAILS_FORMAT = " in {0}s, {1} @ {2} DPS";
    private const string RAID_PLAYER = "Totals";

    internal static Tuple<string, string> GetSummary(CombinedStats currentStats, List<PlayerStats> selected, bool selectedTitleOnly)
    {
      List<string> list = new List<string>();

      string title = "";
      string details = "";
      long selectedTotal = 0;

      if (selected != null && currentStats != null)
      {
        int count = 0;
        foreach (PlayerStats stats in selected.OrderByDescending(item => item.Damage))
        {
          count++;
          list.Add(String.Format("{0}. {1} = {2} @ {3} DPS", count, stats.Name, Utils.FormatDamage(stats.Damage), Utils.FormatDamage(stats.DPS)));

          if (selectedTitleOnly)
          {
            selectedTotal += stats.Damage;
          }
        }

        details = ", " + string.Join(", ", list); ;
        if (selectedTitleOnly)
        {
          long selectedDPS = (long)Math.Round(selectedTotal / currentStats.TimeDiff);
          string damageTitle = String.Format(DETAILS_FORMAT, currentStats.TimeDiff, Utils.FormatDamage(selectedTotal), Utils.FormatDamage(selectedDPS));
          title = currentStats.TargetTitle + damageTitle;
        }
        else
        {
          title = currentStats.TargetTitle + currentStats.DamageTitle;
        }
      }

      return new Tuple<string, string>(title, details);
    }

    internal static CombinedStats BuildTotalStats(List<NonPlayer> selected)
    {
      CombinedStats combined = new CombinedStats() { NpcIDs = new SortedSet<long>() };
      Dictionary<string, PlayerStats> individualStats = new Dictionary<string, PlayerStats>();
      PlayerStats raidTotals = CreatePlayerStats(RAID_PLAYER);

      Dictionary<string, List<string>> needAggregate = new Dictionary<string, List<string>>();
      Dictionary<string, List<NonPlayer>> aggregateNpcStats = new Dictionary<string, List<NonPlayer>>();
      DictionaryListHelper<string, string> needAggregateHelper = new DictionaryListHelper<string, string>();
      DictionaryListHelper<string, NonPlayer> aggregateNpcStatsHelper = new DictionaryListHelper<string, NonPlayer>();
      DictionaryListHelper<string, PlayerStats> statsHelper = new DictionaryListHelper<string, PlayerStats>();


      try
      {
        string title = selected.First().Name;
        foreach (NonPlayer npc in selected)
        {
          if (npc.BeginTimeString == NonPlayer.BREAK_TIME)
          {
            continue;
          }

          combined.NpcIDs.Add(npc.ID);
          foreach (string key in npc.DamageMap.Keys)
          {
            if (DataManager.Instance.IsProbablyNotAPlayer(key))
            {
              continue;
            }

            PlayerStats playerTotals;
            DamageStats npcStats = npc.DamageMap[key];

            if (!individualStats.ContainsKey(key))
            {
              playerTotals = CreatePlayerStats(key);
              individualStats[key] = playerTotals;
            }
            else
            {
              playerTotals = individualStats[key];
            }

            // see if there's a pet mapping, check this first
            string parent = DataManager.Instance.GetPlayerFromPet(key);
            if (parent != null)
            {
              needAggregateHelper.AddToList(needAggregate, parent, key);
            }
            else if (npcStats.Owner != "" && npcStats.IsPet)
            {
              needAggregateHelper.AddToList(needAggregate, npcStats.Owner, key);
            } else if (npcStats.Owner == "" && npcStats.IsPet)
            {
              playerTotals.Details = "Unassigned Pet";
            }

            aggregateNpcStatsHelper.AddToList(aggregateNpcStats, key, npc);

            UpdateTotals(playerTotals, npcStats, npc.FightID);
            UpdateTotals(raidTotals, npcStats, npc.FightID);
          }
        }

        combined.RaidStats = raidTotals;
        combined.TimeDiff = raidTotals.TimeDiffs.Values.Sum();
        combined.TargetTitle = (selected.Count > 1 ? "Combined (" + selected.Count + "): " : "") + title;
        combined.DamageTitle = String.Format(DETAILS_FORMAT, raidTotals.TimeDiffs.Values.Sum(), Utils.FormatDamage(raidTotals.Damage), Utils.FormatDamage(raidTotals.DPS));

        // do this before figuring out children removes values from the main stats list
        combined.SubStats = new Dictionary<string, List<PlayerSubStats>>();
        foreach (var stat in individualStats.Values)
        {
          combined.SubStats[stat.Name] = stat.SubStats.Values.OrderByDescending(item => item.Damage).ToList();
        }

        combined.Children = new Dictionary<string, List<PlayerStats>>();
        if (needAggregate.Count > 0)
        {
          foreach (string key in needAggregate.Keys)
          {
            string aggregateName = key + " +Pets";
            PlayerStats aggregatePlayerStats = CreatePlayerStats(aggregateName);
            List<string> all = needAggregate[key].ToList();
            all.Add(key);

            foreach (string child in all)
            {
              if (aggregateNpcStats.ContainsKey(child) && individualStats.ContainsKey(child))
              {
                statsHelper.AddToList(combined.Children, aggregateName, individualStats[child]);
                individualStats.Remove(child);

                foreach (NonPlayer npc in aggregateNpcStats[child])
                {
                  UpdateTotals(aggregatePlayerStats, npc.DamageMap[child], npc.FightID);
                }
              }
            }

            individualStats.Add(aggregateName, aggregatePlayerStats);
          }
        }

        combined.StatsList = individualStats.Values.OrderByDescending(item => item.Damage).ToList();
        for (int i = 0; i < combined.StatsList.Count; i++)
        {
          string name = combined.StatsList[i].Name;
          combined.StatsList[i].Rank = i + 1;
          if (combined.Children.ContainsKey(name))
          {
            combined.Children[name] = combined.Children[name].OrderByDescending(item => item.Damage).ToList();
          }
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      return combined;
    }

    internal static void UpdateTotals(PlayerStats playerTotals, DamageStats npcStats, int FightID)
    {
      if (!playerTotals.BeginTimes.ContainsKey(FightID))
      {
        playerTotals.BeginTimes[FightID] = new DateTime();
        playerTotals.LastTimes[FightID] = new DateTime();
        playerTotals.TimeDiffs[FightID] = 0;
        playerTotals.SubStats = new Dictionary<string, PlayerSubStats>();
      }

      playerTotals.Damage += npcStats.TotalDamage;
      playerTotals.Hits += npcStats.Count;
      playerTotals.CritHits += npcStats.CritCount;
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
      playerTotals.DPS = (long)Math.Round(playerTotals.Damage / playerTotals.TotalSeconds);
      playerTotals.Avg = (long)Math.Round(Convert.ToDecimal(playerTotals.Damage) / playerTotals.Hits);
      playerTotals.CritRate = Math.Round(Convert.ToDecimal(playerTotals.CritHits) / playerTotals.Hits * 100, 1);

      foreach (string key in npcStats.HitMap.Keys)
      {
        if (!playerTotals.SubStats.ContainsKey(key))
        {
          playerTotals.SubStats[key] = new PlayerSubStats() { Details = "", Name = "", HitType = key };
        }

        playerTotals.SubStats[key].Damage += npcStats.HitMap[key].TotalDamage;
        playerTotals.SubStats[key].Hits += npcStats.HitMap[key].Count;
        playerTotals.SubStats[key].CritHits += npcStats.HitMap[key].CritCount;
        playerTotals.SubStats[key].Max = (playerTotals.SubStats[key].Max < npcStats.HitMap[key].Max) ? npcStats.HitMap[key].Max : playerTotals.SubStats[key].Max;
        playerTotals.SubStats[key].TotalSeconds = playerTotals.TotalSeconds;
        playerTotals.SubStats[key].DPS = (long)Math.Round(playerTotals.SubStats[key].Damage / playerTotals.SubStats[key].TotalSeconds);
        playerTotals.SubStats[key].Avg = (long)Math.Round(Convert.ToDecimal(playerTotals.SubStats[key].Damage) / playerTotals.SubStats[key].Hits);
        playerTotals.SubStats[key].CritRate = Math.Round(Convert.ToDecimal(playerTotals.SubStats[key].CritHits) / playerTotals.SubStats[key].Hits * 100, 1);
      }
    }

    internal static PlayerStats CreatePlayerStats(string name)
    {
      return new PlayerStats()
      {
        Name = name,
        Damage = 0,
        DPS = 0,
        Details = "",
        HitType = "",
        Max = 0,
        Avg = 0,
        TotalSeconds = 0,
        Hits = 0,
        CritRate = 0,
        BeginTimes = new Dictionary<int, DateTime>(),
        LastTimes = new Dictionary<int, DateTime>(),
        TimeDiffs = new Dictionary<int, double>()
      };
    }
  }
}
