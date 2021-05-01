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
    internal const string SPECIAL_FORMAT = "{0} {{{1}}}";
    internal const int SPECIAL_OFFSET = 15;
    internal const int DEATH_OFFSET = 15;

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
        Percent = 100 // until something says otherwise
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
        Type = string.Intern(type)
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

    internal static string FormatTotals(long total, int roundTo = 2)
    {
      string result;

      if (total < 1000)
      {
        result = total.ToString(CultureInfo.CurrentCulture);
      }
      else if (total < 1000000)
      {
        result = Math.Round((decimal)total / 1000, roundTo) + "K";
      }
      else if (total < 1000000000)
      {
        result = Math.Round((decimal)total / 1000 / 1000, roundTo) + "M";
      }
      else
      {
        result = Math.Round((decimal)total / 1000 / 1000 / 1000, roundTo) + "B";
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

    internal static void UpdateRaidTimeRanges(Fight fight, ConcurrentDictionary<string, TimeRange> playerTimeRanges,
      ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> playerSubTimeRanges, bool includeInitialTanking = false)
    {
      if (includeInitialTanking)
      {
        foreach (var entry in fight.InitialTankSegments)
        {
          AddTimeEntry(playerTimeRanges, entry);
        }

        foreach (var subEntry in fight.InitialTankSubSegments)
        {
          AddSubTimeEntry(playerSubTimeRanges, subEntry);
        }
      }

      foreach (var entry in fight.DamageSegments)
      {
        AddTimeEntry(playerTimeRanges, entry);
      }

      foreach (var subEntry in fight.DamageSubSegments)
      {
        AddSubTimeEntry(playerSubTimeRanges, subEntry);
      }
    }

    internal static void AddSubTimeEntry(ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> playerSubTimeRanges,
      KeyValuePair<string, Dictionary<string, TimeSegment>> subEntry)
    {
      if (!playerSubTimeRanges.TryGetValue(subEntry.Key, out ConcurrentDictionary<string, TimeRange> ranges))
      {
        ranges = new ConcurrentDictionary<string, TimeRange>();
        playerSubTimeRanges[subEntry.Key] = ranges;
      }

      foreach (var typeEntry in subEntry.Value)
      {
        AddTimeEntry(ranges, typeEntry);
      }
    }

    private static void AddTimeEntry(ConcurrentDictionary<string, TimeRange> ranges, KeyValuePair<string, TimeSegment> entry)
    {
      if (ranges.TryGetValue(entry.Key, out TimeRange range))
      {
        range.Add(new TimeSegment(entry.Value.BeginTime, entry.Value.EndTime));
      }
      else
      {
        // make sure to copy the time segment and not just use the one in the Fight
        ranges[entry.Key] = new TimeRange(new TimeSegment(entry.Value.BeginTime, entry.Value.EndTime));
      }
    }

    internal static void UpdateAllStatsTimeRanges(PlayerStats stats, ConcurrentDictionary<string, TimeRange> playerTimeRanges,
      ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> playerSubTimeRanges, double maxTime = -1)
    {
      if (playerTimeRanges.TryGetValue(stats.Name, out TimeRange range))
      {
        var filteredRange = FilterMaxTime(range, maxTime);
        stats.TotalSeconds = filteredRange.GetTotal();
      }

      UpdateSubStatsTimeRanges(stats, playerSubTimeRanges, maxTime);
    }

    internal static void UpdateSubStatsTimeRanges(PlayerStats stats, ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> playerSubTimeRanges, double maxTime = -1)
    {
      if (playerSubTimeRanges.TryGetValue(stats.Name, out ConcurrentDictionary<string, TimeRange> subRanges))
      {
        foreach (var kv in stats.SubStats)
        {
          if (subRanges.TryGetValue(kv.Key, out TimeRange subRange))
          {
            var filteredRange = FilterMaxTime(subRange, maxTime);
            kv.Value.TotalSeconds = filteredRange.GetTotal();
          }
        }

        foreach (var kv in stats.SubStats2)
        {
          if (subRanges.TryGetValue(kv.Key, out TimeRange subRange))
          {
            var filteredRange = FilterMaxTime(subRange, maxTime);
            kv.Value.TotalSeconds = filteredRange.GetTotal();
          }
        }
      }
    }

    internal static void UpdateTimeSegments(Dictionary<string, TimeSegment> segments, Dictionary<string, Dictionary<string, TimeSegment>> subSegments,
      string key, string player, double time)
    {
      if (segments != null)
      {
        if (segments.TryGetValue(player, out TimeSegment segment))
        {
          segment.EndTime = time;
        }
        else
        {
          segments[player] = new TimeSegment(time, time);
        }
      }

      if (!subSegments.TryGetValue(player, out Dictionary<string, TimeSegment> typeSegments))
      {
        typeSegments = new Dictionary<string, TimeSegment>();
        subSegments[player] = typeSegments;
      }

      if (typeSegments.TryGetValue(key, out TimeSegment typeSegment))
      {
        typeSegment.EndTime = time;
      }
      else
      {
        typeSegments[key] = new TimeSegment(time, time);
      }
    }

    internal static void UpdateStats(PlayerSubStats stats, HitRecord record)
    {
      switch (record.Type)
      {
        case Labels.BANE:
          stats.BaneHits++;
          stats.Hits += 1;
          break;
        case Labels.BLOCK:
          stats.Blocks++;
          stats.MeleeAttempts += 1;
          break;
        case Labels.DODGE:
          stats.Dodges++;
          stats.MeleeAttempts += 1;
          break;
        case Labels.MISS:
          stats.Misses++;
          stats.MeleeAttempts += 1;
          break;
        case Labels.PARRY:
          stats.Parries++;
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
    }

    internal static void MergeStats(PlayerSubStats to, PlayerSubStats from)
    {
      if (to != null && from != null)
      {
        to.BaneHits += from.BaneHits;
        to.Blocks += from.Blocks;
        to.Dodges += from.Dodges;
        to.Misses += from.Misses;
        to.Parries += from.Parries;
        to.MeleeAttempts += from.MeleeAttempts;
        to.MeleeHits += from.MeleeHits;
        to.Total += from.Total;
        to.TotalAss += from.TotalAss;
        to.TotalCrit += from.TotalCrit;
        to.TotalHead += from.TotalHead;
        to.TotalLucky += from.TotalLucky;
        to.TotalRiposte += from.TotalRiposte;
        to.TotalSlay += from.TotalSlay;
        to.Hits += from.Hits;
        to.Max = Math.Max(to.Max, from.Max);
        to.Extra += from.Extra;
        to.AssHits += from.AssHits;
        to.CritHits += from.CritHits;
        to.DoubleBowHits = from.DoubleBowHits;
        to.FlurryHits += from.FlurryHits;
        to.HeadHits += from.HeadHits;
        to.LuckyHits += from.LuckyHits;
        to.TwincastHits += from.TwincastHits;
        to.Resists += from.Resists;
        to.StrikethroughHits += from.StrikethroughHits;
        to.RiposteHits += from.RiposteHits;
        to.SlayHits += from.SlayHits;
        to.RampageHits += from.RampageHits;
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

        if (stats.MeleeHits > 0)
        {
          stats.DoubleBowRate = Math.Round(Convert.ToDouble(stats.DoubleBowHits) / stats.MeleeHits * 100, 2);
          stats.FlurryRate = Math.Round(Convert.ToDouble(stats.FlurryHits) / stats.MeleeHits * 100, 2);
          stats.StrikethroughRate = Math.Round(Convert.ToDouble(stats.StrikethroughHits) / stats.MeleeHits * 100, 2);
          stats.RiposteRate = Math.Round(Convert.ToDouble(stats.RiposteHits) / stats.MeleeHits * 100, 2);
          stats.RampageRate = Math.Round(Convert.ToDouble(stats.RampageHits) / stats.MeleeHits * 100, 2);
        }

        if (stats.MeleeAttempts > 0)
        {
          stats.MeleeHitRate = Math.Round(Convert.ToDouble(stats.MeleeHits) / stats.MeleeAttempts * 100, 2);
          stats.MeleeAccRate = Math.Round(Convert.ToDouble(stats.MeleeHits) / (stats.MeleeAttempts - stats.Parries - stats.Dodges - stats.Blocks) * 100, 2);
        }

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

    internal static void UpdateCalculations(PlayerSubStats stats, PlayerStats raidTotals, Dictionary<string, int> resistCounts = null, PlayerStats superStats = null)
    {
      if (superStats != null)
      {
        if (resistCounts != null && superStats.Name == ConfigUtil.PlayerName && resistCounts.TryGetValue(stats.Name, out int value))
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

    internal static ConcurrentDictionary<string, string> GetSpecials(PlayerStats raidStats)
    {
      ConcurrentDictionary<string, string> playerSpecials = new ConcurrentDictionary<string, string>();
      ConcurrentDictionary<object, bool> temp = new ConcurrentDictionary<object, bool>();
      var allSpecials = DataManager.Instance.GetSpecials();
      int specialStart = 0;

      raidStats.Ranges.TimeSegments.ForEach(segment =>
      {
        double offsetBegin = segment.BeginTime - SPECIAL_OFFSET;
        var actions = new List<IAction>();

        if (specialStart > -1 && specialStart < allSpecials.Count)
        {
          specialStart = allSpecials.FindIndex(specialStart, special => special.BeginTime >= offsetBegin);
          if (specialStart > -1)
          {
            for (int j = specialStart; j < allSpecials.Count; j++)
            {
              if (allSpecials[j].BeginTime >= offsetBegin && allSpecials[j].BeginTime <= segment.EndTime)
              {
                specialStart = j;
                actions.Add(allSpecials[j]);
              }
            }
          }
        }

        foreach (var deaths in DataManager.Instance.GetDeathsDuring(offsetBegin, segment.EndTime + DEATH_OFFSET).Select(block => block.Actions))
        {
          actions.AddRange(deaths);
        }

        actions.ForEach(action =>
        {
          if (!temp.ContainsKey(action))
          {
            string code = null;
            string player = null;

            if (action is DeathRecord death && PlayerManager.Instance.IsVerifiedPlayer(death.Killed))
            {
              player = death.Killed;
              code = "X";
            }
            if (action is SpecialSpell spell)
            {
              player = spell.Player;
              code = spell.Code;
            }

            if (!string.IsNullOrEmpty(player) && !string.IsNullOrEmpty(code))
            {
              if (playerSpecials.TryGetValue(player, out string special))
              {
                playerSpecials[player] = special + code;
              }
              else
              {
                playerSpecials[player] = code;
              }
            }

            temp.TryAdd(action, true);
          }
        });
      });

      return playerSpecials;
    }

    internal static TimeRange FilterMaxTime(TimeRange range, double maxTime)
    {
      TimeRange result;

      if (maxTime > -1)
      {
        result = new TimeRange();
        range.TimeSegments.ForEach(segment =>
        {
          if (segment.EndTime <= maxTime)
          {
            result.Add(segment);
          }
          else if (segment.BeginTime < maxTime)
          {
            result.Add(new TimeSegment(segment.BeginTime, maxTime));
          }
        });
      }
      else
      {
        result = range;
      }

      return result;
    }

    internal static bool IsHitType(string type)
    {
      bool isHitType = true;
      if (!string.IsNullOrEmpty(type))
      {
        switch (type)
        {
          case Labels.MISS:
          case Labels.DODGE:
          case Labels.PARRY:
            isHitType = false;
            break;
        }
      }
      return isHitType;
    }
  }
}
