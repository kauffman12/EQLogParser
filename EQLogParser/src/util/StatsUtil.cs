using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EQLogParser
{
  static class StatsUtil
  {
    internal const string TIME_FORMAT = "in {0}s";
    internal const string TOTAL_FORMAT = "{0}{1}@{2}";
    internal const string TOTAL_ONLY_FORMAT = "{0}";
    internal const string PLAYER_FORMAT = "{0} = ";
    internal const string PLAYER_RANK_FORMAT = "{0}. {1} = ";
    internal const string SPECIAL_FORMAT = "{0} {{{1}}}";
    internal const int SPECIAL_OFFSET = 15;
    internal const int DEATH_OFFSET = 15;

    private static readonly ConcurrentDictionary<string, byte> RegularMeleeTypes = new ConcurrentDictionary<string, byte>(new Dictionary<string, byte>()
    { { "Bites", 1 }, { "Claws", 1 },  { "Crushes", 1 }, { "Pierces", 1 }, { "Punches", 1 }, { "Slashes", 1 } });

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
      var className = PlayerManager.Instance.GetPlayerClass(origName);

      return new PlayerStats()
      {
        Name = string.Intern(name),
        ClassName = string.Intern(className),
        OrigName = string.Intern(origName),
        Percent = 100, // until something says otherwise
      };
    }

    internal static PlayerSubStats CreatePlayerSubStats(ICollection<PlayerSubStats> individualStats, string subType, string type)
    {
      var key = Helpers.CreateRecordKey(type, subType);
      PlayerSubStats stats = null;

      lock (individualStats)
      {
        stats = individualStats.FirstOrDefault(stats => stats.Key == key);
        if (stats == null)
        {
          stats = new PlayerSubStats { ClassName = "", Name = string.Intern(subType), Type = string.Intern(type), Key = key };
          individualStats.Add(stats);
        }
      }

      return stats;
    }

    internal static string FormatTitle(string targetTitle, string timeTitle, string damageTitle = "")
    {
      var result = targetTitle;
      if (!string.IsNullOrEmpty(timeTitle))
      {
        result += " " + timeTitle;
      }

      if (!string.IsNullOrEmpty(damageTitle))
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
        result = string.Format("{0}K", Math.Round((decimal)total / 1000, roundTo));
      }
      else if (total < 1000000000)
      {
        result = string.Format("{0}M", Math.Round((decimal)total / 1000 / 1000, roundTo));
      }
      else
      {
        result = string.Format("{0}B", Math.Round((decimal)total / 1000 / 1000 / 1000, roundTo));
      }

      return result;
    }

    internal static uint ParseUInt(string str, uint defValue = uint.MaxValue) => ParseUInt(str.AsSpan(), defValue);

    internal static uint ParseUInt(ReadOnlySpan<char> span, uint defValue = uint.MaxValue)
    {
      uint y = 0;

      for (var i = 0; i < span.Length; i++)
      {
        if (!char.IsDigit(span[i]))
        {
          return defValue;
        }

        y = (y * 10) + (uint)(span[i] - '0');
      }

      return y;
    }

    internal static void UpdateRaidTimeRanges(Dictionary<string, TimeSegment> segments, Dictionary<string, Dictionary<string, TimeSegment>> subSegments,
    ConcurrentDictionary<string, TimeRange> playerTimeRanges, ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> playerSubTimeRanges)
    {
      foreach (ref var entry in segments.ToArray().AsSpan())
      {
        AddTimeEntry(playerTimeRanges, entry);
      }

      foreach (ref var subEntry in subSegments.ToArray().AsSpan())
      {
        AddSubTimeEntry(playerSubTimeRanges, subEntry);
      }
    }

    internal static void AddSubTimeEntry(ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> playerSubTimeRanges,
      KeyValuePair<string, Dictionary<string, TimeSegment>> subEntry)
    {
      if (!playerSubTimeRanges.TryGetValue(subEntry.Key, out var ranges))
      {
        ranges = new ConcurrentDictionary<string, TimeRange>();
        playerSubTimeRanges[subEntry.Key] = ranges;
      }

      foreach (ref var typeEntry in subEntry.Value.ToArray().AsSpan())
      {
        AddTimeEntry(ranges, typeEntry);
      }
    }

    private static void AddTimeEntry(ConcurrentDictionary<string, TimeRange> ranges, KeyValuePair<string, TimeSegment> entry)
    {
      if (ranges.TryGetValue(entry.Key, out var range))
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
      ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> playerSubTimeRanges, double minTime = -1, double maxTime = -1)
    {
      if (playerTimeRanges.TryGetValue(stats.Name, out var range))
      {
        var filteredRange = FilterTimeRange(range, minTime, maxTime);
        stats.TotalSeconds = filteredRange.GetTotal();
      }

      UpdateSubStatsTimeRanges(stats, playerSubTimeRanges, minTime, maxTime);
    }

    internal static void UpdateSubStatsTimeRanges(PlayerStats stats, ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> playerSubTimeRanges,
      double minTime = -1, double maxTime = -1)
    {
      if (playerSubTimeRanges.TryGetValue(stats.Name, out var subRanges))
      {
        UpdateSubStat(stats.SubStats, subRanges, minTime, maxTime);
        UpdateSubStat(stats.SubStats2, subRanges, minTime, maxTime);
      }
    }

    private static void UpdateSubStat(List<PlayerSubStats> subStats, ConcurrentDictionary<string, TimeRange> subRanges, double minTime, double maxTime)
    {
      foreach (ref var subStat in subStats.ToArray().AsSpan())
      {
        if (subRanges.TryGetValue(subStat.Key, out var subRange))
        {
          var filteredRange = FilterTimeRange(subRange, minTime, maxTime);
          subStat.TotalSeconds = filteredRange.GetTotal();
        }
      }
    }

    internal static void UpdateTimeSegments(Dictionary<string, TimeSegment> segments, Dictionary<string, Dictionary<string, TimeSegment>> subSegments,
      string key, string player, double time)
    {
      if (segments != null)
      {
        if (segments.TryGetValue(player, out var segment))
        {
          segment.EndTime = time;
        }
        else
        {
          segments[player] = new TimeSegment(time, time);
        }
      }

      if (!subSegments.TryGetValue(player, out var typeSegments))
      {
        typeSegments = new Dictionary<string, TimeSegment>();
        subSegments[player] = typeSegments;
      }

      if (typeSegments.TryGetValue(key, out var typeSegment))
      {
        typeSegment.EndTime = time;
      }
      else
      {
        typeSegments[key] = new TimeSegment(time, time);
      }
    }

    internal static void UpdateStats(PlayerSubStats stats, HitRecord record, bool newFrame = false, bool isPet = false)
    {
      var newMeleeHit = false;
      var parseModifiers = true;

      switch (record.Type)
      {
        // absorb isn't counted as a hit so don't parse whether it was a strikethrough, etc
        case Labels.ABSORB:
          stats.Absorbs++;
          stats.MeleeAttempts++;
          parseModifiers = false;
          break;
        case Labels.BANE:
          stats.BaneHits++;
          stats.Hits += 1;
          break;
        case Labels.BLOCK:
          stats.Blocks++;
          stats.MeleeAttempts++;
          break;
        case Labels.DODGE:
          stats.Dodges++;
          stats.MeleeAttempts++;
          break;
        case Labels.MISS:
          stats.Misses++;
          stats.MeleeAttempts++;
          break;
        case Labels.PARRY:
          stats.Parries++;
          stats.MeleeAttempts++;
          break;
        case Labels.RIPOSTE:  // defensive riposte
          stats.RiposteHits++;
          stats.MeleeAttempts++;
          break;
        case Labels.INVULNERABLE:
          stats.Invulnerable++;
          stats.MeleeAttempts++;
          break;
        case Labels.PROC:
        case Labels.DOT:
        case Labels.DD:
          stats.SpellHits++;
          stats.Hits++;
          break;
        case Labels.HOT:
        case Labels.HEAL:
          stats.SpellHits++;
          stats.Hits += 1;
          break;
        case Labels.DS:
        case Labels.RS:
          stats.Hits += 1;
          break;
        default:
          stats.Hits += 1;
          stats.MeleeAttempts++;
          newMeleeHit = true;
          break;
      }

      if (newMeleeHit)
      {
        // regular hit from a player OR Hits from a pet can do things like Flurry
        if (record.Type == Labels.MELEE)
        {
          if (RegularMeleeTypes.ContainsKey(record.SubType) || (record.SubType == "Hits" && isPet))
          {
            stats.RegularMeleeHits++;
          }
          else if (record.SubType == "Shoots")
          {
            stats.BowHits++;
          }
        }

        stats.MeleeHits++;
      }

      if (record.Total > 0)
      {
        stats.Total += record.Total;
        stats.Max = Math.Max(stats.Max, record.Total);
        stats.Min = GetMin(stats.Min, record.Total);

        if (newFrame)
        {
          stats.BestSec = Math.Max(stats.BestSec, stats.BestSecTemp);
          stats.BestSecTemp = 0;
        }

        stats.BestSecTemp += record.Total;
      }

      if (record.OverTotal > 0)
      {
        stats.Extra += record.OverTotal - record.Total;
      }

      if (parseModifiers)
      {
        LineModifiersParser.UpdateStats(record, stats);
      }
    }

    internal static void MergeStats(PlayerSubStats to, PlayerSubStats from)
    {
      if (to != null && from != null)
      {
        to.BestSec = Math.Max(to.BestSec, from.BestSec);
        to.Absorbs += from.Absorbs;
        to.BaneHits += from.BaneHits;
        to.Blocks += from.Blocks;
        to.BowHits += from.BowHits;
        to.Dodges += from.Dodges;
        to.Misses += from.Misses;
        to.Parries += from.Parries;
        to.MeleeAttempts += from.MeleeAttempts;
        to.MeleeHits += from.MeleeHits;
        to.Total += from.Total;
        to.TotalAss += from.TotalAss;
        to.TotalCrit += from.TotalCrit;
        to.TotalFinishing += from.TotalFinishing;
        to.TotalHead += from.TotalHead;
        to.TotalLucky += from.TotalLucky;
        to.TotalNonTwincast += from.TotalNonTwincast;
        to.TotalNonTwincastCrit += from.TotalNonTwincastCrit;
        to.TotalNonTwincastLucky += from.TotalNonTwincastLucky;
        to.TotalRiposte += from.TotalRiposte;
        to.TotalSlay += from.TotalSlay;
        to.Hits += from.Hits;
        to.Max = Math.Max(to.Max, from.Max);
        to.Min = GetMin(to.Min, from.Min);
        to.Extra += from.Extra;
        to.AssHits += from.AssHits;
        to.CritHits += from.CritHits;
        to.NonTwincastCritHits += from.NonTwincastCritHits;
        to.NonTwincastLuckyHits += from.NonTwincastLuckyHits;
        to.DoubleBowHits = from.DoubleBowHits;
        to.FinishingHits += from.FinishingHits;
        to.FlurryHits += from.FlurryHits;
        to.HeadHits += from.HeadHits;
        to.LuckyHits += from.LuckyHits;
        to.TwincastHits += from.TwincastHits;
        to.Resists += from.Resists;
        to.SpellHits += from.SpellHits;
        to.StrikethroughHits += from.StrikethroughHits;
        to.RiposteHits += from.RiposteHits;
        to.SlayHits += from.SlayHits;
        to.RampageHits += from.RampageHits;
        to.RegularMeleeHits += from.RegularMeleeHits;
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
        stats.Potential = stats.Total + stats.Extra;

        if ((stats.CritHits - stats.LuckyHits) is uint nonLucky && nonLucky > 0)
        {
          stats.AvgCrit = (long)Math.Round(Convert.ToDecimal(stats.TotalCrit) / nonLucky, 2);
        }

        if (stats.LuckyHits > 0)
        {
          stats.AvgLucky = (long)Math.Round(Convert.ToDecimal(stats.TotalLucky) / stats.LuckyHits, 2);
        }

        if (stats.NonTwincastCritHits > 0)
        {
          stats.AvgNonTwincastCrit = (long)Math.Round(Convert.ToDecimal(stats.TotalNonTwincastCrit) / stats.NonTwincastCritHits, 2);
        }

        if (stats.NonTwincastLuckyHits > 0)
        {
          stats.AvgNonTwincastLucky = (long)Math.Round(Convert.ToDecimal(stats.TotalNonTwincastLucky) / stats.NonTwincastLuckyHits, 2);
        }

        if (stats.Total > 0)
        {
          stats.ExtraRate = (float)Math.Round((float)stats.Extra / stats.Total * 100, 2);
        }

        if ((stats.Hits - stats.TwincastHits) is uint nonTwincast && nonTwincast > 0)
        {
          stats.AvgNonTwincast = (long)Math.Round(Convert.ToDecimal(stats.TotalNonTwincast) / nonTwincast, 2);
        }

        stats.CritRate = (float)Math.Round((float)stats.CritHits / stats.Hits * 100, 2);
        stats.LuckRate = (float)Math.Round((float)stats.LuckyHits / stats.Hits * 100, 2);

        // All Regular Melee Hits are MeleeHits but not the reverse
        if (stats.RegularMeleeHits > 0)
        {
          stats.FlurryRate = (float)Math.Round((float)stats.FlurryHits / stats.RegularMeleeHits * 100, 2);
        }

        if (stats.MeleeHits > 0)
        {
          stats.RiposteRate = (float)Math.Round((float)stats.RiposteHits / stats.MeleeHits * 100, 2);
          stats.RampageRate = (float)Math.Round((float)stats.RampageHits / stats.MeleeHits * 100, 2);
        }

        if (stats.BowHits > 0)
        {
          stats.DoubleBowRate = (float)Math.Round((float)stats.DoubleBowHits / stats.BowHits * 100, 2);
        }

        if (stats.MeleeAttempts > 0)
        {
          stats.MeleeHitRate = (float)Math.Round((float)stats.MeleeHits / stats.MeleeAttempts * 100, 2);
          stats.MeleeAccRate = (float)Math.Round((float)stats.MeleeHits / (stats.MeleeAttempts - stats.Parries - stats.Dodges - stats.Blocks - stats.Invulnerable - stats.Absorbs) * 100, 2);
        }

        stats.MeleeUndefended = stats.MeleeHits - stats.StrikethroughHits;

        if (stats.SpellHits > 0)
        {
          var tcMult = stats.Type == Labels.DD ? 2 : 1;
          stats.TwincastRate = (float)Math.Round((float)stats.TwincastHits / stats.SpellHits * tcMult * 100, 2);
          stats.TwincastRate = (float)(stats.TwincastRate > 100.0 ? 100.0 : stats.TwincastRate);
          stats.ResistRate = (float)Math.Round((float)stats.Resists / (stats.SpellHits + stats.Resists) * 100, 2);
        }

        if (superStats != null && superStats.Total > 0)
        {
          stats.Percent = (float)Math.Round(superStats.Percent / 100 * ((float)stats.Total / superStats.Total) * 100, 2);
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
        if (resistCounts != null && superStats.Name == ConfigUtil.PlayerName && resistCounts.TryGetValue(stats.Name, out var value))
        {
          stats.Resists = value;
        }
      }

      CalculateRates(stats, raidTotals, superStats);

      // total percents
      if (raidTotals.Total > 0)
      {
        stats.PercentOfRaid = (float)Math.Round((float)stats.Total / raidTotals.Total * 100, 2);
      }

      // remaining amount
      if (stats.BestSecTemp > 0)
      {
        stats.BestSec = Math.Max(stats.BestSec, stats.BestSecTemp);
        stats.BestSecTemp = 0;
      }

      // handle sub stats
      if (stats is PlayerStats playerStats)
      {

        foreach (ref var subStat in playerStats.SubStats.ToArray().AsSpan())
        {
          UpdateCalculations(subStat, raidTotals, resistCounts, playerStats);
        }

        // optional stats
        foreach (ref var subStat2 in playerStats.SubStats2.ToArray().AsSpan())
        {
          UpdateCalculations(subStat2, raidTotals, resistCounts, playerStats);
        }
      }
    }

    internal static void PopulateSpecials(PlayerStats raidStats)
    {
      raidStats.Specials.Clear();
      raidStats.Deaths.Clear();

      var temp = new ConcurrentDictionary<object, bool>();
      var allSpecials = DataManager.Instance.GetSpecials();
      var specialStart = 0;

      foreach (ref var segment in raidStats.Ranges.TimeSegments.ToArray().AsSpan())
      {
        var offsetBegin = segment.BeginTime - SPECIAL_OFFSET;
        var actions = new List<IAction>();

        if (specialStart > -1 && specialStart < allSpecials.Count)
        {
          specialStart = allSpecials.FindIndex(specialStart, special => special.BeginTime >= offsetBegin);
          if (specialStart > -1)
          {
            for (var j = specialStart; j < allSpecials.Count; j++)
            {
              if (allSpecials[j].BeginTime >= offsetBegin && allSpecials[j].BeginTime <= segment.EndTime)
              {
                specialStart = j;
                actions.Add(allSpecials[j]);
              }
            }
          }
        }

        foreach (var block in DataManager.Instance.GetDeathsDuring(offsetBegin, segment.EndTime + DEATH_OFFSET))
        {
          foreach (var death in block.Actions.Cast<DeathRecord>())
          {
            if (PlayerManager.Instance.IsVerifiedPlayer(death.Killed) || PlayerManager.Instance.IsMerc(death.Killed))
            {
              actions.Add(death);
              raidStats.Deaths.Add(new DeathEvent
              {
                BeginTime = block.BeginTime,
                Killed = death.Killed,
                Killer = death.Killer,
                Message = death.Message,
                Previous = death.Previous
              });
            }
          }
        }

        foreach (ref var action in actions.ToArray().AsSpan())
        {
          if (!temp.ContainsKey(action))
          {
            string code = null;
            string player = null;

            if (action is DeathRecord death)
            {
              player = death.Killed;
              code = "X";
            }
            else if (action is SpecialSpell spell)
            {
              player = spell.Player;
              code = spell.Code;
            }

            if (!string.IsNullOrEmpty(player) && !string.IsNullOrEmpty(code))
            {
              if (raidStats.Specials.TryGetValue(player, out var special))
              {
                raidStats.Specials[player] = special + code;
              }
              else
              {
                raidStats.Specials[player] = code;
              }
            }

            temp.TryAdd(action, true);
          }
        }
      }
    }

    internal static TimeRange FilterTimeRange(TimeRange range, double minTime, double maxTime)
    {
      TimeRange result;

      if (maxTime > -1 || minTime > -1)
      {
        result = new TimeRange();
        range.TimeSegments.ForEach(segment =>
        {
          if ((minTime == -1 || segment.BeginTime >= minTime) && (maxTime == -1 || segment.EndTime <= maxTime))
          {
            result.Add(segment);
          }
          else if ((minTime == -1 || segment.BeginTime >= minTime) && maxTime >= segment.BeginTime)
          {
            result.Add(new TimeSegment(segment.BeginTime, maxTime));
          }
          else if ((maxTime == -1 || segment.EndTime <= maxTime) && minTime <= segment.EndTime)
          {
            result.Add(new TimeSegment(minTime, segment.EndTime));
          }
          else if (segment.BeginTime < minTime && segment.EndTime > maxTime)
          {
            result.Add(new TimeSegment(minTime, maxTime));
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
      var isHitType = true;
      if (!string.IsNullOrEmpty(type))
      {
        switch (type)
        {
          case Labels.ABSORB:
          case Labels.DODGE:
          case Labels.INVULNERABLE:
          case Labels.MISS:
          case Labels.PARRY:
          case Labels.RIPOSTE:
            isHitType = false;
            break;
        }
      }
      return isHitType;
    }

    private static uint GetMin(uint to, uint from)
    {
      if (to == 0 && from > 0)
      {
        return from;
      }
      else if (from == 0 && to > 0)
      {
        return to;
      }

      return Math.Min(to, from);
    }
  }
}
