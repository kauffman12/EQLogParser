using System;
using System.Collections.Generic;
using System.Threading;

namespace EQLogParser
{
  // One snapshot's unprojected suffix. No facts, rows or timeline are retained. Update runs on the meter worker;
  // opening/closing checks only read the cached hint. A new snapshot starts a new cursor and retires these hints.
  internal sealed class OverlayPendingActivity(int projectedCount)
  {
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Span>> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _projectedCount = projectedCount;
    private int _through = projectedCount;
    private long _stamp = long.MinValue;
    private double _latest = double.NegativeInfinity;

    internal void Update(DamageFactTable facts, EntityTimeline timeline)
    {
      lock (_gate)
      {
        var stamp = timeline.StateStamp();
        if (_stamp != stamp)
        {
          _stamp = stamp;
          _through = _projectedCount;
          Volatile.Write(ref _latest, double.NegativeInfinity);
          _names.Clear();
        }

        // FactCount publishes each complete append, including its interned names. Do not chase a moving tail.
        var end = facts.FactCount;
        for (; _through < end; _through++)
        {
          var fact = facts.At(_through);
          var attacker = facts.NameOf(fact.AtkIdx);
          var defender = facts.NameOf(fact.DefIdx);
          if (!FightProjection.TryCombatSides(fact, timeline, attacker, defender, out _, out _)) continue;
          Add(attacker, fact.TimeS);
          Add(defender, fact.TimeS);
          if (fact.TimeS > _latest) Volatile.Write(ref _latest, fact.TimeS);
        }
      }
    }

    internal bool IsRecent(double now, double seconds)
    {
      // The UI must not wait for a worker scanning a bulk suffix.
      var latest = Volatile.Read(ref _latest);
      return latest <= now && now - latest <= seconds;
    }

    internal bool Continues(DerivedFight row, double now)
    {
      lock (_gate)
      {
        if (!_names.TryGetValue(row.Name, out var spans)) return false;
        foreach (var span in spans)
        {
          // Only a chain connected to THIS life may keep it visible. A post-gap same-name arrival is a new life.
          if (span.Begin > row.LastTime + FightProjection.EngagementGapS) break;
          if (span.End >= row.LastTime && span.End <= now && now - span.End <= FightProjection.EngagementGapS)
            return true;
        }
        return false;
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
