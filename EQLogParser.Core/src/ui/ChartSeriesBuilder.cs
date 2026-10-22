using log4net;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace EQLogParser;

/// <summary>Folds records into ordered sparse chart buckets without retaining the input records.</summary>
internal sealed class ChartSeriesBuilder(string name, string playerName = null)
{
  private const int GapMarkerOffsetSeconds = 6;

  private readonly DataPoint _current = new() { Name = name, PlayerName = playerName };
  private readonly ChartActivityClock _clock = new();
  private bool _pending;
  private ChartSeriesRecovery _recovery;

  internal List<DataPoint> Points { get; } = [];

  internal void Add(DataPoint record, bool isCrit, bool isTwincast, bool isHit)
  {
    var bucket = new ChartBucket(record.CurrentTime, record.Total, 1, isHit ? 1 : 0,
      isHit && isCrit ? 1 : 0, isHit && isTwincast ? 1 : 0);
    if (_recovery is null && _clock.Seconds > 0 && record.CurrentTime < _current.CurrentTime)
    {
      Finish();
      _recovery = new ChartSeriesRecovery(_current.Name, _current.PlayerName, Points);
    }
    if (_recovery is not null)
    {
      _clock.Add(record.CurrentTime);
      _recovery.Add(bucket);
      return;
    }
    AddBucket(bucket);
  }

  internal void Finish()
  {
    if (_recovery is not null)
    {
      _recovery.Finish(Points);
      return;
    }
    if (!_pending) return;
    var point = Snapshot(_current.CurrentTime, false);
    if (Points.Count > 0 && Points[^1].CurrentTime == point.CurrentTime) Points[^1] = point;
    else Points.Add(point);
    _pending = false;
  }

  internal static void SetRates(DataPoint point, double seconds)
  {
    point.ValuePerSecond = seconds > 0 ? StatsUtil.RatePerSecond(point.FightTotal, seconds) : 0;
    if (point.FightHits == 0) return;
    point.Avg = (long)Math.Round((decimal)point.FightTotal / point.FightHits, 2);
    point.CritRate = Math.Round((double)point.FightCritHits / point.FightHits * 100, 2);
    point.TcRate = Math.Round((double)point.FightTcHits / point.FightHits * 100, 2);
  }

  // The ordinary stream and the rare reordered rebuild use exactly the same bucket fold.
  internal void AddBucket(ChartBucket bucket)
  {
    if (_clock.Seconds > 0 && bucket.Time != _current.CurrentTime)
    {
      Finish();
      if (bucket.Time - _current.CurrentTime > FightProjection.EngagementGapS)
      {
        Points.Add(Snapshot(_current.CurrentTime + GapMarkerOffsetSeconds, true));
        Points.Add(Snapshot(bucket.Time - GapMarkerOffsetSeconds, true));
      }
      _current.TotalPerSecond = 0;
      _current.AttemptsPerSecond = 0;
      _current.HitsPerSecond = 0;
      _current.CritsPerSecond = 0;
      _current.TcPerSecond = 0;
    }
    _clock.Add(bucket.Time);
    _current.CurrentTime = bucket.Time;
    _current.Total += bucket.Total;
    _current.FightTotal += bucket.Total;
    _current.TotalPerSecond += bucket.Total;
    _current.AttemptsPerSecond += bucket.Attempts;
    _current.HitsPerSecond += bucket.Hits;
    _current.FightHits += (uint)bucket.Hits;
    _current.CritsPerSecond += bucket.Crits;
    _current.FightCritHits += (uint)bucket.Crits;
    _current.TcPerSecond += bucket.Twins;
    _current.FightTcHits += (uint)bucket.Twins;
    _pending = true;
  }

  private DataPoint Snapshot(double time, bool gap)
  {
    var point = new DataPoint
    {
      Name = _current.Name,
      PlayerName = _current.PlayerName,
      CurrentTime = time,
      DateTime = DateUtil.FromDotNetSeconds(time),
      Total = _current.Total,
      FightTotal = _current.FightTotal,
      FightHits = _current.FightHits,
      FightCritHits = _current.FightCritHits,
      FightTcHits = _current.FightTcHits,
      TotalPerSecond = gap ? 0 : _current.TotalPerSecond,
      AttemptsPerSecond = gap ? 0 : _current.AttemptsPerSecond,
      HitsPerSecond = gap ? 0 : _current.HitsPerSecond,
      CritsPerSecond = gap ? 0 : _current.CritsPerSecond,
      TcPerSecond = gap ? 0 : _current.TcPerSecond
    };
    SetRates(point, _clock.Seconds);
    return point;
  }
}

/*
 * Sorted buckets need only the previous timestamp. Refuse backwards clock movement without aborting the paint path;
 * report the first offending pair and count repeats. ChartSeriesBuilderTest.OutOfOrderRecordsDegradeTheClockInsteadOfThrowing
 * pins the unchanged denominator and count. docs/DesignNotes.md → "Sparse chart buckets: cumulative correctness and calculation cost".
 */
internal sealed class ChartActivityClock
{
  private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

  private double _last = double.NaN;
  internal double Seconds { get; private set; }

  /// <summary>Timestamp regressions refused by this clock; the series builder preserves and reorders their amounts.</summary>
  internal int OutOfOrder { get; private set; }

  internal void Add(double time)
  {
    if (!double.IsNaN(_last) && time < _last)
    {
      if (OutOfOrder++ == 0)
        Log.Warn($"chart clock: a record at {time:0.###} arrived behind this series' newest second ({_last:0.###}) "
                 + "- the activity clock keeps its last second; further out-of-order records are counted, not printed");
      return;
    }

    var gap = time - _last;
    Seconds += double.IsNaN(_last) || gap > FightProjection.EngagementGapS ? 1 : gap;
    _last = time;
  }
}
