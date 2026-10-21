using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace EQLogParser;

internal readonly record struct MeterArrival(int Through, double Time)
{
  internal static readonly MeterArrival None = new(0, double.NegativeInfinity);
}

// Visibility-only receipt times, never log timestamps. Millisecond batches avoid a timestamp on every fact.
// Keep longer than the longest supported reset mode (90 s); expired receipts cannot open a board.
internal sealed class MeterArrivals
{
  private const double RetentionSeconds = 120;
  private readonly object _gate = new();
  private readonly List<Batch> _batches = [];
  private int _head;
  private int _through;
  private int _checkedThrough = -1;
  private long _stamp = long.MinValue;
  private MeterDamageFilter _filter;
  private MeterArrival _answer = MeterArrival.None;
  private Published _published = new(MeterArrival.None);

  // Dispatcher readers must never wait for a worker classifying a newly received prefix.
  internal MeterArrival Observed => Volatile.Read(ref _published).Arrival;

  internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

  internal void Record(int through, double now)
  {
    lock (_gate)
    {
      if (through <= _through) return;
      var time = Math.Floor(now * 1000) / 1000;
      if (_batches.Count > _head && _batches[^1].Time == time)
        _batches[^1] = _batches[^1] with { Through = through };
      else _batches.Add(new Batch(_through, through, time));
      _through = through;
      while (_head < _batches.Count && now - _batches[_head].Time > RetentionSeconds) _head++;
      if (_head > 1024 && _head * 2 >= _batches.Count)
      {
        _batches.RemoveRange(0, _head);
        _head = 0;
      }
    }
  }

  internal MeterArrival Latest(DamageFactTable facts, EntityTimeline timeline)
  {
    lock (_gate)
    {
      var stamp = timeline.StateStamp();
      var filter = MeterDamageFilter.Current;
      if (_checkedThrough == _through && _stamp == stamp && _filter == filter) return _answer;
      var from = _stamp == stamp && _filter == filter ? Math.Max(0, _checkedThrough) : 0;
      if (from == 0 || _head < _batches.Count && _answer.Through <= _batches[_head].From)
        _answer = MeterArrival.None;
      _checkedThrough = _through;
      _stamp = stamp;
      _filter = filter;
      for (var i = _batches.Count - 1; i >= _head; i--)
      {
        var batch = _batches[i];
        if (batch.Through <= from) break;
        for (var ordinal = batch.Through - 1; ordinal >= Math.Max(batch.From, from); ordinal--)
        {
          var fact = facts.At(ordinal);
          if (!MeterActivityIndex.AcceptsPending(fact, timeline, facts.NameOf(fact.AtkIdx),
              facts.NameOf(fact.DefIdx), filter)) continue;
          _answer = new MeterArrival(ordinal + 1, batch.Time);
          Volatile.Write(ref _published, new Published(_answer));
          return _answer;
        }
      }
      Volatile.Write(ref _published, new Published(_answer));
      return _answer;
    }
  }

  private sealed record Published(MeterArrival Arrival);

  private readonly record struct Batch(int From, int Through, double Time);
}
