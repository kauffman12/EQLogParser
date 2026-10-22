using System.Collections.Generic;

namespace EQLogParser;

internal readonly record struct ChartBucket(double Time, long Total, long Attempts, long Hits, long Crits, long Twins)
{
  internal ChartBucket Plus(ChartBucket other) => new(Time, Total + other.Total, Attempts + other.Attempts,
    Hits + other.Hits, Crits + other.Crits, Twins + other.Twins);
}

// Allocated only after an out-of-order timestamp. Keep per-time amounts, never records or synthetic gap markers.
// docs/DesignNotes.md → "Sparse chart buckets: cumulative correctness and calculation cost".
internal sealed class ChartSeriesRecovery(string name, string playerName, IReadOnlyList<DataPoint> points)
{
  private readonly Dictionary<double, ChartBucket> _buckets = Seed(points);
  private bool _dirty;

  internal void Add(ChartBucket bucket)
  {
    _buckets[bucket.Time] = _buckets.TryGetValue(bucket.Time, out var prior) ? prior.Plus(bucket) : bucket;
    _dirty = true;
  }

  internal void Finish(List<DataPoint> destination)
  {
    if (!_dirty) return;
    var ordered = new List<ChartBucket>(_buckets.Values);
    ordered.Sort(static (a, b) => a.Time.CompareTo(b.Time));
    var rebuilt = new ChartSeriesBuilder(name, playerName);
    foreach (var bucket in ordered) rebuilt.AddBucket(bucket);
    rebuilt.Finish();
    destination.Clear();
    destination.AddRange(rebuilt.Points);
    _dirty = false;
  }

  private static Dictionary<double, ChartBucket> Seed(IReadOnlyList<DataPoint> points)
  {
    var buckets = new Dictionary<double, ChartBucket>();
    foreach (var point in points)
    {
      if (point.AttemptsPerSecond == 0) continue;
      buckets.Add(point.CurrentTime, new ChartBucket(point.CurrentTime, point.TotalPerSecond,
        point.AttemptsPerSecond, point.HitsPerSecond, point.CritsPerSecond, point.TcPerSecond));
    }
    return buckets;
  }
}
