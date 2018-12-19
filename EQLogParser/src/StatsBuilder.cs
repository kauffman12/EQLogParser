using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  class StatsBuilder
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const string RAID_PLAYER = "Totals";
    public const string TIME_FORMAT = "in {0}s";
    public const string DAMAGE_FORMAT = "{0} @ {1} DPS";
    private const string PLAYER_FORMAT = "{0} = ";
    private const string PLAYER_RANK_FORMAT = "{0}. {1} = ";

    internal static string BuildTitle(CombinedStats currentStats, bool showTotals = true)
    {
      string result;
      if (showTotals)
      {
        result = FormatTitle(currentStats.TargetTitle, currentStats.TimeTitle, currentStats.DamageTitle);
      }
      else
      {
        result = FormatTitle(currentStats.TargetTitle, currentStats.TimeTitle);
      }
      return result;
    }

    internal static string FormatTitle(string targetTitle, string timeTitle, string damageTitle = "")
    {
      string result;
      result = targetTitle + " " + timeTitle;
      if (damageTitle != "")
      {
        result += ", " + damageTitle;
      }
      return result;
    }

    internal static StatsSummary BuildSummary(CombinedStats currentStats, List<PlayerStats> selected, bool showTotals, bool rankPlayers)
    {
      List<string> list = new List<string>();

      string title = "";
      string details = "";

      if (selected != null && currentStats != null)
      {
        int count = 0;
        foreach (PlayerStats stats in selected.OrderByDescending(item => item.TotalDamage))
        {
          count++;
          string playerFormat = rankPlayers ? String.Format(PLAYER_RANK_FORMAT, stats.Rank + 1, stats.Name) : String.Format(PLAYER_FORMAT, stats.Name);
          string damageFormat = String.Format(DAMAGE_FORMAT, Utils.FormatDamage(stats.TotalDamage), Utils.FormatDamage(stats.DPS));
          list.Add(playerFormat + damageFormat);
        }

        details = list.Count > 0 ? ", " + string.Join(", ", list) : ""; 
        title = BuildTitle(currentStats, showTotals);
      }

      return new StatsSummary() { Title = title, RankedPlayers = details };
    }

    internal static CombinedStats BuildTotalStats(List<NonPlayer> selected)
    {
      CombinedStats combined = new CombinedStats() { NpcIDs = new SortedSet<long>() };
      ConcurrentDictionary<string, PlayerStats> individualStats = new ConcurrentDictionary<string, PlayerStats>();
      PlayerStats raidTotals = CreatePlayerStats(RAID_PLAYER);

      Dictionary<string, List<string>> needAggregate = new Dictionary<string, List<string>>();
      Dictionary<string, List<NonPlayer>> aggregateNpcStats = new Dictionary<string, List<NonPlayer>>();
      DictionaryListHelper<string, string> needAggregateHelper = new DictionaryListHelper<string, string>();
      DictionaryListHelper<string, NonPlayer> aggregateNpcStatsHelper = new DictionaryListHelper<string, NonPlayer>();
      DictionaryListHelper<string, PlayerStats> statsHelper = new DictionaryListHelper<string, PlayerStats>();
      Dictionary<string, byte> uniqueClasses = new Dictionary<string, byte>();

      try
      {
        string title = selected.First().Name;

        foreach (NonPlayer npc in selected.OrderBy(item => item.FightID))
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
              if (playerTotals.ClassName != "")
              {
                uniqueClasses[playerTotals.ClassName] = 1;
              }
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
            }

            aggregateNpcStatsHelper.AddToList(aggregateNpcStats, key, npc);

            UpdateTotals(playerTotals, npcStats, npc.FightID);
            UpdateTotals(raidTotals, npcStats, npc.FightID);
          }
        }

        combined.RaidStats = raidTotals;
        combined.TimeDiff = raidTotals.TimeDiffs.Values.Sum();
        combined.TargetTitle = (selected.Count > 1 ? "Combined (" + selected.Count + "): " : "") + title;
        combined.TimeTitle = String.Format(TIME_FORMAT, combined.TimeDiff);
        combined.DamageTitle = String.Format(DAMAGE_FORMAT, Utils.FormatDamage(raidTotals.TotalDamage), Utils.FormatDamage(raidTotals.DPS));
        combined.UniqueClasses = uniqueClasses;

        // save them all before child code removes
        var allStatValues = individualStats.Values.ToList();

        combined.Children = new ConcurrentDictionary<string, List<PlayerStats>>();
        if (needAggregate.Count > 0)
        {
          Parallel.ForEach(needAggregate.Keys, (key) =>
          {
            string aggregateName = (key == DataManager.UNASSIGNED_PET_OWNER) ? key : key + " +Pets";
            PlayerStats aggregatePlayerStats = CreatePlayerStats(aggregateName, key);
            List<string> all = needAggregate[key].ToList();
            all.Add(key);

            foreach (string child in all)
            {
              if (aggregateNpcStats.ContainsKey(child) && individualStats.ContainsKey(child))
              {
                statsHelper.AddToList(combined.Children, aggregateName, individualStats[child]);

                PlayerStats removed;
                individualStats.TryRemove(child, out removed);

                foreach (NonPlayer npc in aggregateNpcStats[child])
                {
                  UpdateTotals(aggregatePlayerStats, npc.DamageMap[child], npc.FightID);
                }
              }
            }

            individualStats[aggregateName] = aggregatePlayerStats;

            // figure out percents
            foreach (PlayerStats childStat in combined.Children[aggregateName])
            {
              childStat.Percent = Math.Round(((decimal)childStat.TotalDamage / aggregatePlayerStats.TotalDamage) * 100, 2);
              childStat.PercentString = childStat.Percent.ToString();
            }
          });
        }

        combined.SubStats = new ConcurrentDictionary<string, List<PlayerSubStats>>();
        Parallel.ForEach(allStatValues, (stat) =>
        {
          combined.SubStats[stat.Name] = stat.SubStats.Values.OrderByDescending(item => item.TotalDamage).ToList();
          foreach (var subStat in combined.SubStats[stat.Name])
          {
            subStat.Percent = Math.Round(stat.Percent / 100 * ((decimal)subStat.TotalDamage / stat.TotalDamage) * 100, 2);
            subStat.PercentString = subStat.Percent.ToString();
          }
        });

        combined.StatsList = individualStats.Values.OrderByDescending(item => item.TotalDamage).ToList();
        for (int i = 0; i < combined.StatsList.Count; i++)
        {
          string name = combined.StatsList[i].Name;
          combined.StatsList[i].Rank = i + 1;
          if (combined.Children.ContainsKey(name))
          {
            combined.Children[name] = combined.Children[name].OrderByDescending(item => item.TotalDamage).ToList();
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

        if (playerTotals.SubStats == null)
        {
          playerTotals.SubStats = new Dictionary<string, PlayerSubStats>();
        }
      }

      playerTotals.FirstFightID = Math.Min(playerTotals.FirstFightID, FightID);
      playerTotals.LastFightID = Math.Max(playerTotals.LastFightID, FightID);

      playerTotals.TotalDamage += npcStats.TotalDamage;
      playerTotals.TotalCritDamage += npcStats.TotalCritDamage;
      playerTotals.TotalLuckyDamage += npcStats.TotalLuckyDamage;
      playerTotals.Hits += npcStats.Count;
      playerTotals.CritHits += npcStats.CritCount;
      playerTotals.LuckyHits += npcStats.LuckyCount;
      playerTotals.TwincastHits += npcStats.TwincastCount;
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
      playerTotals.DPS = (long) Math.Round(playerTotals.TotalDamage / playerTotals.TotalSeconds);
      playerTotals.Avg = (long) Math.Round(Convert.ToDecimal(playerTotals.TotalDamage) / playerTotals.Hits);
      if (playerTotals.CritHits > 0)
      {
        playerTotals.AvgCrit = (long) Math.Round(Convert.ToDecimal(playerTotals.TotalCritDamage) / playerTotals.CritHits);
      }
      if (playerTotals.LuckyHits > 0)
      {
        playerTotals.AvgLucky = (long) Math.Round(Convert.ToDecimal(playerTotals.TotalLuckyDamage) / playerTotals.LuckyHits);
      }
      playerTotals.CritRate = Math.Round(Convert.ToDecimal(playerTotals.CritHits) / playerTotals.Hits * 100, 1);
      playerTotals.LuckRate = Math.Round(Convert.ToDecimal(playerTotals.LuckyHits) / playerTotals.Hits * 100, 1);
      playerTotals.TwincastRate = Math.Round(Convert.ToDecimal(playerTotals.TwincastHits) / playerTotals.Hits * 100, 1);

      foreach (string key in npcStats.HitMap.Keys)
      {
        if (!playerTotals.SubStats.ContainsKey(key))
        {
          playerTotals.SubStats[key] = new PlayerSubStats() { ClassName = "", Name = "", HitType = key };
        }

        playerTotals.SubStats[key].TotalDamage += npcStats.HitMap[key].TotalDamage;
        playerTotals.SubStats[key].TotalCritDamage += npcStats.HitMap[key].TotalCritDamage;
        playerTotals.SubStats[key].TotalLuckyDamage += npcStats.HitMap[key].TotalLuckyDamage;
        playerTotals.SubStats[key].Hits += npcStats.HitMap[key].Count;
        playerTotals.SubStats[key].CritHits += npcStats.HitMap[key].CritCount;
        playerTotals.SubStats[key].LuckyHits += npcStats.HitMap[key].LuckyCount;
        playerTotals.SubStats[key].TwincastHits += npcStats.HitMap[key].TwincastCount;
        playerTotals.SubStats[key].Max = (playerTotals.SubStats[key].Max < npcStats.HitMap[key].Max) ? npcStats.HitMap[key].Max : playerTotals.SubStats[key].Max;
        playerTotals.SubStats[key].TotalSeconds = playerTotals.TotalSeconds;
        playerTotals.SubStats[key].DPS = (long) Math.Round(playerTotals.SubStats[key].TotalDamage / playerTotals.SubStats[key].TotalSeconds);
        playerTotals.SubStats[key].Avg = (long) Math.Round(Convert.ToDecimal(playerTotals.SubStats[key].TotalDamage) / playerTotals.SubStats[key].Hits);
        if (playerTotals.SubStats[key].CritHits > 0)
        {
          playerTotals.SubStats[key].AvgCrit = (long) Math.Round(Convert.ToDecimal(playerTotals.SubStats[key].TotalCritDamage) / playerTotals.SubStats[key].CritHits);
        }
        if (playerTotals.SubStats[key].LuckyHits > 0)
        {
          playerTotals.SubStats[key].AvgLucky = (long) Math.Round(Convert.ToDecimal(playerTotals.SubStats[key].TotalLuckyDamage) / playerTotals.SubStats[key].LuckyHits);
        }
        playerTotals.SubStats[key].CritRate = Math.Round(Convert.ToDecimal(playerTotals.SubStats[key].CritHits) / playerTotals.SubStats[key].Hits * 100, 1);
        playerTotals.SubStats[key].LuckRate = Math.Round(Convert.ToDecimal(playerTotals.SubStats[key].LuckyHits) / playerTotals.SubStats[key].Hits * 100, 1);
        playerTotals.SubStats[key].TwincastRate = Math.Round(Convert.ToDecimal(playerTotals.SubStats[key].TwincastHits) / playerTotals.SubStats[key].Hits * 100, 1);
      }
    }

    internal static PlayerStats CreatePlayerStats(string name, string origName = null)
    {
      string className = "";
      origName = origName == null ? name : origName;

      if (!DataManager.Instance.CheckNameForPet(origName))
      {
        className = DataManager.Instance.GetPlayerClass(origName);
      }

      return new PlayerStats()
      {
        Name = name,
        ClassName = className,
        HitType = "",
        TotalSeconds = 0,
        PercentString = "-",
        Percent = 100, // until something says otherwise
        BeginTimes = new Dictionary<int, DateTime>(),
        LastTimes = new Dictionary<int, DateTime>(),
        TimeDiffs = new Dictionary<int, double>(),
        FirstFightID = int.MaxValue,
        LastFightID = int.MinValue
      };
    }
  }
}
