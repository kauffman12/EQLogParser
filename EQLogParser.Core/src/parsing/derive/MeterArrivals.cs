using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace EQLogParser;

internal readonly record struct MeterArrival(int Through, double Time)
{
  internal static readonly MeterArrival None = new(0, double.NegativeInfinity);
}

// Visibility-only receipt times, never log timestamps. Retain batches longer than every supported reset mode.
// docs/DesignNotes.md → "Damage overlay: fight accounting and display retention".
internal sealed class MeterArrivals
{
  private const double RetentionSeconds = 120;
  private readonly object _gate = new();
  private readonly List<Batch> _batches = [];
  private int _head;
  private int _through;
  private int _checkedThrough = -1;
  private long _revision;
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

  internal MeterArrival Latest(DamageFactTable facts, EntityTimeline timeline,
    CancellationToken cancellation = default)
  {
    /*
     * Verified prefix still current: answer from the memo, which may legitimately be None (nothing eligible arrived). Polling must
     * not restamp a receipt or walk facts again. Pruning only happens inside Record, which moves _through, so an equal
     * _checkedThrough already means the stored answer was checked against the retention window when it was published.
     */
    lock (_gate)
    {
      if (_checkedThrough == _through && _stamp == timeline.StateStamp() && _filter == MeterDamageFilter.Current)
        return _answer;
    }
    return BeginRead(timeline).Complete(facts, cancellation);
  }

  // Only receipt metadata is copied under the ingest lock. A read owns a fixed prefix even if its last batch grows.
  internal Read BeginRead(EntityTimeline timeline)
  {
    var stamp = timeline.StateStamp();
    var filter = MeterDamageFilter.Current;
    lock (_gate)
    {
      var from = _stamp == stamp && _filter == filter ? Math.Max(0, _checkedThrough) : 0;
      var answer = from == 0 || !IsRetained(_answer) ? MeterArrival.None : _answer;
      var lo = _head;
      var hi = _batches.Count;
      while (lo < hi)
      {
        var mid = lo + ((hi - lo) / 2);
        if (_batches[mid].Through <= from) lo = mid + 1;
        else hi = mid;
      }
      var batches = new Batch[_batches.Count - lo];
      _batches.CopyTo(lo, batches, 0, batches.Length);
      return new Read(this, timeline, ++_revision, _through, stamp, filter, from, answer, batches);
    }
  }

  private MeterArrival Publish(Read read, MeterArrival answer, CancellationToken cancellation)
  {
    lock (_gate)
    {
      cancellation.ThrowIfCancellationRequested();
      if (!read.InputsCurrent) return MeterArrival.None;
      if (!IsRetained(answer)) answer = MeterArrival.None;
      if (read.Revision != _revision) return answer;
      _checkedThrough = read.Through;
      _stamp = read.Stamp;
      _filter = read.Filter;
      _answer = answer;
      if (_published.Arrival != answer) Volatile.Write(ref _published, new Published(answer));
      return answer;
    }
  }

  private bool IsRetained(MeterArrival answer) =>
    _head < _batches.Count && answer.Through > _batches[_head].From;

  // A paused/cancelled read owns no ingest lock and acknowledges nothing until its complete answer is published.
  internal readonly record struct Read(MeterArrivals Owner, EntityTimeline Timeline, long Revision, int Through,
    long Stamp, MeterDamageFilter Filter, int From, MeterArrival Fallback, Batch[] Batches)
  {
    // An invalidated scan is not a verified absence of receipts and must not retire a timed accumulation.
    internal bool InputsCurrent => Stamp == Timeline.StateStamp() && Filter == MeterDamageFilter.Current;

    internal MeterArrival Complete(DamageFactTable facts, CancellationToken cancellation = default)
    {
      cancellation.ThrowIfCancellationRequested();
      var answer = Fallback;
      for (var i = Batches.Length - 1; i >= 0; i--)
      {
        var batch = Batches[i];
        for (var ordinal = batch.Through - 1; ordinal >= Math.Max(batch.From, From); ordinal--)
        {
          if ((ordinal & 2047) == 0) cancellation.ThrowIfCancellationRequested();
          var fact = facts.At(ordinal);
          if (!MeterActivityIndex.AcceptsPending(fact, Timeline, facts.NameOf(fact.AtkIdx),
              facts.NameOf(fact.DefIdx), Filter)) continue;
          answer = new MeterArrival(ordinal + 1, batch.Time);
          return Owner.Publish(this, answer, cancellation);
        }
      }
      return Owner.Publish(this, answer, cancellation);
    }
  }

  internal readonly record struct Batch(int From, int Through, double Time);

  private sealed record Published(MeterArrival Arrival);
}
