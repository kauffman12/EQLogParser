using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  class DamageStatsBuilder : StatsBuilder
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal static List<List<TimedAction>> DamageGroups = new List<List<TimedAction>>();
    internal static Dictionary<string, byte> NpcNames = new Dictionary<string, byte>();
    internal static bool IsBaneAvailable = false;

    private static ConcurrentDictionary<string, byte> PlayerHasPet = new ConcurrentDictionary<string, byte>();
    private static ConcurrentDictionary<string, string> PetToPlayer = new ConcurrentDictionary<string, string>();
    private static List<TimedAction> Resists = new List<TimedAction>();
    private static List<NonPlayer> Selected;
    private static string Title;

    private static DictionaryAddHelper<long, int> LongIntAddHelper = new DictionaryAddHelper<long, int>();

    internal static StatsSummary BuildSummary(CombinedStats currentStats, List<PlayerStats> selected, bool showTotals, bool rankPlayers)
    {
      List<string> list = new List<string>();

      string title = "";
      string details = "";
      string shortTitle = "";

      if (currentStats != null)
      {
        if (selected != null)
        {
          foreach (PlayerStats stats in selected.OrderByDescending(item => item.Total))
          {
            string playerFormat = rankPlayers ? string.Format(PLAYER_RANK_FORMAT, stats.Rank, stats.Name) : string.Format(PLAYER_FORMAT, stats.Name);
            string damageFormat = string.Format(TOTAL_FORMAT, Helpers.FormatDamage(stats.Total), "", Helpers.FormatDamage(stats.DPS));
            string timeFormat = string.Format(TIME_FORMAT, stats.TotalSeconds);
            list.Add(playerFormat + damageFormat + " " + timeFormat);
          }
        }

        details = list.Count > 0 ? ", " + string.Join(", ", list) : "";
        title = BuildTitle(currentStats, showTotals);
        shortTitle = BuildTitle(currentStats, false);
      }

      return new StatsSummary() { Title = title, RankedPlayers = details, ShortTitle = shortTitle };
    }

    internal static CombinedDamageStats BuildTotalStats(string title, List<NonPlayer> selected, bool showBane)
    {
      CombinedDamageStats combined = null;
      Selected = selected;
      Title = title;

      try
      {
        PlayerStats raidTotals = CreatePlayerStats(RAID_PLAYER);

        DamageGroups.Clear();
        NpcNames.Clear();
        PlayerHasPet.Clear();
        PetToPlayer.Clear();
        Resists.Clear();

        selected.ForEach(npc =>
        {
          UpdateTimeDiffs(raidTotals, npc);
          NpcNames[npc.Name] = 1;
        });

        raidTotals.TotalSeconds = raidTotals.TimeDiffs.Sum();

        if (raidTotals.BeginTimes.Count > 0 && raidTotals.BeginTimes.Count == raidTotals.LastTimes.Count)
        {

          for (int i = 0; i < raidTotals.BeginTimes.Count; i++)
          {
            DamageGroups.Add(DataManager.Instance.GetDamageDuring(raidTotals.BeginTimes[i], raidTotals.LastTimes[i]));
            Resists.AddRange(DataManager.Instance.GetResistsDuring(raidTotals.BeginTimes[i], raidTotals.LastTimes[i]));
          }

          // send update
          DataPointEvent de = new DataPointEvent() { EventType = "UPDATE", NpcNames = NpcNames, ShowBane = false };
          EventsUpdateDataPoint?.Invoke(DamageGroups, de);

          Parallel.ForEach(DamageGroups, records =>
          {
            // look for pets that need to be combined first
            Parallel.ForEach(records, timedAction =>
            {
              DamageRecord record = timedAction as DamageRecord;
              // see if there's a pet mapping, check this first
              string pname = DataManager.Instance.GetPlayerFromPet(record.Attacker);
              if (pname != null || (record.AttackerOwner != "" && (pname = record.AttackerOwner) != ""))
              {
                PlayerHasPet[pname] = 1;
                PetToPlayer[record.Attacker] = pname;
              }
            });
          });
        }
        else
        {
          // send update
          DataPointEvent de = new DataPointEvent() { EventType = "UPDATE" };
          EventsUpdateDataPoint?.Invoke(DamageGroups, de);
        }

        combined = ComputeDamageStats(raidTotals, showBane);
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
      }

      return combined;
    }

    internal static CombinedDamageStats ComputeDamageStats(PlayerStats raidTotals, bool showBane = false)
    {
      ConcurrentDictionary<string, Dictionary<string, PlayerStats>> childrenStats = new ConcurrentDictionary<string, Dictionary<string, PlayerStats>>();
      ConcurrentDictionary<string, PlayerStats> topLevelStats = new ConcurrentDictionary<string, PlayerStats>();
      ConcurrentDictionary<string, PlayerStats> aggregateStats = new ConcurrentDictionary<string, PlayerStats>();
      Dictionary<string, PlayerStats> individualStats = new Dictionary<string, PlayerStats>();

      // always start over
      raidTotals.Total = 0;
      IsBaneAvailable = false;

      // send update
      DataPointEvent de = new DataPointEvent() { EventType = "UPDATE", NpcNames = NpcNames, ShowBane = showBane };
      EventsUpdateDataPoint?.Invoke(DamageGroups, de);

      DamageGroups.ForEach(records =>
      {
        // keep track of time range as well as the players that have been updated
        Dictionary<string, PlayerSubStats> allStats = new Dictionary<string, PlayerSubStats>();

        records.ForEach(timedAction =>
        {
          DamageRecord record = timedAction as DamageRecord;
          if (NpcNames.ContainsKey(record.Defender) && !DataManager.Instance.IsProbablyNotAPlayer(record.Attacker))
          {
            if (record.Type == Labels.BANE_NAME)
            {
              IsBaneAvailable = true;
            }

            if (showBane || record.Type != Labels.BANE_NAME)
            {
              raidTotals.Total += record.Total;
            }

            PlayerStats stats = CreatePlayerStats(individualStats, record.Attacker);
            UpdateStats(stats, record, showBane);
            allStats[record.Attacker] = stats;

            string player = null;
            if (!PetToPlayer.TryGetValue(record.Attacker, out player) && !PlayerHasPet.ContainsKey(record.Attacker))
            {
              // not a pet
              topLevelStats[record.Attacker] = stats;
            }
            else
            {
              string origName = (player != null) ? player : record.Attacker;
              string aggregateName = (player == DataManager.UNASSIGNED_PET_OWNER) ? origName : origName + " +Pets";

              PlayerStats aggregatePlayerStats = CreatePlayerStats(individualStats, aggregateName, origName);
              UpdateStats(aggregatePlayerStats, record, showBane);
              allStats[aggregateName] = aggregatePlayerStats;
              topLevelStats[aggregateName] = aggregatePlayerStats;

              Dictionary<string, PlayerStats> children;
              if (!childrenStats.TryGetValue(aggregateName, out children))
              {
                childrenStats[aggregateName] = new Dictionary<string, PlayerStats>();
              }

              childrenStats[aggregateName][stats.Name] = stats;
            }

            PlayerSubStats subStats = CreatePlayerSubStats(stats.SubStats, record.SubType, record.Type);
            UpdateSubStats(subStats, record);
            allStats[stats.Name + "=" + record.SubType] = subStats;
          }
        });

        Parallel.ForEach(allStats.Values, stats =>
        {
          stats.TotalSeconds += stats.LastTime - stats.BeginTime + 1;
          stats.BeginTime = double.NaN;
        });
      });

      raidTotals.DPS = (long) Math.Round(raidTotals.Total / raidTotals.TotalSeconds, 2);

      // add up resists
      Dictionary<string, uint> resistCounts = new Dictionary<string, uint>();
      Parallel.ForEach(Resists, resist =>
      {
        ResistRecord record = resist as ResistRecord;
        StringIntAddHelper.Add(resistCounts, record.Spell, 1);
      });

      // get death counts
      ConcurrentDictionary<string, uint> deathCounts = GetPlayerDeaths(raidTotals);

      Parallel.ForEach(individualStats.Values, stats =>
      {
        PlayerStats topLevel;
        if (topLevelStats.TryGetValue(stats.Name, out topLevel))
        {
          uint totalDeaths = 0;
          Dictionary<string, PlayerStats> children;
          if (childrenStats.TryGetValue(stats.Name, out children))
          {
            foreach (var child in children.Values)
            {
              UpdateCalculations(child, raidTotals, resistCounts);

              if (stats.Total > 0)
              {
                child.Percent = Math.Round(Convert.ToDouble(child.Total) / stats.Total * 100, 2);
              }

              uint count;
              if (deathCounts.TryGetValue(child.Name, out count))
              {
                child.Deaths = count;
                totalDeaths += child.Deaths;
              }
            }
          }

          UpdateCalculations(stats, raidTotals, resistCounts);
          stats.Deaths = totalDeaths;
        }
        else if (!PetToPlayer.ContainsKey(stats.Name))
        {
          UpdateCalculations(stats, raidTotals, resistCounts);

          uint count;
          if (deathCounts.TryGetValue(stats.Name, out count))
          {
            stats.Deaths = count;
          }
        }
      });

      CombinedDamageStats combined = new CombinedDamageStats();
      combined.RaidStats = raidTotals;
      combined.UniqueClasses = new Dictionary<string, byte>();
      combined.Children = new Dictionary<string, List<PlayerStats>>();
      combined.StatsList = topLevelStats.Values.AsParallel().OrderByDescending(item => item.Total).ToList();
      combined.TargetTitle = (Selected.Count > 1 ? "Combined (" + Selected.Count + "): " : "") + Title;
      combined.TimeTitle = string.Format(TIME_FORMAT, raidTotals.TotalSeconds);
      combined.TotalTitle = string.Format(TOTAL_FORMAT, Helpers.FormatDamage(raidTotals.Total), " Damage ", Helpers.FormatDamage(raidTotals.DPS));

      for (int i = 0; i < combined.StatsList.Count; i++)
      {
        combined.StatsList[i].Rank = Convert.ToUInt16(i + 1);
        combined.UniqueClasses[combined.StatsList[i].ClassName] = 1;

        Dictionary<string, PlayerStats> children;
        if (childrenStats.TryGetValue(combined.StatsList[i].Name, out children))
        {
          combined.Children.Add(combined.StatsList[i].Name, children.Values.OrderByDescending(stats => stats.Total).ToList());
        }
      }

      return combined;
    }

    internal static Dictionary<string, List<HitFreqChartData>> GetHitFreqValues(CombinedDamageStats combined, PlayerStats selected)
    {
      Dictionary<string, List<HitFreqChartData>> results = new Dictionary<string, List<HitFreqChartData>>();

      // get chart data for player and pets if available
      List<PlayerStats> list = new List<PlayerStats>();
      if (combined.Children.ContainsKey(selected.Name))
      {
        list.AddRange(combined.Children[selected.Name]);
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
          chartData.NonCritXValues = nonCritDamages;
          results[stat.Name].Add(chartData);
        }
      });

      return results;
    }

    private static void UpdateSubStats(PlayerSubStats subStats, DamageRecord record)
    {
      uint critHits = subStats.CritHits;
      UpdateStats(subStats, record, true);

      Dictionary<long, int> values = subStats.CritHits > critHits ? subStats.CritFreqValues : subStats.NonCritFreqValues;
      LongIntAddHelper.Add(values, record.Total, 1);
    }
  }
}

