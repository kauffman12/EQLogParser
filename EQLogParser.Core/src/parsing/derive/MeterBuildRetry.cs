using System;
using System.Threading;

// Annotations only, no null-flow analysis: an answered scope may legitimately have no stats.
#nullable enable annotations

namespace EQLogParser;

internal readonly record struct MeterBuildResult
{
  internal static MeterBuildResult Deferred => new(true, null);

  internal bool IsDeferred { get; }
  internal DamageOverlayStats? Stats { get; }

  internal static MeterBuildResult Completed(DamageOverlayStats? stats = null) => new(false, stats);

  private MeterBuildResult(bool isDeferred, DamageOverlayStats? stats)
  {
    IsDeferred = isDeferred;
    Stats = stats;
  }
}

// Input churn may postpone an answer, but cannot keep one refresh retrying forever.
// docs/DesignNotes.md → "Incremental damage meter: compact accounting, paired publication".
internal static class MeterBuildRetry
{
  internal const int MaxAttempts = 3;

  internal static MeterBuildResult Run(Func<MeterBuildResult> attempt, CancellationToken cancellation = default)
  {
    for (var i = 0; i < MaxAttempts; i++)
    {
      cancellation.ThrowIfCancellationRequested();
      var result = attempt();
      cancellation.ThrowIfCancellationRequested();
      if (!result.IsDeferred) return result;
    }
    return MeterBuildResult.Deferred;
  }
}
