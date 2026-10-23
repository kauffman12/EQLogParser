using System;
using System.Collections.Generic;
using System.Threading;

namespace EQLogParser
{
  // One snapshot's unprojected suffix. No facts, rows or timeline are retained. Update runs on the meter worker;
  // it answers data continuity only, never receipt freshness. A new snapshot starts a new cursor and retires these hints.
  internal sealed class OverlayPendingActivity(int projectedCount)
  {
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Span>> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _projectedCount = projectedCount;
    private int _through = projectedCount;
    private long _stamp = long.MinValue;
    private double _latest = double.NegativeInfinity;
    private int _latestSequence = -1;
    private MeterDamageFilter _filter;
    private bool _hasFilter;

    internal double Latest => Volatile.Read(ref _latest);
    internal int LatestSequence => Volatile.Read(ref _latestSequence);

    internal void Update(DamageFactTable facts, EntityTimeline timeline, CancellationToken cancellation = default)
    {
      lock (_gate)
      {
        var stamp = timeline.StateStamp();
        var filter = MeterDamageFilter.Current;
        if (_stamp != stamp || !_hasFilter || _filter != filter)
        {
          _stamp = stamp;
          _filter = filter;
          _hasFilter = true;
          _through = _projectedCount;
          Volatile.Write(ref _latest, double.NegativeInfinity);
          Volatile.Write(ref _latestSequence, -1);
          _names.Clear();
        }

        // FactCount publishes each complete append, including its interned names. Do not chase a moving tail.
        var end = facts.FactCount;
        for (; _through < end; _through++)
        {
          if ((_through & 2047) == 0) cancellation.ThrowIfCancellationRequested();
          var fact = facts.At(_through);
          var attacker = facts.NameOf(fact.AtkIdx);
          var defender = facts.NameOf(fact.DefIdx);
          // Both directions continue a life: this answers data continuity for rows already displayed, not which panel may
          // open or keep the window up (that question is MeterArrivals.Observed(panels)).
          if (MeterActivityIndex.AcceptsPending(fact, timeline, attacker, defender, filter) == MeterPanels.None) continue;
          Add(attacker, fact.TimeS);
          Add(defender, fact.TimeS);
          if (fact.TimeS > _latest) Volatile.Write(ref _latest, fact.TimeS);
          if (fact.Seq > _latestSequence) Volatile.Write(ref _latestSequence, fact.Seq);
        }
      }
    }

    internal bool Covers(int through, EntityTimeline timeline)
    {
      lock (_gate) return _through >= through && _stamp == timeline.StateStamp()
        && _hasFilter && _filter == MeterDamageFilter.Current;
    }

    internal double ContinuedThrough(string name, double last)
    {
      lock (_gate)
      {
        if (!_names.TryGetValue(name, out var spans)) return double.NegativeInfinity;
        foreach (var span in spans)
        {
          // Only a chain connected to THIS life may keep it visible. A post-gap same-name arrival is a new life.
          if (span.Begin > last + FightProjection.EngagementGapS) break;
          if (span.End >= last)
            return span.End;
        }
        return double.NegativeInfinity;
      }
    }

    private void Add(string name, double time)
    {
      if (!_names.TryGetValue(name, out var spans)) _names[name] = spans = [];
      // Sorted disjoint activity chains; repeated seconds cost no new span, and out-of-order facts can bridge chains.
      var lo = 0;
      var hi = spans.Count;
      while (lo < hi)
      {
        var mid = lo + ((hi - lo) / 2);
        if (spans[mid].End + FightProjection.EngagementGapS < time) lo = mid + 1;
        else hi = mid;
      }
      if (lo == spans.Count || spans[lo].Begin > time + FightProjection.EngagementGapS)
      {
        spans.Insert(lo, new Span(time, time));
        return;
      }
      var target = spans[lo];
      target.Begin = Math.Min(target.Begin, time);
      target.End = Math.Max(target.End, time);
      var last = lo;
      while (last + 1 < spans.Count && spans[last + 1].Begin <= target.End + FightProjection.EngagementGapS)
      {
        target.End = Math.Max(target.End, spans[++last].End);
      }
      if (last > lo) spans.RemoveRange(lo + 1, last - lo);
    }

    private sealed class Span(double begin, double end)
    {
      internal double Begin = begin;
      internal double End = end;
    }
  }
}
