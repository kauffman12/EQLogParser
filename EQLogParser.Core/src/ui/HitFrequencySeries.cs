using System;
using System.Collections.Generic;
using System.Linq;

// Annotations only, no null-flow analysis: a melee distribution may have no defensible DI fit.
#nullable enable annotations

namespace EQLogParser;

internal readonly record struct HitFrequencyBar(long Amount, int Frequency, long Difference);

/// <summary>A board breakdown's identity and exact distributions, independent of its selector label.</summary>
internal sealed class HitFrequencyChoice
{
  private readonly bool _isMelee;
  private MeleeDiFit? _criticalDiFit;
  private MeleeDiFit? _nonCriticalDiFit;
  private bool _criticalDiComputed;
  private bool _nonCriticalDiComputed;

  public string? Label { get; }
  internal string? Key { get; }
  internal IReadOnlyList<HitFrequencyBar> Critical { get; }
  internal IReadOnlyList<HitFrequencyBar> NonCritical { get; }

  internal HitFrequencyChoice(PlayerSubStats source, string? label)
  {
    Label = label;
    Key = source.Key;
    Critical = HitFrequencySeries.Prepare(source.CritFreqValues);
    NonCritical = HitFrequencySeries.Prepare(source.NonCritFreqValues);
    _isMelee = source.Type == Labels.Melee;
  }

  internal IReadOnlyList<HitFrequencyBar> Values(bool critical) => critical ? Critical : NonCritical;
  // Explicit analysis only; opening a chart must not run the disabled DI estimator.
  internal MeleeDiFit? DiFor(bool critical)
  {
    if (!_isMelee) return null;
    if (critical)
    {
      if (!_criticalDiComputed)
      {
        _criticalDiFit = MeleeDiEstimator.TryFit(Critical, critical: true);
        _criticalDiComputed = true;
      }
      return _criticalDiFit;
    }
    if (!_nonCriticalDiComputed)
    {
      _nonCriticalDiFit = MeleeDiEstimator.TryFit(NonCritical);
      _nonCriticalDiComputed = true;
    }
    return _nonCriticalDiFit;
  }
}

/// <summary>Prepares chart-owned exact amounts without changing board grouping or histogram ownership.</summary>
internal static class HitFrequencySeries
{
  internal static Dictionary<string, List<HitFrequencyChoice>> ForPlayers(PlayerStats selected, CombinedStats? combined)
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
