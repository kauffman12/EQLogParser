using System;
using System.Collections.Generic;

namespace EQLogParser;

/// <summary>Plots six-second rates at arrivals and expirations, bounded by the selected data's log-time edge.</summary>
internal static class ChartRollingSeries
{
  internal static List<DataPoint> Build(IReadOnlyList<DataPoint> source, double through)
  {
    var result = new List<DataPoint>();
    if (source.Count == 0 || !double.IsFinite(through)) return result;
    through = Math.Floor(through);
    var start = Math.Floor(source[0].CurrentTime);
    if (start > through) return result;

    var added = 0;
    var expired = 0;
    var total = 0L;
    Append(result, source[0], start, total);
    while (true)
    {
      // Original gap markers and zero outcomes carry no rolling amount. They must not create extra transitions.
      while (added < source.Count && source[added].TotalPerSecond == 0) added++;
      while (expired < source.Count && source[expired].TotalPerSecond == 0) expired++;
      var nextAdd = added < source.Count ? Math.Floor(source[added].CurrentTime) : double.PositiveInfinity;
      var nextExpiry = expired < source.Count
        ? Math.Floor(source[expired].CurrentTime) + ChartAggregation.RollingSeconds : double.PositiveInfinity;
      var time = Math.Min(nextAdd, nextExpiry);
      if (time > through) break;

      while (expired < source.Count && Math.Floor(source[expired].CurrentTime) + ChartAggregation.RollingSeconds <= time)
        total -= source[expired++].TotalPerSecond;
      while (added < source.Count && Math.Floor(source[added].CurrentTime) <= time)
        total += source[added++].TotalPerSecond;

      var previous = result[^1];
      if (previous.CurrentTime == time)
      {
        previous.RollingTotal = total;
        previous.RollingDps = StatsUtil.RateOverWholeSeconds(total, ChartAggregation.RollingSeconds);
      }
      else if (previous.RollingTotal != total)
      {
        // Hold the old value through the preceding second; a long connecting diagonal would hide the quiet trough.
        if (previous.CurrentTime < time - 1) Append(result, source[0], time - 1, previous.RollingTotal);
        Append(result, source[0], time, total);
      }
    }

    if (result[^1].CurrentTime < through) Append(result, source[0], through, total);
    return result;
  }

  private static void Append(List<DataPoint> result, DataPoint identity, double time, long total) => result.Add(new DataPoint
  {
    Name = identity.Name,
    PlayerName = identity.PlayerName,
    CurrentTime = time,
    DateTime = DateUtil.FromDotNetSeconds(time),
    RollingTotal = total,
    RollingDps = StatsUtil.RateOverWholeSeconds(total, ChartAggregation.RollingSeconds)
  });
}
