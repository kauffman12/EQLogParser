using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EQLogParser
{
  internal static class StatsUtil
  {
    internal const string TimeFormat = "in {0}s";
    internal const string TotalFormat = "{0}{1}@{2}";
    internal const string TotalOnlyFormat = "{0}";
    internal const string PlayerFormat = "{0} = ";
    internal const string PlayerRankFormat = "{0}. {1} = ";
    internal const string SpecialFormat = "{0} {{{1}}}";
    internal const int SpecialOffset = 15;
    internal const int DeathOffset = 15;

    private static readonly ConcurrentDictionary<string, byte> RegularMeleeTypes = new(new Dictionary<string, byte>
    {
      { "Bites", 1 }, { "Claws", 1 }, { "Crushes", 1 }, { "Pierces", 1 }, { "Punches", 1 }, { "Slashes", 1 }
    });

    internal static PlayerStats CreatePlayerStats(Dictionary<string, PlayerStats> individualStats, string key, string origName = null)
    {
      PlayerStats stats;
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
      origName ??= name;
      var className = PlayerManager.Instance.GetPlayerClass(origName);

      return new PlayerStats
      {
        Name = string.Intern(name),
        ClassName = string.Intern(className),
        OrigName = string.Intern(origName),
        Percent = 100, // until something says otherwise
      };
    }

    internal static PlayerSubStats CreatePlayerSubStats(ICollection<PlayerSubStats> individualStats, string subType, string type)
    {
      var key = CreateRecordKey(type, subType);
      PlayerSubStats stats;

      lock (individualStats)
      {
        stats = individualStats.FirstOrDefault(indStats => indStats.Key == key);
        if (stats == null)
        {
          stats = new PlayerSubStats { ClassName = "", Name = string.Intern(subType), Type = string.Intern(type), Key = key };
          individualStats.Add(stats);
        }
      }

      return stats;
    }

    internal static string CreateRecordKey(string type, string subType)
    {
      var key = subType;

      if (type is Labels.Dd or Labels.Dot)
      {
        key = type + "=" + key;
      }

      return key;
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
        result = $"{Math.Round((decimal)total / 1000, roundTo)}K";
      }
      else if (total < 1000000000)
      {
        result = $"{Math.Round((decimal)total / 1000 / 1000, roundTo)}M";
      }
      else
      {
        result = $"{Math.Round((decimal)total / 1000 / 1000 / 1000, roundTo)}B";
      }

      return result;
    }

    internal static uint ParseUInt(string str, uint defValue = uint.MaxValue) => ParseUInt(str.AsSpan(), defValue);

    internal static uint ParseUInt(ReadOnlySpan<char> span, uint defValue = uint.MaxValue)
    {
      uint y = 0;

      foreach (var c in span)
      {
        if (!char.IsDigit(c))
        {
          return defValue;
        }

        y = (y * 10) + (uint)(c - '0');
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
      ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> playerSubTimeRanges, double minTime = double.NaN, double maxTime = double.NaN)
    {
      if (playerTimeRanges.TryGetValue(stats.Name, out var range))
      {
        var filteredRange = FilterTimeRange(range, minTime, maxTime);
        stats.TotalSeconds = filteredRange.GetTotal();
      }

      UpdateSubStatsTimeRanges(stats, playerSubTimeRanges, minTime, maxTime);
    }

    internal static void UpdateSubStatsTimeRanges(PlayerStats stats, ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> playerSubTimeRanges,
      double minTime = double.NaN, double maxTime = double.NaN)
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
        case Labels.Absorb:
          stats.Absorbs++;
          stats.MeleeAttempts++;
          parseModifiers = false;
          break;
        case Labels.Bane:
          stats.BaneHits++;
          stats.Hits += 1;
          break;
        case Labels.Block:
          stats.Blocks++;
          stats.MeleeAttempts++;
          break;
        case Labels.Dodge:
          stats.Dodges++;
          stats.MeleeAttempts++;
          break;
        case Labels.Miss:
          stats.Misses++;
          stats.MeleeAttempts++;
          break;
        case Labels.Parry:
          stats.Parries++;
          stats.MeleeAttempts++;
          break;
        case Labels.Riposte:  // defensive riposte
          stats.RiposteHits++;
          stats.MeleeAttempts++;
          break;
        case Labels.Invulnerable:
          stats.Invulnerable++;
          stats.MeleeAttempts++;
          break;
        case Labels.Proc:
        case Labels.Dot:
        case Labels.Dd:
          stats.SpellHits++;
          stats.Hits++;
          break;
        case Labels.Hot:
        case Labels.Heal:
          stats.SpellHits++;
          stats.Hits += 1;
          break;
        case Labels.Ds:
        case Labels.Rs:
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
        if (record.Type == Labels.Melee)
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

      var hitPotential = record.Total + record.OverTotal;
      stats.MaxPotentialHit = Math.Max(hitPotential, stats.MaxPotentialHit);

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
        to.MaxPotentialHit = Math.Max(to.MaxPotentialHit, from.MaxPotentialHit);
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

    internal static void CalculateRates(PlayerSubStats stats, PlayerStats raidStats, PlayerStats subStats)
    {
      if (stats.Hits > 0)
      {
        stats.Potential = stats.Total + stats.Extra;
        stats.Dps = (long)Math.Round(stats.Total / stats.TotalSeconds, 2);
        stats.Sdps = (long)Math.Round(stats.Total / raidStats.TotalSeconds, 2);
        stats.Pdps = (long)Math.Round(stats.Potential / stats.TotalSeconds, 2);
        stats.Avg = (long)Math.Round(Convert.ToDecimal(stats.Total) / stats.Hits, 2);

        if ((stats.CritHits - stats.LuckyHits) is var nonLucky and > 0)
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

        if (stats.Potential > 0)
        {
          stats.ExtraRate = (float)Math.Round((float)stats.Extra / stats.Potential * 100, 2);
        }

        if ((stats.Hits - stats.TwincastHits) is var nonTwincast and > 0)
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
          var tcMulti = stats.Type == Labels.Dd ? 2 : 1;
          stats.TwincastRate = (float)Math.Round((float)stats.TwincastHits / stats.SpellHits * tcMulti * 100, 2);
          stats.TwincastRate = (float)(stats.TwincastRate > 100.0 ? 100.0 : stats.TwincastRate);
          stats.ResistRate = (float)Math.Round((float)stats.Resists / (stats.SpellHits + stats.Resists) * 100, 2);
        }

        if (subStats is { Total: > 0 })
        {
          stats.Percent = (float)Math.Round((float)stats.Total / subStats.Total * 100, 2);
          stats.Sdps = (long)Math.Round(stats.Total / subStats.TotalSeconds, 2);
        }
        else if (subStats == null)
        {
          stats.Sdps = (long)Math.Round(stats.Total / raidStats.TotalSeconds, 2);
        }
      }
    }

    internal static void UpdateCalculations(PlayerSubStats stats, PlayerStats raidTotals,
      ConcurrentDictionary<string, int> resistCounts = null, PlayerStats superStats = null)
    {
      if (superStats != null)
      {
        if (resistCounts != null)
        {
          if (true)
          {

          }
        }
        if (resistCounts != null && resistCounts.TryGetValue(stats.Name, out var value))
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

    internal static void PopulateSpecials(PlayerStats raidStats, bool includeResists = false)
    {
      raidStats.ResistCounts.Clear();
      raidStats.Specials.Clear();
      raidStats.Deaths.Clear();

      var resistStart = 0;
      var specialStart = 0;
      var deathStart = 0;
      var allResists = includeResists ? RecordManager.Instance.GetAllResists().ToList() : new List<(double, ResistRecord)>();
      var allSpecials = RecordManager.Instance.GetAllSpecials().ToList();
      var allDeaths = RecordManager.Instance.GetAllDeaths().ToList();
      var temp = new HashSet<IAction>();
      var actions = new List<IAction>();

      foreach (var segment in raidStats.Ranges.TimeSegments)
      {
        actions.Clear();
        var offsetBegin = segment.BeginTime - SpecialOffset;
        var offsetEnd = segment.EndTime;
        if (specialStart > -1 && specialStart < allSpecials.Count)
        {
          specialStart = allSpecials.FindIndex(specialStart, special => special.Item1 >= offsetBegin);
          if (specialStart > -1)
          {
            for (var j = specialStart; j < allSpecials.Count; j++)
            {
              if (allSpecials[j].Item1 >= offsetBegin && allSpecials[j].Item1 <= segment.EndTime)
              {
                specialStart = j;
                actions.Add(allSpecials[j].Item2);
              }
            }
          }
        }

        offsetBegin = segment.BeginTime;
        offsetEnd = segment.EndTime + DeathOffset;
        if (deathStart > -1 && deathStart < allDeaths.Count)
        {
          deathStart = allDeaths.FindIndex(deathStart, death => death.Item1 >= offsetBegin);
          if (deathStart > -1)
          {
            for (var j = deathStart; j < allDeaths.Count; j++)
            {
              if (allDeaths[j].Item1 >= offsetBegin && allDeaths[j].Item1 <= offsetEnd)
              {
                deathStart = j;
                actions.Add(allDeaths[j].Item2);
                raidStats.Deaths.Add(new DeathEvent
                {
                  BeginTime = allDeaths[j].Item1,
                  Record = allDeaths[j].Item2
                });
              }
            }
          }
        }

        offsetBegin = segment.BeginTime;
        offsetEnd = segment.EndTime;
        if (resistStart > -1 && resistStart < allResists.Count)
        {
          resistStart = allResists.FindIndex(resistStart, resist => resist.Item1 >= offsetBegin);
          if (resistStart > -1)
          {
            for (var j = resistStart; j < allResists.Count; j++)
            {
              if (allResists[j].Item1 >= offsetBegin && allResists[j].Item1 <= offsetEnd)
              {
                resistStart = j;
                if (!raidStats.ResistCounts.TryGetValue(allResists[j].Item2.Attacker, out var perPlayer))
                {
                  perPlayer = new ConcurrentDictionary<string, int>();
                  raidStats.ResistCounts[allResists[j].Item2.Attacker] = perPlayer;
                }

                if (perPlayer.TryGetValue(allResists[j].Item2.Spell, out var currentCount))
                {
                  perPlayer[allResists[j].Item2.Spell] = currentCount + 1;
                }
                else
                {
                  perPlayer[allResists[j].Item2.Spell] = 1;
                }
              }
            }
          }
        }

        foreach (var action in actions)
        {
          if (!temp.Contains(action))
          {
            string code = null;
            string player = null;

            if (action is DeathRecord death)
            {
              player = death.Killed;
              code = "X";
            }
            else if (action is SpecialRecord spell)
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

            temp.Add(action);
          }
        }
      }
    }

    internal static TimeRange FilterTimeRange(TimeRange range, double minTime, double maxTime)
    {
      TimeRange result;

      if (!double.IsNaN(maxTime) || !double.IsNaN(minTime))
      {
        result = new TimeRange();
        range.TimeSegments.ForEach(segment =>
        {
          if ((double.IsNaN(minTime) || segment.BeginTime >= minTime) && (double.IsNaN(maxTime) || segment.EndTime <= maxTime))
          {
            result.Add(segment);
          }
          else if ((double.IsNaN(minTime) || segment.BeginTime >= minTime) && maxTime >= segment.BeginTime)
          {
            result.Add(new TimeSegment(segment.BeginTime, maxTime));
          }
          else if ((double.IsNaN(maxTime) || segment.EndTime <= maxTime) && minTime <= segment.EndTime)
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
          case Labels.Absorb:
          case Labels.Dodge:
          case Labels.Invulnerable:
          case Labels.Miss:
          case Labels.Parry:
          case Labels.Riposte:
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

      if (from == 0 && to > 0)
      {
        return to;
      }

      return Math.Min(to, from);
    }
  }
}
