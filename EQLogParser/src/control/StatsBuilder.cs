using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace EQLogParser
{
  class StatsBuilder
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const string RAID_PLAYER = "Totals";
    public const string TIME_FORMAT = "in {0}s";
    public const string DAMAGE_FORMAT = "{0} @{1}";
    private const string PLAYER_FORMAT = "{0} = ";
    private const string PLAYER_RANK_FORMAT = "{0}. {1} = ";
    private static DictionaryAddHelper<long, int> LongIntAddHelper = new DictionaryAddHelper<long, int>();

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

    internal static StatsSummary BuildSummary(CombinedStats currentStats, List<PlayerStats> selected, bool showTotals, bool rankPlayers)
    {
      List<string> list = new List<string>();

      string title = "";
      string details = "";

      if (selected != null && currentStats != null)
      {
        foreach (PlayerStats stats in selected.OrderByDescending(item => item.TotalDamage))
        {
          string playerFormat = rankPlayers ? String.Format(PLAYER_RANK_FORMAT, stats.Rank, stats.Name) : String.Format(PLAYER_FORMAT, stats.Name);
          string damageFormat = String.Format(DAMAGE_FORMAT, Helpers.FormatDamage(stats.TotalDamage), Helpers.FormatDamage(stats.DPS));
          string timeFormat = String.Format(TIME_FORMAT, stats.TotalSeconds);
          list.Add(playerFormat + damageFormat + " " + timeFormat);
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

        foreach (NonPlayer npc in selected.OrderBy(item => item.ID))
        {
          if (npc.BeginTimeString == NonPlayer.BREAK_TIME)
          {
            continue;
          }

          combined.NpcIDs.Add(npc.ID);
          foreach (var key in npc.DamageMap.Keys)
          {
            if (!DataManager.Instance.IsProbablyNotAPlayer(key))
            {
              PlayerStats playerTotals;
              DamageStats npcStats = npc.DamageMap[key];

              if (!individualStats.ContainsKey(key))
              {
                playerTotals = CreatePlayerStats(key);
                individualStats[key] = playerTotals;
                if (playerTotals.ClassName != "")
                {
                  lock (uniqueClasses)
                  {
                    uniqueClasses[playerTotals.ClassName] = 1;
                  }
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
              UpdateTotals(playerTotals, npcStats);
              UpdateTotals(raidTotals, npcStats);
            }
          }
        }

        combined.RaidStats = raidTotals;
        combined.TimeDiff = raidTotals.TimeDiffs.Sum();
        combined.TargetTitle = (selected.Count > 1 ? "Combined (" + selected.Count + "): " : "") + title;
        combined.TimeTitle = String.Format(TIME_FORMAT, combined.TimeDiff);
        combined.DamageTitle = String.Format(DAMAGE_FORMAT, Helpers.FormatDamage(raidTotals.TotalDamage), Helpers.FormatDamage(raidTotals.DPS));
        combined.UniqueClasses = uniqueClasses;

        // save them all before child code removes
        var allStatValues = individualStats.Values.ToList();

        combined.Children = new Dictionary<string, List<PlayerStats>>();
        if (needAggregate.Count > 0)
        {
          Parallel.ForEach(needAggregate.Keys, (key) =>
          {
            string aggregateName = (key == DataManager.UNASSIGNED_PET_OWNER) ? key : key + " +Pets";
            PlayerStats aggregatePlayerStats = CreatePlayerStats(aggregateName, key);
            List<string> all = needAggregate[key].ToList();
            all.Add(key);

            List<DamageStats> allDamageStats = new List<DamageStats>();
            foreach (string child in all)
            {
              if (aggregateNpcStats.ContainsKey(child) && individualStats.ContainsKey(child))
              {
                statsHelper.AddToList(combined.Children, aggregateName, individualStats[child]);

                PlayerStats removed;
                individualStats.TryRemove(child, out removed);

                foreach (NonPlayer npc in aggregateNpcStats[child])
                {
                  allDamageStats.Add(npc.DamageMap[child]);
                }
              }
            }

            allDamageStats.OrderBy(dStats => dStats.BeginTime).ToList().ForEach(dStats =>
            {
              UpdateTotals(aggregatePlayerStats, dStats);
            });

            individualStats[aggregateName] = aggregatePlayerStats;

            // figure out percents
            foreach (PlayerStats childStat in combined.Children[aggregateName])
            {
              childStat.Percent = Math.Round(((decimal)childStat.TotalDamage / aggregatePlayerStats.TotalDamage) * 100, 2);
              childStat.PercentString = childStat.Percent.ToString();
            }
          });
        }

        combined.SubStats = new Dictionary<string, List<PlayerSubStats>>();
        Parallel.ForEach(allStatValues, (stat) =>
        {
          List<PlayerSubStats> subStatsList = stat.SubStats.Values.OrderByDescending(item => item.TotalDamage).ToList();
          foreach (var subStat in subStatsList)
          {
            subStat.Percent = Math.Round(stat.Percent / 100 * ((decimal)subStat.TotalDamage / stat.TotalDamage) * 100, 2);
            subStat.PercentString = subStat.Percent.ToString();
          }

          lock (combined.SubStats)
          {
            combined.SubStats[stat.Name] = subStatsList;
          }
        });

        combined.StatsList = individualStats.Values.OrderByDescending(item => item.TotalDamage).ToList();

        Parallel.ForEach(combined.StatsList, (stat, state, index) =>
        {
          stat.Rank = (int)index + 1;
          if (combined.Children.ContainsKey(stat.Name))
          {
            combined.Children[stat.Name] = combined.Children[stat.Name].OrderByDescending(item => item.TotalDamage).ToList();
          }
        });
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      return combined;
    }

    internal static Dictionary<string, List<HitFreqChartData>> GetHitFreqValues(CombinedStats combined, PlayerStats playerStats)
    {
      Dictionary<string, List<HitFreqChartData>> results = new Dictionary<string, List<HitFreqChartData>>();

      // get chart data for player and pets if available
      List<PlayerStats> list = new List<PlayerStats>();
      if (combined.Children.ContainsKey(playerStats.Name))
      {
        list.AddRange(combined.Children[playerStats.Name]);
      }
      else
      {
        list.Add(playerStats);
      }

      list.ForEach(stat =>
      {
        results[stat.Name] = new List<HitFreqChartData>();
        foreach (string type in stat.SubStats.Keys)
        {
          List<int> critFreqs = new List<int>();
          List<int> nonCritFreqs = new List<int>();
          HitFreqChartData chartData = new HitFreqChartData() { HitType = type };

          // add crits
          var critDamages = stat.SubStats[type].CritFreqValues.Keys.OrderBy(key => key).ToList();
          critDamages.ForEach(damage => critFreqs.Add(stat.SubStats[type].CritFreqValues[damage]));
          chartData.CritYValues = critFreqs;
          chartData.CritXValues = critDamages;

          // add non crits
          var nonCritDamages = stat.SubStats[type].NonCritFreqValues.Keys.OrderBy(key => key).ToList();
          nonCritDamages.ForEach(damage => nonCritFreqs.Add(stat.SubStats[type].NonCritFreqValues[damage]));
          chartData.NonCritYValues = nonCritFreqs;
          chartData.NonCritXValues= nonCritDamages;

          results[stat.Name].Add(chartData);
        }
      });

      return results;
    }

    internal static ChartData GetDPSValues(CombinedStats combined, List<PlayerStats> stats, NpcDamageManager damageManager)
    {
      // sort stats
      stats = stats.OrderBy(item => item.Name).ToList();
      int interval = combined.TimeDiff < 12 ? 1 : (int)combined.TimeDiff / 12;

      List<string> labels = new List<string>();
      DictionaryAddHelper<string, long> addHelper = new DictionaryAddHelper<string, long>();
      Dictionary<string, List<long>> playerValues = new Dictionary<string, List<long>>();

      foreach (var timeIndex in Enumerable.Range(0, combined.RaidStats.BeginTimes.Count))
      {
        DateTime firstTime = combined.RaidStats.BeginTimes[timeIndex];
        DateTime lastTime = combined.RaidStats.LastTimes[timeIndex];
        DateTime currentTime = firstTime;

        int index = 0;
        var damages = damageManager.GetDamageStartingAt(firstTime);

        Dictionary<string, long> playerTotals = new Dictionary<string, long>();
        labels.Add(Helpers.FormatDateTime(firstTime));
        while (currentTime <= lastTime)
        {
          while (index < damages.Count && damages[index].CurrentTime <= currentTime)
          {
            Dictionary<string, long> playerDamage = damages[index].PlayerDamage;
            foreach (var stat in stats)
            {
              if (combined.Children.ContainsKey(stat.Name))
              {
                combined.Children[stat.Name].ForEach(child =>
                {
                  if (playerDamage.ContainsKey(child.Name))
                  {
                    addHelper.Add(playerTotals, stat.Name, playerDamage[child.Name]);
                  }
                });
              }
              else if (playerDamage.ContainsKey(stat.Name))
              {
                addHelper.Add(playerTotals, stat.Name, playerDamage[stat.Name]);
              }
            }

            index++;
          }

          foreach (var stat in stats)
          {
            if (!playerValues.ContainsKey(stat.Name))
            {
              playerValues[stat.Name] = new List<long>();
            }

            long dps = 0;
            if (playerTotals.ContainsKey(stat.Name))
            {
              dps = (long)Math.Round(playerTotals[stat.Name] / ((currentTime - firstTime).TotalSeconds + 1));
            }

            playerValues[stat.Name].Add(dps);
          }

          labels.Add(Helpers.FormatDateTime(currentTime));

          if (currentTime == lastTime)
          {
            break;
          }
          else
          {
            currentTime = currentTime.AddSeconds(interval);
            if (currentTime > lastTime)
            {
              currentTime = lastTime;
            }
          }
        }
      }

      // scale down if too many points
      int scale = -1;
      var firstPlayer = playerValues.Values.First();
      if (firstPlayer.Count > 20)
      {
        scale = firstPlayer.Count / 20;
      }

      if (scale > 1)
      {
        Dictionary<string, List<long>> newPlayerValues = new Dictionary<string, List<long>>();
        Parallel.ForEach(playerValues.Keys, (player) =>
        {
          var newList = playerValues[player].Where((item, i) => i % scale == 0).ToList();

          lock (newPlayerValues)
          {
            newPlayerValues[player] = newList;
          }
        });

        playerValues = newPlayerValues;
        labels = labels.Where((item, i) => i % scale == 0).ToList();
      }

      return new ChartData() { Values = playerValues, XAxisLabels = labels };
    }

    internal static List<PlayerStats> GetSelectedPlayerStatsByClass(string classString, ItemCollection items)
    {
      DataManager.SpellClasses type = (DataManager.SpellClasses)Enum.Parse(typeof(DataManager.SpellClasses), classString);
      string className = DataManager.Instance.GetClassName(type);

      List<PlayerStats> selectedStats = new List<PlayerStats>();
      foreach (var item in items)
      {
        PlayerStats stats = item as PlayerStats;
        if (stats.ClassName == className)
        {
          selectedStats.Add(stats);
        }
      }

      return selectedStats;
    }

    private static string FormatTitle(string targetTitle, string timeTitle, string damageTitle = "")
    {
      string result;
      result = targetTitle + " " + timeTitle;
      if (damageTitle != "")
      {
        result += ", " + damageTitle;
      }
      return result;
    }

    private static void UpdateTotals(PlayerStats playerTotals, DamageStats npcStats)
    {
      playerTotals.TotalDamage += npcStats.TotalDamage;
      playerTotals.TotalCritDamage += npcStats.TotalCritDamage;
      playerTotals.TotalLuckyDamage += npcStats.TotalLuckyDamage;
      playerTotals.Hits += npcStats.Count;
      playerTotals.CritHits += npcStats.CritCount;
      playerTotals.LuckyHits += npcStats.LuckyCount;
      playerTotals.TwincastHits += npcStats.TwincastCount;
      playerTotals.Max = (playerTotals.Max < npcStats.Max) ? npcStats.Max : playerTotals.Max;

      int currentIndex = playerTotals.BeginTimes.Count - 1;
      if (currentIndex == -1)
      {
        playerTotals.BeginTimes.Add(npcStats.BeginTime);
        playerTotals.LastTimes.Add(npcStats.LastTime);
        playerTotals.TimeDiffs.Add(0); // update afterward
        currentIndex = 0;
      }
      else if (playerTotals.LastTimes[currentIndex] >= npcStats.BeginTime)
      {
        if (npcStats.LastTime > playerTotals.LastTimes[currentIndex])
        {
          playerTotals.LastTimes[currentIndex] = npcStats.LastTime;
        }
      }
      else
      {
        playerTotals.BeginTimes.Add(npcStats.BeginTime);
        playerTotals.LastTimes.Add(npcStats.LastTime);
        playerTotals.TimeDiffs.Add(0); // update afterward
        currentIndex++;
      }

      playerTotals.TimeDiffs[currentIndex] = playerTotals.LastTimes[currentIndex].Subtract(playerTotals.BeginTimes[currentIndex]).TotalSeconds + 1;

      playerTotals.TotalSeconds = playerTotals.TimeDiffs.Sum();
      playerTotals.DPS = (long)Math.Round(playerTotals.TotalDamage / playerTotals.TotalSeconds);
      playerTotals.Avg = (long)Math.Round(Convert.ToDecimal(playerTotals.TotalDamage) / playerTotals.Hits);
      playerTotals.CritRate = Math.Round(Convert.ToDecimal(playerTotals.CritHits) / playerTotals.Hits * 100, 1);
      playerTotals.LuckRate = Math.Round(Convert.ToDecimal(playerTotals.LuckyHits) / playerTotals.Hits * 100, 1);
      playerTotals.TwincastRate = Math.Round(Convert.ToDecimal(playerTotals.TwincastHits) / playerTotals.Hits * 100, 1);

      if ((playerTotals.CritHits - playerTotals.LuckyHits) > 0)
      {
        playerTotals.AvgCrit = (long)Math.Round(Convert.ToDecimal(playerTotals.TotalCritDamage) / (playerTotals.CritHits - playerTotals.LuckyHits));
      }
      if (playerTotals.LuckyHits > 0)
      {
        playerTotals.AvgLucky = (long)Math.Round(Convert.ToDecimal(playerTotals.TotalLuckyDamage) / playerTotals.LuckyHits);
      }

      Parallel.ForEach(npcStats.HitMap.Keys, (key) =>
      {
        PlayerSubStats subStats = null;
        lock (playerTotals)
        {
          subStats = new PlayerSubStats() { ClassName = "", Name = "", HitType = key, CritFreqValues = new Dictionary<long, int>(), NonCritFreqValues = new Dictionary<long, int>() };
          if (!playerTotals.SubStats.ContainsKey(key))
          {
            playerTotals.SubStats[key] = subStats;
          }
          else
          {
            subStats = playerTotals.SubStats[key];
          }
        }

        Hit hitMap = npcStats.HitMap[key];
        subStats.TotalDamage += hitMap.TotalDamage;
        subStats.TotalCritDamage += hitMap.TotalCritDamage;
        subStats.TotalLuckyDamage += hitMap.TotalLuckyDamage;
        subStats.Hits += hitMap.Count;
        subStats.CritHits += hitMap.CritCount;
        subStats.LuckyHits += hitMap.LuckyCount;
        subStats.TwincastHits += hitMap.TwincastCount;
        subStats.Max = (subStats.Max < hitMap.Max) ? hitMap.Max : subStats.Max;
        subStats.TotalSeconds = playerTotals.TotalSeconds;
        subStats.DPS = (long)Math.Round(subStats.TotalDamage / subStats.TotalSeconds);
        subStats.Avg = (long)Math.Round(Convert.ToDecimal(subStats.TotalDamage) / subStats.Hits);
        subStats.CritRate = Math.Round(Convert.ToDecimal(subStats.CritHits) / subStats.Hits * 100, 1);
        subStats.LuckRate = Math.Round(Convert.ToDecimal(subStats.LuckyHits) / subStats.Hits * 100, 1);
        subStats.TwincastRate = Math.Round(Convert.ToDecimal(subStats.TwincastHits) / subStats.Hits * 100, 1);
        hitMap.CritFreqValues.Keys.ToList().ForEach(k => LongIntAddHelper.Add(subStats.CritFreqValues, k, hitMap.CritFreqValues[k]));
        hitMap.NonCritFreqValues.Keys.ToList().ForEach(k => LongIntAddHelper.Add(subStats.NonCritFreqValues, k, hitMap.NonCritFreqValues[k]));

        if ((subStats.CritHits - subStats.LuckyHits) > 0)
        {
          subStats.AvgCrit = (long)Math.Round(Convert.ToDecimal(subStats.TotalCritDamage) / (subStats.CritHits - subStats.LuckyHits));
        }
        if (subStats.LuckyHits > 0)
        {
          subStats.AvgLucky = (long)Math.Round(Convert.ToDecimal(subStats.TotalLuckyDamage) / subStats.LuckyHits);
        }
      });
    }

    private static PlayerStats CreatePlayerStats(string name, string origName = null)
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
        PercentString = "-",
        Percent = 100, // until something says otherwise
        BeginTimes = new List<DateTime>(),
        LastTimes = new List<DateTime>(),
        SubStats = new Dictionary<string, PlayerSubStats>(),
        TimeDiffs = new List<double>()
      };
    }
  }
}

