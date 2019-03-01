using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  class HealStatsBuilder : StatsBuilder
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const int HEAL_OFFSET = 3; // additional # of seconds to count hilling after last damage is seen

    internal static string BuildTitle(CombinedHealStats currentStats, bool showTotals = true)
    {
      string result;
      if (showTotals)
      {
        result = FormatTitle(currentStats.TargetTitle, currentStats.TimeTitle, currentStats.TotalTitle);
      }
      else
      {
        result = FormatTitle(currentStats.TargetTitle, currentStats.TimeTitle);
      }

      return result;
    }

    internal static CombinedHealStats BuildTotalStats(string title, List<NonPlayer> selected)
    {
      CombinedHealStats combined = new CombinedHealStats();

      try
      {
        PlayerStats raidTotals = CreatePlayerStats(RAID_PLAYER);
        selected.ForEach(npc => UpdateTimeDiffs(raidTotals, npc, HEAL_OFFSET));
        raidTotals.TotalSeconds = raidTotals.TimeDiffs.AsParallel().Sum();
        combined.RaidStats = raidTotals;

       if (raidTotals.BeginTimes.Count > 0 && raidTotals.BeginTimes.Count == raidTotals.LastTimes.Count)
        {
          Dictionary<string, PlayerStats> individualStats = new Dictionary<string, PlayerStats>();

          for (int i = 0; i < raidTotals.BeginTimes.Count; i++)
          {
            // keep track of time range as well as the players that have been updated
            ConcurrentDictionary<string, PlayerStats> healerStats = new ConcurrentDictionary<string, PlayerStats>();
            var diff = (raidTotals.LastTimes[i] - raidTotals.BeginTimes[i]).TotalSeconds;
            if (diff == 0)
            {
              diff = 1;
            }

            var records = DataManager.Instance.GetHealsDuring(raidTotals.BeginTimes[i], raidTotals.LastTimes[i]);
            if (records.Count > 0)
            {
              Parallel.ForEach(records, timedAction =>
              {
                HealRecord record = timedAction as HealRecord;
                PlayerStats stats = null;

                lock (individualStats)
                {
                  if (!individualStats.ContainsKey(record.Healer))
                  {
                    stats = CreatePlayerStats(record.Healer);
                    individualStats[record.Healer] = stats;
                  }
                  else
                  {
                    stats = individualStats[record.Healer];
                  }
                }

                lock (stats)
                {
                  raidTotals.Total += record.Total;
                  stats.Total += record.Total;
                  stats.Hits += 1;
                  stats.Max = (stats.Max < record.Total) ? record.Total : stats.Max;

                  if (record.Total > 0 && record.OverHeal > 0)
                  {
                    stats.Extra += (record.OverHeal - record.Total);
                  }

                  LineModifiersParser.Parse(record, stats);
                  healerStats.TryAdd(record.Healer, stats);
                }
              });

              Parallel.ForEach(healerStats.Values.ToList(), healerStat => healerStat.TotalSeconds += diff);
            }
          }

          raidTotals.DPS = (long) Math.Round(raidTotals.Total / raidTotals.TotalSeconds, 2);
          combined.StatsList = individualStats.Values.OrderByDescending(item => item.Total).ToList();
          combined.TargetTitle = (selected.Count > 1 ? "Combined (" + selected.Count + "): " : "") + title;
          combined.TimeTitle = string.Format(TIME_FORMAT, raidTotals.TotalSeconds);
          combined.TotalTitle = string.Format(TOTAL_FORMAT, Helpers.FormatDamage(raidTotals.Total), Helpers.FormatDamage(raidTotals.DPS));

          Parallel.ForEach(combined.StatsList, stats =>
          {
            stats.DPS = (long) Math.Round(stats.Total / stats.TotalSeconds, 2);
            stats.SDPS = (long) Math.Round(stats.Total / raidTotals.TotalSeconds, 2);

            if (stats.Hits > 0)
            {
              stats.Avg = (long) Math.Round(Convert.ToDecimal(stats.Total) / stats.Hits, 2);
              stats.CritRate = Math.Round(Convert.ToDecimal(stats.CritHits) / stats.Hits * 100, 2);
              stats.LuckRate = Math.Round(Convert.ToDecimal(stats.LuckyHits) / stats.Hits * 100, 2);
            }

            if (stats.Total > 0)
            {
              stats.ExtraRate = Math.Round(Convert.ToDecimal(stats.Extra) / stats.Total * 100, 2);
            }

            if ((stats.CritHits - stats.LuckyHits) > 0)
            {
              stats.AvgCrit = (long) Math.Round(Convert.ToDecimal(stats.TotalCrit) / (stats.CritHits - stats.LuckyHits), 2);
            }

            if (stats.LuckyHits > 0)
            {
              stats.AvgLucky = (long) Math.Round(Convert.ToDecimal(stats.TotalLucky) / stats.LuckyHits, 2);
            }

            // total percents
            if (raidTotals.Total > 0)
            {
              stats.PercentOfRaid = Math.Round((decimal) stats.Total / raidTotals.Total * 100, 2);
            }
          });

          for (int i=0; i<combined.StatsList.Count; i++)
          {
            combined.StatsList[i].Rank = i + 1;
          }
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      return combined;
    }
  }
}
