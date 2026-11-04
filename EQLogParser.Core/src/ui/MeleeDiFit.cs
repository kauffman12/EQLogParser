using System.Collections.Generic;
using System.Numerics;

namespace EQLogParser;

/// <summary>An estimated ladder over observed amounts only; unmatched values never acquire invented labels.</summary>
internal sealed class MeleeDiFit
{
  private readonly Dictionary<long, int> _levels;

  internal decimal FirstAmount { get; }
  internal decimal Interval { get; }
  internal decimal DamageBonus => FirstAmount - Interval;
  internal decimal MaximumResidual { get; }
  internal int ObservedLevels { get; }
  internal int MatchedAmounts => _levels.Count;
  internal long HitCount { get; }
  internal long TotalHitCount { get; }

  internal MeleeDiFit(decimal firstAmount, decimal interval, decimal maximumResidual, long hitCount,
    long totalHitCount, Dictionary<long, int> levels)
  {
    FirstAmount = firstAmount;
    Interval = interval;
    MaximumResidual = maximumResidual;
    HitCount = hitCount;
    TotalHitCount = totalHitCount;
    _levels = levels;
    var observed = 0u;
    foreach (var level in levels.Values) observed |= 1u << (level - 1);
    ObservedLevels = BitOperations.PopCount(observed);
  }

  internal int LevelFor(long amount) => _levels.GetValueOrDefault(amount);
}
