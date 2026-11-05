using System;

namespace EQLogParser;

/// <summary>Critical components may be partial and scale their rounding budget; ordinary hits retain strict coverage.</summary>
internal readonly record struct MeleeDiPolicy(bool Critical)
{
  internal const int WholeCoveragePercent = 98;
  internal int MinimumCoveragePercent => Critical ? 40 : WholeCoveragePercent;

  internal decimal Tolerance(decimal interval)
    => Math.Min(interval / 4, Critical ? Math.Max(1m, ((MeleeDiEstimator.LevelCount - 1) * interval) / 10000) : 1m);

  // Observed endpoint errors can enlarge the fitted span by at most two rounding allowances.
  internal decimal SearchTolerance(long observedSpan)
    => Critical ? Math.Max(1m, observedSpan / 9998m) : 1m;

  internal bool CanAnchorPartial(int frequency, long totalHits)
    => Critical && frequency >= 5 && frequency * 1000L >= totalHits * MinimumCoveragePercent;

  internal bool Supports(long matchedHits, long totalHits, int lastLevelPeak,
    int repeatedLevels = MeleeDiEstimator.MinimumObservedLevels)
  {
    if (matchedHits < MeleeDiEstimator.MinimumHits
      || matchedHits * 100 < totalHits * MinimumCoveragePercent) return false;
    // A partial critical ladder needs a repeated high-end spike, not a singleton borrowed from a dense modifier band.
    return matchedHits * 100 >= totalHits * WholeCoveragePercent
      || (Critical && repeatedLevels >= MeleeDiEstimator.MinimumObservedLevels
        && lastLevelPeak >= 5 && lastLevelPeak * 10L >= matchedHits);
  }
}
