using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  class StatsUtil
  {
    internal const string TIME_FORMAT = "in {0}s";
    internal const string TOTAL_FORMAT = "{0}{1}@{2}";
    internal const string TOTAL_ONLY_FORMAT = "{0}";
    internal const string PLAYER_FORMAT = "{0} = ";
    internal const string PLAYER_RANK_FORMAT = "{0}. {1} = ";
    internal const int DEATH_TIME_OFFSET = 10; // seconds forward

    internal static PlayerStats CreatePlayerStats(Dictionary<string, PlayerStats> individualStats, string key, string origName = null)
    {
      PlayerStats stats = null;

      lock (individualStats)
      {
        if (!individualStats.ContainsKey(key))
        {
          stats = CreatePlayerStats(key, origName);
          individualStats[key] = stats;
        }
        else
        {
          stats = individualStats[key];
        }
      }

      return stats;
    }

    internal static PlayerStats CreatePlayerStats(string name, string origName = null)
    {
      origName = origName ?? name;
      string className = PlayerManager.Instance.GetPlayerClass(origName);

      return new PlayerStats()
      {
        Name = string.Intern(name),
        ClassName = string.Intern(className),
        OrigName = string.Intern(origName),
        Percent = 100, // until something says otherwise
        BeginTime = double.NaN
      };
    }

    internal static PlayerSubStats CreatePlayerSubStats(Dictionary<string, PlayerSubStats> individualStats, string subType, string type)
    {
      var key = Helpers.CreateRecordKey(type, subType);
      PlayerSubStats stats = null;

      lock (individualStats)
      {
        if (!individualStats.ContainsKey(key))
        {
          stats = CreatePlayerSubStats(subType, type);
          individualStats[key] = stats;
        }
        else
        {
          stats = individualStats[key];
        }
      }

      return stats;
    }

    internal static PlayerSubStats CreatePlayerSubStats(string name, string type)
    {
      return new PlayerSubStats()
      {
        ClassName = "",
        Name = string.Intern(name),
        Type = string.Intern(type),
        BeginTime = double.NaN
      };
    }

    internal static string FormatTitle(string targetTitle, string timeTitle, string damageTitle = "")
    {
      string result;
      result = targetTitle + " " + timeTitle;
      if (damageTitle.Length > 0)
      {
        result += ", " + damageTitle;
      }
      return result;
    }

    internal static string FormatTotals(long total)
    {
      string result;

      if (total < 1000)
      {
        result = total.ToString(CultureInfo.CurrentCulture);
      }
      else if (total < 1000000)
      {
        result = Math.Round((decimal)total / 1000, 2) + "K";
      }
      else if (total < 1000000000)
      {
        result = Math.Round((decimal)total / 1000 / 1000, 2) + "M";
      }
      else
      {
        result = Math.Round((decimal)total / 1000 / 1000 / 1000, 2) + "B";
      }

      return result;
    }

    internal static uint ParseUInt(string str)
    {
      uint y = 0;
      for (int i = 0; i < str.Length; i++)
      {
        if (!char.IsDigit(str[i]))
        {
          return uint.MaxValue;
        }

        y = y * 10 + (Convert.ToUInt32(str[i]) - '0');
      }
      return y;
    }

    internal static void UpdateTimeDiffs(PlayerSubStats subStats, FullTimedAction action, double offset = 0)
    {
      int currentIndex = subStats.BeginTimes.Count - 1;
      if (currentIndex == -1)
      {
        subStats.BeginTimes.Add(action.BeginTime);
        subStats.LastTimes.Add(action.LastTime + offset);
        subStats.TimeDiffs.Add(0); // update afterward
        currentIndex = 0;
      }
      else if (subStats.LastTimes[currentIndex] >= action.BeginTime)
      {
        var offsetLastTime = action.LastTime + offset;
        if (offsetLastTime > subStats.LastTimes[currentIndex])
        {
          subStats.LastTimes[currentIndex] = offsetLastTime;
        }
      }
      else
      {
        subStats.BeginTimes.Add(action.BeginTime);
        subStats.LastTimes.Add(action.LastTime + offset);
        subStats.TimeDiffs.Add(0); // update afterward
        currentIndex++;
      }

      subStats.TimeDiffs[currentIndex] = subStats.LastTimes[currentIndex] - subStats.BeginTimes[currentIndex] + 1;
    }

    internal static void UpdateStats(PlayerSubStats stats, HitRecord record, double beginTime)
    {
      switch (record.Type)
      {
        case Labels.BANE:
          stats.BaneHits++;
          stats.Hits += 1;
          break;
        case Labels.MISS:
          stats.Misses++;
          stats.MeleeAttempts += 1;
          break;
        case Labels.PROC:
        case Labels.DOT:
        case Labels.DD:
        case Labels.HOT:
        case Labels.HEAL:
        case Labels.DS:
          stats.Hits += 1;
          break;
        default:
          stats.Hits += 1;
          stats.MeleeHits++;
          stats.MeleeAttempts += 1;
          break;
      }

      if (record.Total > 0)
      {
        stats.Total += record.Total;
        stats.Max = Math.Max(stats.Max, record.Total);
      }

      if (record.Total > 0 && record.OverTotal > 0)
      {
        stats.Extra += (record.OverTotal - record.Total);
      }

      LineModifiersParser.Parse(record, stats);

      stats.BeginTime = double.IsNaN(stats.BeginTime) ? beginTime : stats.BeginTime;
      stats.LastTime = beginTime;
    }

    internal static void MergeStats(PlayerSubStats to, PlayerSubStats from)
    {
      if (to != null && from != null)
      {
        to.BaneHits += from.BaneHits;
        to.Misses += from.Misses;
        to.MeleeAttempts += from.MeleeAttempts;
        to.MeleeHits += from.MeleeHits;
        to.Total += from.Total;
        to.TotalCrit += from.TotalCrit;
        to.TotalLucky += from.TotalLucky;
        to.Hits += from.Hits;
        to.Max = Math.Max(to.Max, from.Max);
        to.Extra += from.Extra;
        to.CritHits += from.CritHits;
        to.LuckyHits += from.LuckyHits;
        to.TwincastHits += from.TwincastHits;
        to.Resists += from.Resists;
        to.StrikethroughHits += from.StrikethroughHits;
        to.RiposteHits += from.RiposteHits;
        to.RampageHits += from.RampageHits;
        to.BeginTime = double.IsNaN(to.BeginTime) ? from.BeginTime : Math.Min(to.BeginTime, from.BeginTime);
        to.LastTime = double.IsNaN(to.LastTime) ? from.LastTime : Math.Max(to.LastTime, from.LastTime);
        to.TotalSeconds = Math.Max(to.TotalSeconds, from.TotalSeconds);
      }
    }

    internal static void CalculateRates(PlayerSubStats stats, PlayerStats raidStats, PlayerStats superStats)
    {
      if (stats.Hits > 0)
      {
        stats.DPS = (long)Math.Round(stats.Total / stats.TotalSeconds, 2);
        stats.SDPS = (long)Math.Round(stats.Total / raidStats.TotalSeconds, 2);
        stats.Avg = (long)Math.Round(Convert.ToDecimal(stats.Total) / stats.Hits, 2);

        if ((stats.CritHits - stats.LuckyHits) > 0)
        {
          stats.AvgCrit = (long)Math.Round(Convert.ToDecimal(stats.TotalCrit) / (stats.CritHits - stats.LuckyHits), 2);
        }

        if (stats.LuckyHits > 0)
        {
          stats.AvgLucky = (long)Math.Round(Convert.ToDecimal(stats.TotalLucky) / stats.LuckyHits, 2);
        }

        if (stats.Total > 0)
        {
          stats.ExtraRate = Math.Round(Convert.ToDouble(stats.Extra) / stats.Total * 100, 2);
        }

        stats.CritRate = Math.Round(Convert.ToDouble(stats.CritHits) / stats.Hits * 100, 2);
        stats.LuckRate = Math.Round(Convert.ToDouble(stats.LuckyHits) / stats.Hits * 100, 2);
        stats.StrikethroughRate = Math.Round(Convert.ToDouble(stats.StrikethroughHits) / stats.MeleeHits * 100, 2);
        stats.RiposteRate = Math.Round(Convert.ToDouble(stats.RiposteHits) / stats.MeleeHits * 100, 2);
        stats.RampageRate = Math.Round(Convert.ToDouble(stats.RampageHits) / stats.MeleeHits * 100, 2);
        stats.MeleeHitRate = Math.Round(Convert.ToDouble(stats.MeleeHits) / stats.MeleeAttempts * 100, 2);

        var tcMult = stats.Type == Labels.DD ? 2 : 1;
        stats.TwincastRate = Math.Round(Convert.ToDouble(stats.TwincastHits) / stats.Hits * tcMult * 100, 2);
        stats.ResistRate = Math.Round(Convert.ToDouble(stats.Resists) / (stats.Hits + stats.Resists) * 100, 2);

        if (superStats != null && superStats.Total > 0)
        {
          stats.Percent = Math.Round(superStats.Percent / 100 * (Convert.ToDouble(stats.Total) / superStats.Total) * 100, 2);
          stats.SDPS = (long)Math.Round(stats.Total / superStats.TotalSeconds, 2);
        }
        else if (superStats == null)
        {
          stats.SDPS = (long)Math.Round(stats.Total / raidStats.TotalSeconds, 2);
        }
      }
    }

    internal static void UpdateCalculations(PlayerSubStats stats, PlayerStats raidTotals, Dictionary<string, uint> resistCounts = null, PlayerStats superStats = null)
    {
      if (superStats != null)
      {
        if (resistCounts != null && superStats.Name == ConfigUtil.PlayerName && resistCounts.TryGetValue(stats.Name, out uint value))
        {
          stats.Resists = value;
        }
      }

      CalculateRates(stats, raidTotals, superStats);

      // total percents
      if (raidTotals.Total > 0)
      {
        stats.PercentOfRaid = Math.Round(Convert.ToDouble(stats.Total) / raidTotals.Total * 100, 2);
      }

      // handle sub stats
      if (stats is PlayerStats playerStats)
      {
        Parallel.ForEach(playerStats.SubStats.Values, subStats => UpdateCalculations(subStats, raidTotals, resistCounts, playerStats));

        // optional stats
        if (playerStats.SubStats2.Count > 0)
        {
          Parallel.ForEach(playerStats.SubStats2.Values, subStats => UpdateCalculations(subStats, raidTotals, resistCounts, playerStats));
        }
      }
    }

    internal static ConcurrentDictionary<string, uint> GetPlayerDeaths(PlayerStats raidStats)
    {
      Dictionary<string, uint> deathCounts = new Dictionary<string, uint>();

      if (raidStats.BeginTimes.Count > 0 && raidStats.LastTimes.Count > 0)
      {
        double beginTime = raidStats.BeginTimes.First();
        double endTime = raidStats.LastTimes.Last() + DEATH_TIME_OFFSET;

        Parallel.ForEach(DataManager.Instance.GetPlayerDeathsDuring(beginTime, endTime), block =>
        {
          block.Actions.ForEach(action =>
          {
            PlayerDeath death = action as PlayerDeath;
            Helpers.StringUIntAddHelper.Add(deathCounts, death.Player, 1);
          });
        });
      }

      return new ConcurrentDictionary<string, uint>(deathCounts);
    }
  }

  internal abstract class Parse
  {
    internal string Name { get; set; }
    internal CombinedStats Combined { get; set; }
    internal List<PlayerStats> Selected { get; set; }
    internal virtual StatsSummary Create(bool showTotals, bool rankPlayers)
    {
      return null;
    }
  }
}
