using System;
using System.Collections.Generic;
using System.Linq;

// Annotations only, no null-flow analysis: an ambiguous or unsupported ladder is not a fit.
#nullable enable annotations

namespace EQLogParser;

/// <summary>Proposes ladders from observed anchors, then checks support against the full sorted histogram.</summary>
internal static class MeleeDiSearch
{
  internal static MeleeDiFit? TryFit(IReadOnlyList<HitFrequencyBar> bars, long hits, MeleeDiPolicy policy)
  {
    var maximumTolerance = policy.SearchTolerance(bars[^1].Amount - bars[0].Amount);
    var capacity = (int)Math.Min(bars.Count, MeleeDiEstimator.LevelCount * (decimal.Floor(2 * maximumTolerance) + 1));
    var peaks = Strongest(bars, capacity);
    var candidates = new MeleeDiCandidates(bars, hits, policy);
    var allowedMisses = hits - ((hits * MeleeDiPolicy.WholeCoveragePercent + 99) / 100);
    if (peaks.Sum(bar => (long)bar.Frequency) * 100 >= hits * MeleeDiPolicy.WholeCoveragePercent)
    {
      var anchorHits = 0L;
      foreach (var anchor in peaks)
      {
        foreach (var endpoint in bars)
        {
          if (endpoint.Amount == anchor.Amount) continue;
          for (var distance = MeleeDiEstimator.LevelCount / 2; distance < MeleeDiEstimator.LevelCount; distance++)
          {
            var interval = Math.Abs((decimal)endpoint.Amount - anchor.Amount) / distance;
            if (interval < 0.5m) continue;
            var first = endpoint.Amount < anchor.Amount ? endpoint.Amount
              : endpoint.Amount - ((MeleeDiEstimator.LevelCount - 1) * interval);
            if (!CouldCover(peaks, first, interval, 4 * policy.Tolerance(interval), allowedMisses)) continue;
            candidates.Propose(first, interval);
            if (candidates.Ambiguous) return null;
          }
        }
        anchorHits += anchor.Frequency;
        if (anchorHits > allowedMisses) break;
      }
    }

    if (policy.Critical && !candidates.HasWhole)
    {
      var partialMisses = hits - ((hits * policy.MinimumCoveragePercent + 99) / 100);
      foreach (var high in bars)
      {
        // Partial critical components need a repeated DI20 spike; isolated values in a lucky band cannot anchor one.
        if (!policy.CanAnchorPartial(high.Frequency, hits)) continue;
        foreach (var low in bars)
        {
          if (low.Amount >= high.Amount) break;
          var interval = (decimal)(high.Amount - low.Amount) / (MeleeDiEstimator.LevelCount - 1);
          if (interval < 0.5m) continue;
          if (!CouldCover(peaks, low.Amount, interval, 4 * policy.Tolerance(interval), partialMisses)) continue;
          candidates.Propose(low.Amount, interval);
          if (candidates.Ambiguous) return null;
        }
      }
    }
    return candidates.Best;
  }

  private static HitFrequencyBar[] Strongest(IReadOnlyList<HitFrequencyBar> bars, int capacity)
  {
    var queue = new PriorityQueue<HitFrequencyBar, (int Frequency, long Amount)>();
    foreach (var bar in bars)
    {
      queue.Enqueue(bar, (bar.Frequency, -bar.Amount));
      if (queue.Count > capacity) queue.Dequeue();
    }
    return queue.UnorderedItems.Select(item => item.Element).OrderByDescending(bar => bar.Frequency)
      .ThenBy(bar => bar.Amount).ToArray();
  }

  private static bool CouldCover(HitFrequencyBar[] peaks, decimal first, decimal interval,
    decimal tolerance, long allowedMisses)
  {
    var misses = 0L;
    foreach (var peak in peaks)
    {
      var index = Math.Clamp(decimal.Round((peak.Amount - first) / interval), 0, MeleeDiEstimator.LevelCount - 1);
      if (Math.Abs(peak.Amount - (first + (index * interval))) <= tolerance) continue;
      misses += peak.Frequency;
      if (misses > allowedMisses) return false;
    }
    return true;
  }
}
