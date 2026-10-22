using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace EQLogParser;

internal readonly record struct MeterArrival(int Through, double Time, int FirstOrdinal = 0)
{
  internal static readonly MeterArrival None = new(0, double.NegativeInfinity);
}

// Receipt times decide visibility and quiet-period cuts, never log timestamps or rate denominators.
// Unchecked batches cannot expire. Checked history folds into mode-specific cuts before its metadata is retired.
internal sealed class MeterArrivals
{
  private const double RetentionSeconds = 120;
  private readonly object _gate = new();
  private readonly List<Batch> _batches = [];
  private readonly List<MeterReceipt> _recent = [];
  private int _head;
  private int _recentHead;
  private int _retiredThrough;
  private int _through;
  private int _checkedThrough = -1;
  private long _revision;
  private long _stamp = long.MinValue;
  private MeterDamageFilter _filter;
  private MeterReceiptRuns _runs = new();
  private MeterReceiptRuns _checkpoint = new();
  private Published _published = new(MeterArrival.None);

  // Dispatcher readers must never wait for a worker classifying a newly received prefix.
  internal MeterArrival Observed => Volatile.Read(ref _published).Arrival;

  internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

  internal void Record(int through, double now)
  {
    lock (_gate)
    {
      if (through <= _through) return;

      // Ordinal ranges chain from the previous Through, even when two arrivals share one millisecond.
      var time = Math.Floor(now * 1000) / 1000;
      if (_batches.Count > _head && _batches[^1].Time == time)
        _batches[^1] = _batches[^1] with { Through = through };
      else _batches.Add(new Batch(_through, through, time));
      _through = through;
    }
  }

  internal MeterArrival Latest(DamageFactTable facts, EntityTimeline timeline,
    int mode = 0, CancellationToken cancellation = default)
  {
    lock (_gate)
    {
      if (_checkedThrough == _through && _stamp == timeline.StateStamp() && _filter == MeterDamageFilter.Current)
        return RetainedAnswer(_runs, mode);
    }
    return BeginRead(timeline).Complete(facts, mode, cancellation);
  }

  // Only receipt metadata is copied under the ingest lock. A read owns a fixed prefix even if its last batch grows.
  internal Read BeginRead(EntityTimeline timeline)
  {
    var stamp = timeline.StateStamp();
    var filter = MeterDamageFilter.Current;
    lock (_gate)
    {
      var continues = _stamp == stamp && _filter == filter;
      var from = continues ? Math.Max(0, _checkedThrough) : _retiredThrough;
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
      return new Read(this, timeline, ++_revision, _through, stamp, filter, from,
        continues, continues ? _runs : _checkpoint, batches);
    }
  }

  private MeterArrival Publish(Read read, MeterReceiptRuns runs, List<MeterReceipt> recent,
    int mode, CancellationToken cancellation)
  {
    lock (_gate)
    {
      cancellation.ThrowIfCancellationRequested();
      if (!read.InputsCurrent) return MeterArrival.None;
      var answer = RetainedAnswer(runs, mode);
      if (read.Revision != _revision) return answer;
      _checkedThrough = read.Through;
      _stamp = read.Stamp;
      _filter = read.Filter;
      _runs = runs;
      if (!read.Continues)
      {
        _recent.Clear();
        _recentHead = 0;
      }
      _recent.AddRange(recent);
      RetireCheckedHistory();
      var observed = RetainedAnswer(runs, 0);
      if (_published.Arrival != observed) Volatile.Write(ref _published, new Published(observed));
      return answer;
    }
  }

  private MeterArrival RetainedAnswer(MeterReceiptRuns runs, int mode) =>
    _batches.Count > _head && runs.Last.Time >= _batches[^1].Time - RetentionSeconds
      ? runs.ForMode(mode) : MeterArrival.None;

  private void RetireCheckedHistory()
  {
    if (_head == _batches.Count) return;
    var oldest = _batches[^1].Time - RetentionSeconds;
    while (_head < _batches.Count && _batches[_head].Through <= _checkedThrough && _batches[_head].Time < oldest)
      _retiredThrough = _batches[_head++].Through;

    // A read may still hold the old checkpoint. Published run summaries are immutable to their readers.
    if (_recentHead < _recent.Count && _recent[_recentHead].Through <= _retiredThrough)
    {
      var checkpoint = _checkpoint.Copy();
      while (_recentHead < _recent.Count && _recent[_recentHead].Through <= _retiredThrough)
        checkpoint.Add(_recent[_recentHead++]);
      _checkpoint = checkpoint;
    }
    Compact(_batches, ref _head);
    Compact(_recent, ref _recentHead);
  }

  private static void Compact<T>(List<T> list, ref int head)
  {
    if (head <= 1024 || head * 2 < list.Count) return;
    list.RemoveRange(0, head);
    head = 0;
    // Release a long-stall backlog's capacity too, not just its logical entries.
    if (list.Count < list.Capacity / 4) list.TrimExcess();
  }

  // A paused/cancelled read owns no ingest lock and acknowledges nothing until its complete answer is published.
  internal readonly record struct Read(MeterArrivals Owner, EntityTimeline Timeline, long Revision, int Through,
    long Stamp, MeterDamageFilter Filter, int From, bool Continues, MeterReceiptRuns Seed, Batch[] Batches)
  {
    // An invalidated scan is not a verified absence of receipts and must not retire a timed accumulation.
    internal bool InputsCurrent => Stamp == Timeline.StateStamp() && Filter == MeterDamageFilter.Current;

    internal MeterArrival Complete(DamageFactTable facts, int mode = 0, CancellationToken cancellation = default)
    {
      cancellation.ThrowIfCancellationRequested();
      var runs = Seed.Copy();
      var recent = new List<MeterReceipt>();
      foreach (var batch in Batches)
      {
        cancellation.ThrowIfCancellationRequested();
        var first = Math.Max(batch.From, From);
        while (first < batch.Through && !Accepts(facts, first, cancellation)) first++;
        if (first == batch.Through) continue;
        var last = batch.Through - 1;
        while (last > first && !Accepts(facts, last, cancellation)) last--;
        var receipt = new MeterReceipt(first, last + 1, batch.Time);
        runs.Add(receipt);
        recent.Add(receipt);
      }
      return Owner.Publish(this, runs, recent, mode, cancellation);
    }

    private bool Accepts(DamageFactTable facts, int ordinal, CancellationToken cancellation)
    {
      if ((ordinal & 2047) == 0) cancellation.ThrowIfCancellationRequested();
      var fact = facts.At(ordinal);
      return MeterActivityIndex.AcceptsPending(fact, Timeline, facts.NameOf(fact.AtkIdx),
        facts.NameOf(fact.DefIdx), Filter);
    }
  }

  internal readonly record struct Batch(int From, int Through, double Time);

  private sealed record Published(MeterArrival Arrival);
}
