using System;
using System.Collections.Generic;

// Annotations only, no null-flow analysis: candidate discovery may end without an unambiguous fit.
#nullable enable annotations

namespace EQLogParser;

/// <summary>Validates proposals at the actual rounding allowance and retains only compatible DI assignments.</summary>
internal sealed class MeleeDiCandidates(IReadOnlyList<HitFrequencyBar> bars, long totalHits, MeleeDiPolicy policy)
{
  private readonly Dictionary<long, int> _wholeAgreed = [];
  private readonly Dictionary<long, int> _partialAgreed = [];
  private MeleeDiFit? _wholeBest;
  private MeleeDiFit? _partialBest;
  private bool _partialAmbiguous;
  private readonly HashSet<(decimal First, decimal Interval)> _seen = [];
  private readonly List<HitFrequencyBar> _representatives = new(MeleeDiEstimator.LevelCount);

  internal MeleeDiFit? Best => _wholeBest ?? (_partialAmbiguous ? null : _partialBest);
  internal bool Ambiguous { get; private set; }
  internal bool HasWhole => _wholeBest is not null;

  internal void Propose(decimal first, decimal interval)
  {
    // A proposal window is only for finding representatives. It never expands final matching precision.
    var proposalTolerance = 4 * policy.Tolerance(interval);
    for (var center = 0; center <= 1; center++)
    {
      FindRepresentatives(first, interval, proposalTolerance, center == 1);
      if (_representatives.Count < MeleeDiEstimator.MinimumObservedLevels) continue;
      var seed = MeleeDiEstimator.TryExact(_representatives, 0, policy);
      if (seed is null || !_seen.Add((seed.FirstAmount, seed.Interval))) continue;
      var fit = MatchAll(seed);
      if (fit is null) continue;
      // A whole-population explanation outranks a partial component; weaker conflicts must not erase it.
      if (fit.HitCount * 100 >= totalHits * MeleeDiPolicy.WholeCoveragePercent)
      {
        if (!RegisterLabels(fit, _wholeAgreed))
        {
          Ambiguous = true;
          return;
        }
        if (Better(fit, _wholeBest)) _wholeBest = fit;
      }
      else
      {
        if (!RegisterLabels(fit, _partialAgreed)) _partialAmbiguous = true;
        if (Better(fit, _partialBest)) _partialBest = fit;
      }
    }
  }

  private void FindRepresentatives(decimal first, decimal interval, decimal tolerance, bool center)
  {
    _representatives.Clear();
    for (var level = 0; level < MeleeDiEstimator.LevelCount; level++)
    {
      var predicted = first + (level * interval);
      var start = LowerBound(predicted - tolerance);
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
      if (_representatives.Count > 0 && bars[best].Amount <= _representatives[^1].Amount) continue;
      _representatives.Add(bars[best]);
    }
  }

  private MeleeDiFit? MatchAll(MeleeDiFit seed)
  {
    var levels = new Dictionary<long, int>();
    var hits = 0L;
    var residual = 0m;
    var lastLevelPeak = 0;
    var repeatedLevels = 0;
    var tolerance = policy.Tolerance(seed.Interval);
    for (var index = 0; index < MeleeDiEstimator.LevelCount; index++)
    {
      var predicted = seed.FirstAmount + (index * seed.Interval);
      var start = LowerBound(predicted - tolerance);
      var peak = 0;
      for (var i = start; i < bars.Count && bars[i].Amount <= predicted + tolerance; i++)
      {
        var bar = bars[i];
        levels.Add(bar.Amount, index + 1);
        hits += bar.Frequency;
        residual = Math.Max(residual, Math.Abs(bar.Amount - predicted));
        peak = Math.Max(peak, bar.Frequency);
      }
      if (peak >= 2) repeatedLevels++;
      if (index == MeleeDiEstimator.LevelCount - 1) lastLevelPeak = peak;
    }
    if (!policy.Supports(hits, totalHits, lastLevelPeak, repeatedLevels)) return null;
    var fit = new MeleeDiFit(seed.FirstAmount, seed.Interval, residual, hits, totalHits, tolerance, levels);
    return fit.ObservedLevels >= MeleeDiEstimator.MinimumObservedLevels
      && levels.ContainsValue(1) && levels.ContainsValue(MeleeDiEstimator.LevelCount) ? fit : null;
  }

  private static bool Better(MeleeDiFit fit, MeleeDiFit? best)
    => best is null || fit.HitCount > best.HitCount
      || (fit.HitCount == best.HitCount && fit.MaximumResidual < best.MaximumResidual);

  private bool RegisterLabels(MeleeDiFit fit, Dictionary<long, int> agreed)
  {
    for (var level = 0; level < MeleeDiEstimator.LevelCount; level++)
    {
      var predicted = fit.FirstAmount + (level * fit.Interval);
      var start = LowerBound(predicted - fit.MaximumResidual);
      for (var i = start; i < bars.Count && bars[i].Amount <= predicted + fit.MaximumResidual; i++)
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

  private int LowerBound(decimal amount)
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
