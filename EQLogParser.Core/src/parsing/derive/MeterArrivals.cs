using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace EQLogParser;

internal readonly record struct MeterArrival(int Through, double Time, int FirstOrdinal = 0)
{
  internal static readonly MeterArrival None = new(0, double.NegativeInfinity);
}

// Source age qualifies live activity at ingest; receipt times then decide visibility and quiet-period cuts.
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
  private Published _published = new(new MeterReceiptRuns(), false);

  /*
   * Dispatcher readers must never wait for receipt verification. Published runs are immutable, already retention-gated,
   * and answer every mode/panel from one volatile reference.
   */
  internal MeterArrival Observed(MeterPanels panels, int mode = 0)
  {
    var published = Volatile.Read(ref _published);
    return published.Retained ? published.Runs.ForMode(mode, panels) : MeterArrival.None;
  }

  internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

  // Production receipts carry both clocks. Old/future data stays captured, but cannot become live meter activity.
  internal void Record(int through, double now, double sourceTime, double wallNow)
  {
    var age = wallNow - sourceTime;
    var minimumMode = double.IsFinite(age) && age >= 0 && age <= MeterReceiptRuns.MaxMode
      ? (int)Math.Ceiling(age) : MeterReceiptRuns.MaxMode + 1;
    Record(through, now, minimumMode);
  }

  // Prequalified receipts let accounting tests exercise the monotonic clock independently of source freshness.
  internal void Record(int through, double now) => Record(through, now, 0);

  // `panels` answers for one window; the cached run beneath it holds both, so a different choice needs no rescan.
  internal MeterArrival Latest(DamageFactTable facts, EntityTimeline timeline,
    int mode = 0, MeterPanels panels = MeterPanels.Both, CancellationToken cancellation = default)
  {
    lock (_gate)
    {
      if (_checkedThrough == _through && _stamp == timeline.StateStamp() && _filter == MeterDamageFilter.Current)
        return RetainedAnswer(_runs, mode, panels);
    }
    return BeginRead(timeline).Complete(facts, mode, panels, cancellation);
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

  private void Record(int through, double now, int minimumMode)
  {
    lock (_gate)
    {
      if (through <= _through) return;
      var from = _through;
      _through = through;
      if (minimumMode > MeterReceiptRuns.MaxMode) return;

      // Only adjoining ranges with the same source qualification may share a millisecond batch.
      var time = Math.Floor(now * 1000) / 1000;
      if (_batches.Count > _head && _batches[^1].Time == time
        && _batches[^1].Through == from && _batches[^1].MinimumMode == minimumMode)
        _batches[^1] = _batches[^1] with { Through = through };
      else _batches.Add(new Batch(from, through, time, minimumMode));
    }
  }

  private MeterArrival Publish(Read read, MeterReceiptRuns runs, List<MeterReceipt> recent,
    int mode, MeterPanels panels, CancellationToken cancellation)
  {
    lock (_gate)
    {
      cancellation.ThrowIfCancellationRequested();
      if (!read.InputsCurrent) return MeterArrival.None;
      var answer = RetainedAnswer(runs, mode, panels);
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
      // The run is immutable after publication; dispatcher readers can ask any mode/panel without a lock or scan.
      Volatile.Write(ref _published, new Published(runs, IsRetained(runs)));
      return answer;
    }
  }

  // Retention asks whether this run still speaks for the newest batch at all; which panel answered is a separate question.
  private MeterArrival RetainedAnswer(MeterReceiptRuns runs, int mode, MeterPanels panels) =>
    IsRetained(runs) ? runs.ForMode(mode, panels) : MeterArrival.None;

  private bool IsRetained(MeterReceiptRuns runs) =>
    _batches.Count > _head && runs.Last.Time >= _batches[^1].Time - RetentionSeconds;

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

    internal MeterArrival Complete(DamageFactTable facts, int mode = 0, MeterPanels panels = MeterPanels.Both,
      CancellationToken cancellation = default)
    {
      cancellation.ThrowIfCancellationRequested();
      var runs = Seed.Copy();
      var recent = new List<MeterReceipt>();
      foreach (var batch in Batches)
      {
        cancellation.ThrowIfCancellationRequested();
        var first = Math.Max(batch.From, From);
        var receipt = new MeterReceipt(
          LaneReceipt(facts, first, batch, MeterPanels.Damage, cancellation),
          LaneReceipt(facts, first, batch, MeterPanels.Tanking, cancellation), batch.MinimumMode);
        if (receipt.Through == 0) continue;
        runs.Add(receipt);
        recent.Add(receipt);
      }
      return Owner.Publish(this, runs, recent, mode, panels, cancellation);
    }

    // Only the endpoints matter; a mixed batch's other lane must not move this lane's reset or arrival ordinal.
    private MeterArrival LaneReceipt(DamageFactTable facts, int first, Batch batch, MeterPanels panel,
      CancellationToken cancellation)
    {
      while (first < batch.Through && (Accepts(facts, first, cancellation) & panel) == 0) first++;
      if (first == batch.Through) return MeterArrival.None;
      var last = batch.Through - 1;
      while (last > first && (Accepts(facts, last, cancellation) & panel) == 0) last--;
      return new MeterArrival(last + 1, batch.Time, first);
    }

    private MeterPanels Accepts(DamageFactTable facts, int ordinal, CancellationToken cancellation)
    {
      if ((ordinal & 2047) == 0) cancellation.ThrowIfCancellationRequested();
      var fact = facts.At(ordinal);
      return MeterActivityIndex.AcceptsPending(fact, Timeline, facts.NameOf(fact.AtkIdx),
        facts.NameOf(fact.DefIdx), Filter);
    }
  }

  internal readonly record struct Batch(int From, int Through, double Time, int MinimumMode = 0);

  private sealed record Published(MeterReceiptRuns Runs, bool Retained);
}
