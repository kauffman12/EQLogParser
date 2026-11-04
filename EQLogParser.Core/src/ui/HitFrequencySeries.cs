using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser;

internal readonly record struct HitFrequencyBar(long Amount, int Frequency, long Difference);

/// <summary>A board breakdown's identity and exact distributions, independent of its selector label.</summary>
internal sealed class HitFrequencyChoice(PlayerSubStats source, string label)
{
  public string Label { get; } = label;
  internal string Key { get; } = source.Key;
  internal IReadOnlyList<HitFrequencyBar> Critical { get; } = HitFrequencySeries.Prepare(source.CritFreqValues);
  internal IReadOnlyList<HitFrequencyBar> NonCritical { get; } = HitFrequencySeries.Prepare(source.NonCritFreqValues);

  internal IReadOnlyList<HitFrequencyBar> Values(bool critical) => critical ? Critical : NonCritical;
}

/// <summary>Prepares chart-owned exact amounts without changing board grouping or histogram ownership.</summary>
internal static class HitFrequencySeries
{
  internal static Dictionary<string, List<HitFrequencyChoice>> ForPlayers(PlayerStats selected, CombinedStats combined)
  {
    var results = new Dictionary<string, List<HitFrequencyChoice>>();
    if (combined?.Children.TryGetValue(selected.Name, out var children) == true)
    {
      foreach (var child in children) results[child.Name] = Choices(child);
    }
    else
    {
      results[selected.Name] = Choices(selected);
    }
    return results;
  }

  internal static HitFrequencyBar[] Prepare(IReadOnlyDictionary<long, int> frequencies)
  {
    var bars = new HitFrequencyBar[frequencies.Count];
    var index = 0;
    var previous = 0L;
    foreach (var (amount, frequency) in frequencies.OrderBy(pair => pair.Key))
    {
      bars[index] = new HitFrequencyBar(amount, frequency, index == 0 ? 0 : amount - previous);
      previous = amount;
      index++;
    }
    return bars;
  }

  // Differences belong to the complete sorted series, never to the filtered rows or a displayed page.
  internal static List<HitFrequencyBar> Filter(IReadOnlyList<HitFrequencyBar> bars, int minimumFrequency)
    => bars.Where(bar => bar.Frequency > minimumFrequency).ToList();

  private static List<HitFrequencyChoice> Choices(PlayerStats stats)
  {
    var duplicateNames = stats.SubStats.GroupBy(row => row.Name, StringComparer.Ordinal)
      .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
    return stats.SubStats.Select(row => new HitFrequencyChoice(row,
      duplicateNames.Contains(row.Name) ? $"{row.Name} — {row.Type}" : row.Name)).ToList();
  }
}
