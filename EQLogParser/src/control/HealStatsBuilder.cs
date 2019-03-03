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

    private const int HEAL_OFFSET = 5; // additional # of seconds to count hilling after last damage is seen
    private const string UNKNOWN_SPELL = "Unknown Spell";
    private const string UNKNOWN_PLAYER = "Unknown Player";

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
      CombinedHealStats combined = new CombinedHealStats() { UniqueClasses = new Dictionary<string, byte>() };

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
            ConcurrentDictionary<string, PlayerSubStats> healerStats = new ConcurrentDictionary<string, PlayerSubStats>();
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
                if (DataManager.Instance.CheckNameForPlayer(record.Healed) || DataManager.Instance.CheckNameForPet(record.Healed))
                {
                  PlayerStats stats = CreatePlayerStats(individualStats, record.Healer);

                  lock (stats)
                  {
                    raidTotals.Total += record.Total;
                    UpdateStats(stats, record);
                    healerStats.TryAdd(record.Healer, stats);
                    LineModifiersParser.Parse(record, stats);

                    var spellStatName = record.Spell ?? UNKNOWN_SPELL;
                    PlayerSubStats spellStats = CreatePlayerSubStats(stats.SubStats, spellStatName, record.Type);
                    UpdateStats(spellStats, record);
                    healerStats.TryAdd(stats.Name + "=" + spellStatName, spellStats);
                    LineModifiersParser.Parse(record, spellStats);

                    var healedStatName = record.Healed;
                    PlayerSubStats healedStats = CreatePlayerSubStats(stats.SubStats2, healedStatName, record.Type);
                    UpdateStats(healedStats, record);
                    healerStats.TryAdd(stats.Name + "=" + healedStatName, healedStats);
                    LineModifiersParser.Parse(record, healedStats);
                  }
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

          Parallel.ForEach(combined.StatsList, stats => UpdateCalculations(stats, raidTotals));

          for (int i=0; i<combined.StatsList.Count; i++)
          {
            combined.StatsList[i].Rank = i + 1;
            combined.UniqueClasses[combined.StatsList[i].ClassName] = 1;
          }
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      return combined;
    }

    private static void UpdateStats(PlayerSubStats stats, HealRecord record)
    {
      stats.Total += record.Total;
      stats.Hits += 1;
      stats.Max = (stats.Max < record.Total) ? record.Total : stats.Max;

      if (record.Total > 0 && record.OverHeal > 0)
      {
        stats.Extra += (record.OverHeal - record.Total);
      }
    }

    private static void UpdateCalculations(PlayerSubStats stats, PlayerStats raidTotals, PlayerStats parentStats = null)
    {
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

      stats.DPS = (long) Math.Round(stats.Total / stats.TotalSeconds, 2);

      if (parentStats == null)
      {
        stats.SDPS = (long) Math.Round(stats.Total / raidTotals.TotalSeconds, 2);
      }
      else
      {
        if (parentStats.Total > 0)
        {
          stats.Percent = Math.Round((decimal) stats.Total / parentStats.Total * 100, 2);
        }

        stats.SDPS = (long) Math.Round(stats.Total / parentStats.TotalSeconds, 2);
      }

      // handle sub stats
      var playerStats = stats as PlayerStats;

      if (playerStats != null)
      {
        Parallel.ForEach(playerStats.SubStats.Values, subStats => UpdateCalculations(subStats, raidTotals, playerStats));
        Parallel.ForEach(playerStats.SubStats2.Values, subStats => UpdateCalculations(subStats, raidTotals, playerStats));
      }
    }
  }
}
