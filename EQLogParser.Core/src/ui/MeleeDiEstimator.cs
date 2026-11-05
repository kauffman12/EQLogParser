using System;
using System.Collections.Generic;

// Annotations only, no null-flow analysis: declining an uncertain distribution is the normal result.
#nullable enable annotations

namespace EQLogParser;

/// <summary>Declines noisy or offset-ambiguous data rather than manufacturing a 1–20 interpretation.</summary>
internal static class MeleeDiEstimator
{
  internal const int LevelCount = 20;
  internal const int MinimumObservedLevels = 16;
  internal const long MinimumHits = 50;

  // Fit the complete prepared series, never a viewport or a minimum-frequency subset.
  // Policy and measured examples: docs/DesignNotes.md → "DI inference: measured melee ladders, not automatic numbering".
  internal static MeleeDiFit? TryFit(IReadOnlyList<HitFrequencyBar> bars, bool critical = false)
  {
    if (bars.Count < MinimumObservedLevels) return null;
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
    return TryExact(bars, hits, roundingTolerance) ?? MeleeDiSearch.TryFit(bars, hits, roundingTolerance);
  }

  internal static MeleeDiFit? TryExact(IReadOnlyList<HitFrequencyBar> bars, long hits, decimal roundingTolerance)
  {
    if (bars.Count < MinimumObservedLevels || bars.Count > LevelCount) return null;
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
