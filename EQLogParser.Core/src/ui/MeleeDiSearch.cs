using System;
using System.Collections.Generic;
using System.Linq;

// Annotations only, no null-flow analysis: an ambiguous or unsupported ladder is not a fit.
#nullable enable annotations

namespace EQLogParser;

/// <summary>Proposes ladders from observed anchors, then checks support against the full sorted histogram.</summary>
internal static class MeleeDiSearch
{
  private const int MinimumHitCoveragePercent = 98;

  internal static MeleeDiFit? TryFit(IReadOnlyList<HitFrequencyBar> bars, long hits, decimal tolerance)
  {
    // A +/- tolerance window contains at most 2*tolerance+1 integer amounts per level.
    // This is a support bound, not an input-size cutoff: every bin participates in the census.
    var capacity = MeleeDiEstimator.LevelCount * ((int)(2 * tolerance) + 1);
    var peaks = Strongest(bars, capacity);
    if (peaks.Sum(bar => (long)bar.Frequency) * 100 < hits * MinimumHitCoveragePercent) return null;
    var allowedMisses = hits - ((hits * MinimumHitCoveragePercent + 99) / 100);
    var anchorHits = 0L;
    MeleeDiFit? best = null;
    var agreed = new Dictionary<long, int>();
    var seen = new HashSet<(decimal First, decimal Interval)>();
    var representatives = new List<HitFrequencyBar>(MeleeDiEstimator.LevelCount);
    foreach (var anchor in peaks)
    {
      foreach (var endpoint in bars)
      {
        if (endpoint.Amount == anchor.Amount) continue;
        // Every level is at least ten steps from one of the two ends. Avoid unstable short-baseline extrapolation.
        for (var distance = MeleeDiEstimator.LevelCount / 2; distance < MeleeDiEstimator.LevelCount; distance++)
        {
          var interval = Math.Abs((decimal)endpoint.Amount - anchor.Amount) / distance;
          if (interval < 0.5m) continue;
          var first = endpoint.Amount < anchor.Amount ? endpoint.Amount
            : endpoint.Amount - ((MeleeDiEstimator.LevelCount - 1) * interval);
          // Endpoint rounding can amplify to less than 3*tolerance across the extrapolated ladder.
          if (!CouldCover(peaks, first, interval, tolerance * 3, allowedMisses)) continue;
          // Try both the modal amount and the center of a rounded cluster; neither can grant a label itself.
          for (var center = 0; center <= 1; center++)
          {
            FindRepresentatives(bars, first, interval, tolerance * 3, center == 1, representatives);
            if (representatives.Count < MeleeDiEstimator.MinimumObservedLevels) continue;
            var seed = MeleeDiEstimator.TryExact(representatives, 0, tolerance);
            if (seed is null || !seen.Add((seed.FirstAmount, seed.Interval))) continue;
            var fit = MatchAll(bars, seed, hits, tolerance);
            if (fit is null) continue;
            if (!RegisterLabels(bars, agreed, fit)) return null;
            if (best is null || fit.HitCount > best.HitCount
              || (fit.HitCount == best.HitCount && fit.MaximumResidual < best.MaximumResidual)) best = fit;
          }
        }
      }
      anchorHits += anchor.Frequency;
      // Every acceptable ladder must include at least one of these anchors, even when its rare ends are far away.
      if (anchorHits > allowedMisses) break;
    }
    return best;
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

  private static void FindRepresentatives(IReadOnlyList<HitFrequencyBar> bars, decimal first, decimal interval,
    decimal tolerance, bool center, List<HitFrequencyBar> representatives)
  {
    representatives.Clear();
    for (var level = 0; level < MeleeDiEstimator.LevelCount; level++)
    {
      var predicted = first + (level * interval);
      var start = LowerBound(bars, predicted - tolerance);
      var end = start;
      var weightedOffset = 0m;
      var hits = 0L;
      while (end < bars.Count && bars[end].Amount <= predicted + tolerance)
      {
        weightedOffset += (bars[end].Amount - predicted) * bars[end].Frequency;
        hits += bars[end].Frequency;
        end++;
      }
      var target = center && hits > 0 ? predicted + (weightedOffset / hits) : predicted;
      var best = -1;
      for (var i = start; i < end; i++)
      {
        var nearer = best < 0 || Math.Abs(bars[i].Amount - target) < Math.Abs(bars[best].Amount - target);
        if (best < 0 || (center ? nearer : bars[i].Frequency > bars[best].Frequency
          || (bars[i].Frequency == bars[best].Frequency && nearer))) best = i;
      }
      if (best < 0) continue;
      // Overlapping proposal windows must not assign a single amount to two levels.
      if (representatives.Count > 0 && bars[best].Amount <= representatives[^1].Amount) continue;
      representatives.Add(bars[best]);
    }
  }

  private static MeleeDiFit? MatchAll(IReadOnlyList<HitFrequencyBar> bars, MeleeDiFit seed, long totalHits,
    decimal roundingTolerance)
  {
    var levels = new Dictionary<long, int>();
    var hits = 0L;
    var residual = 0m;
    var tolerance = Math.Min(roundingTolerance, seed.Interval / 4);
    for (var index = 0; index < MeleeDiEstimator.LevelCount; index++)
    {
      var predicted = seed.FirstAmount + (index * seed.Interval);
      var start = LowerBound(bars, predicted - tolerance);
      for (var i = start; i < bars.Count && bars[i].Amount <= predicted + tolerance; i++)
      {
        var bar = bars[i];
        levels.Add(bar.Amount, index + 1);
        hits += bar.Frequency;
        residual = Math.Max(residual, Math.Abs(bar.Amount - predicted));
      }
    }
    if (hits < MeleeDiEstimator.MinimumHits || hits * 100 < totalHits * MinimumHitCoveragePercent) return null;
    var fit = new MeleeDiFit(seed.FirstAmount, seed.Interval, residual, hits, totalHits, levels);
    return fit.ObservedLevels >= MeleeDiEstimator.MinimumObservedLevels
      && levels.ContainsValue(1) && levels.ContainsValue(MeleeDiEstimator.LevelCount) ? fit : null;
  }

  private static bool RegisterLabels(IReadOnlyList<HitFrequencyBar> bars, Dictionary<long, int> agreed, MeleeDiFit fit)
  {
    // Only matched windows need examining; do not rescan a large source for each accepted hypothesis.
    for (var level = 0; level < MeleeDiEstimator.LevelCount; level++)
    {
      var predicted = fit.FirstAmount + (level * fit.Interval);
      var tolerance = fit.MaximumResidual;
      var start = LowerBound(bars, predicted - tolerance);
      for (var i = start; i < bars.Count && bars[i].Amount <= predicted + tolerance; i++)
      {
        var amount = bars[i].Amount;
        var label = fit.LevelFor(amount);
        if (label == 0) continue;
        if (agreed.TryGetValue(amount, out var previous) && previous != label) return false;
        agreed[amount] = label;
      }
    }
    return true;
  }

  private static int LowerBound(IReadOnlyList<HitFrequencyBar> bars, decimal amount)
  {
    var low = 0;
    var high = bars.Count;
    while (low < high)
    {
      var middle = low + ((high - low) / 2);
      if (bars[middle].Amount < amount) low = middle + 1;
      else high = middle;
    }
    return low;
  }
}
