using System;
using System.Collections.Generic;

// Annotations only, no null-flow analysis: declining an uncertain distribution is the normal result.
#nullable enable annotations

namespace EQLogParser;

/// <summary>Declines noisy or offset-ambiguous data rather than manufacturing a 1–20 interpretation.</summary>
internal static class MeleeDiEstimator
{
  internal const int LevelCount = 20;
  private const int MinimumObservedLevels = 16;
  private const long MinimumHits = 50;
  private const int MinimumHitCoveragePercent = 98;
  private const int MinimumAmountCoveragePercent = 90;
  private const int MaximumAmounts = LevelCount * 100 / MinimumAmountCoveragePercent;

  // Fit the complete prepared series, never a viewport or a minimum-frequency subset.
  // Policy and measured examples: docs/DesignNotes.md → "DI inference: measured melee ladders, not automatic numbering".
  internal static MeleeDiFit? TryFit(IReadOnlyList<HitFrequencyBar> bars, bool critical = false)
  {
    if (bars.Count < MinimumObservedLevels || bars.Count > MaximumAmounts) return null;
    var hits = 0L;
    var previous = 0L;
    foreach (var bar in bars)
    {
      if (bar.Amount <= previous || !TextUtils.InAmountRange(bar.Amount) || bar.Frequency <= 0) return null;
      previous = bar.Amount;
      hits += bar.Frequency;
    }
    if (hits < MinimumHits) return null;

    // Critical multipliers amplify integer rounding; the quarter-interval cap still separates adjacent levels.
    var roundingTolerance = critical ? 3m : 1m;
    return TryExact(bars, hits, roundingTolerance) ?? TryDominant(bars, hits, roundingTolerance);
  }

  private static MeleeDiFit? TryExact(IReadOnlyList<HitFrequencyBar> bars, long hits, decimal roundingTolerance)
  {
    var indices = new int[bars.Count];
    for (var span = bars.Count - 1; span < LevelCount; span++)
    {
      if (!TryCandidate(bars, span, indices, roundingTolerance, out var first, out var interval, out var residual))
      {
        continue;
      }
      // Require observed ends; do not assign an absolute DI offset from a shorter supported ladder.
      if (span != LevelCount - 1) return null;
      var levels = new Dictionary<long, int>(bars.Count);
      for (var i = 0; i < bars.Count; i++) levels.Add(bars[i].Amount, indices[i] + 1);
      return new MeleeDiFit(first, interval, residual, hits, hits, levels);
    }
    return null;
  }

  private static MeleeDiFit? TryDominant(IReadOnlyList<HitFrequencyBar> bars, long hits, decimal roundingTolerance)
  {
    MeleeDiFit? best = null;
    var agreed = new Dictionary<long, int>();
    // The amount-coverage floor bounds the search to at most 22 bins and two seed omissions.
    for (var first = 0; first < bars.Count; first++)
    {
      for (var second = first; second < bars.Count; second++)
      {
        var kept = bars.Count - (first == second ? 1 : 2);
        if (kept < MinimumObservedLevels || kept > LevelCount
          || kept * 100 < bars.Count * MinimumAmountCoveragePercent) continue;
        var subset = new HitFrequencyBar[kept];
        var index = 0;
        var seedHits = 0L;
        for (var i = 0; i < bars.Count; i++)
        {
          if (i == first || i == second) continue;
          subset[index++] = bars[i];
          seedHits += bars[i].Frequency;
        }
        if (TryExact(subset, seedHits, roundingTolerance) is not { } seed) continue;
        var fit = MatchAll(bars, seed, hits, roundingTolerance);
        if (fit is null) continue;
        if (!RegisterLabels(bars, agreed, fit)) return null;
        if (best is null || fit.HitCount > best.HitCount
          || (fit.HitCount == best.HitCount && fit.MaximumResidual < best.MaximumResidual)) best = fit;
      }
    }
    return best;
  }

  private static MeleeDiFit? MatchAll(IReadOnlyList<HitFrequencyBar> bars, MeleeDiFit seed, long totalHits,
    decimal roundingTolerance)
  {
    var levels = new Dictionary<long, int>();
    var hits = 0L;
    var residual = 0m;
    var tolerance = Math.Min(roundingTolerance, seed.Interval / 4);
    foreach (var bar in bars)
    {
      var index = decimal.Round((bar.Amount - seed.FirstAmount) / seed.Interval, 0, MidpointRounding.AwayFromZero);
      if (index < 0 || index >= LevelCount) continue;
      var error = Math.Abs(bar.Amount - (seed.FirstAmount + (index * seed.Interval)));
      if (error > tolerance) continue;
      levels.Add(bar.Amount, (int)index + 1);
      hits += bar.Frequency;
      residual = Math.Max(residual, error);
    }
    if (hits < MinimumHits || hits * 100 < totalHits * MinimumHitCoveragePercent
      || levels.Count * 100 < bars.Count * MinimumAmountCoveragePercent) return null;
    return new MeleeDiFit(seed.FirstAmount, seed.Interval, residual, hits, totalHits, levels);
  }

  private static bool RegisterLabels(IReadOnlyList<HitFrequencyBar> bars, Dictionary<long, int> agreed, MeleeDiFit fit)
  {
    foreach (var bar in bars)
    {
      var level = fit.LevelFor(bar.Amount);
      if (level == 0) continue;
      if (agreed.TryGetValue(bar.Amount, out var previous) && previous != level) return false;
      agreed[bar.Amount] = level;
    }
    return true;
  }

  private static bool TryCandidate(IReadOnlyList<HitFrequencyBar> bars, int span, int[] indices,
    decimal roundingTolerance, out decimal first, out decimal interval, out decimal residual)
  {
    first = interval = residual = 0;
    var origin = bars[0].Amount;
    var approximate = (decimal)(bars[^1].Amount - origin) / span;
    if (approximate < 1) return false;
    var sumX = 0m;
    var sumY = 0m;
    var sumXX = 0m;
    var sumXY = 0m;
    var previous = -1;
    for (var i = 0; i < bars.Count; i++)
    {
      var offset = bars[i].Amount - origin;
      var index = (int)decimal.Round(offset / approximate, 0, MidpointRounding.AwayFromZero);
      if (index <= previous || index > span) return false;
      indices[i] = index;
      previous = index;
      sumX += index;
      sumY += offset;
      sumXX += index * index;
      sumXY += (decimal)index * offset;
    }

    // Centered decimal arithmetic keeps one-point rounding errors meaningful even at the 48-bit amount ceiling.
    interval = ((bars.Count * sumXY) - (sumX * sumY)) / ((bars.Count * sumXX) - (sumX * sumX));
    first = origin + ((sumY - (interval * sumX)) / bars.Count);
    if (interval < 1 || first - interval < 0) return false;
    var tolerance = Math.Min(roundingTolerance, interval / 4);
    for (var i = 0; i < bars.Count; i++)
    {
      residual = Math.Max(residual, Math.Abs(bars[i].Amount - (first + (indices[i] * interval))));
      if (residual > tolerance) return false;
    }
    return true;
  }
}
