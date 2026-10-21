using System;
using System.Collections.Generic;

namespace EQLogParser;

/// <summary>Folds time-ordered records into sparse chart buckets without retaining the input records.</summary>
internal sealed class ChartSeriesBuilder(string name, string playerName = null)
{
  private readonly DataPoint _current = new() { Name = name, PlayerName = playerName };
  private readonly ChartActivityClock _clock = new();
  private bool _pending;

  internal List<DataPoint> Points { get; } = [];

  internal void Add(DataPoint record, bool isCrit, bool isTwincast, bool isHit)
  {
    if (_clock.Seconds > 0 && record.CurrentTime != _current.CurrentTime)
    {
      Finish();
      if (record.CurrentTime - _current.CurrentTime > FightProjection.EngagementGapS)
      {
        // Gap markers zero the bucket, never the cumulative amounts or their denominators.
        Points.Add(Snapshot(_current.CurrentTime + ChartAggregation.RollingSeconds, true));
        Points.Add(Snapshot(record.CurrentTime - ChartAggregation.RollingSeconds, true));
      }
      _current.TotalPerSecond = 0;
      _current.AttemptsPerSecond = 0;
      _current.HitsPerSecond = 0;
      _current.CritsPerSecond = 0;
      _current.TcPerSecond = 0;
    }

    _clock.Add(record.CurrentTime);
    _current.CurrentTime = record.CurrentTime;
    _current.Total += record.Total;
    _current.FightTotal += record.Total;
    _current.TotalPerSecond += record.Total;
    _current.AttemptsPerSecond++;
    if (isHit)
    {
      _current.HitsPerSecond++;
      _current.FightHits++;
      if (isCrit)
      {
        _current.CritsPerSecond++;
        _current.FightCritHits++;
      }
      if (isTwincast)
      {
        _current.TcPerSecond++;
        _current.FightTcHits++;
      }
    }
    _pending = true;
  }

  internal void Finish()
  {
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

// Sorted buckets need only their previous timestamp, not a growing TimeRange scanned at every publication.
internal sealed class ChartActivityClock
{
  private double _last = double.NaN;
  internal double Seconds { get; private set; }

  internal void Add(double time)
  {
    if (!double.IsNaN(_last) && time < _last)
      throw new InvalidOperationException("Chart records must be time-ordered");
    var gap = time - _last;
    Seconds += double.IsNaN(_last) || gap > FightProjection.EngagementGapS ? 1 : gap;
    _last = time;
  }
}
