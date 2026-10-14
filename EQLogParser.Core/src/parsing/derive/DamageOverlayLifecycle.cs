using System;
using System.Collections.Generic;

namespace EQLogParser
{
  // Accounting is per life in On Kill mode; the completed result's display grace is not an accounting boundary.
  // The owner serializes Select and Reset (the window uses StatsLock).
  // See docs/DesignNotes.md → Damage overlay: fight accounting and display retention.
  internal sealed class DamageOverlayLifecycle
  {
    internal const double CompletedDisplaySeconds = 8;
    private int _sessionId = -1;
    private double _resetTime = double.NegativeInfinity;
    private double _timedStart = double.NaN;

    internal DamageOverlayScope Select(int sessionId, IReadOnlyList<DerivedFight> rows, double now, int mode,
      OverlayPendingActivity pending = null)
    {
      if (_sessionId != sessionId)
      {
        _sessionId = sessionId;
        _resetTime = double.NegativeInfinity;
        _timedStart = double.NaN;
      }

      return mode == 0 ? SelectOnKill(rows, now, pending) : SelectTimed(rows, now, mode, pending);
    }

    // A click cuts records, not lives. Reopening the window without a click leaves this state alone.
    internal void Reset(int sessionId, double now)
    {
      _sessionId = sessionId;
      _resetTime = now;
      _timedStart = now;
    }

    internal static bool HasRecentActivity(IReadOnlyList<DerivedFight> rows, double now, int mode,
      OverlayPendingActivity pending = null)
    {
      foreach (var row in rows)
      {
        if (!HasDamage(row) || row.BeginTime > now) continue;
        var last = row.LastTime;
        if (mode != 0 && IsRecent(last, now, mode)) return true;
        if (mode == 0 && (IsActive(row, now) || IsCompleted(row) && IsRecent(CompletedAt(row), now, CompletedDisplaySeconds)))
          return true;
      }

      // A reset can leave no materializable rows while real combat waits for projection. Keep polling;
      // this hint alone never supplies totals or resurrects a completed life.
      return pending?.IsRecent(now, mode == 0 ? FightProjection.EngagementGapS : mode) == true;
    }

    // Ordinals, not seconds: two separate arrivals in one log second are still new damage. A death-only derive
    // changes no watermark, so an already displayed kill cannot repeatedly reopen the overlay.
    internal static int LatestActivityOrdinal(IReadOnlyList<DerivedFight> rows)
    {
      var newest = 0;
      foreach (var row in rows)
      {
        if (HasDamage(row)) newest = Math.Max(newest, row.FactEnd);
      }
      return newest;
    }

    private DamageOverlayScope SelectOnKill(IReadOnlyList<DerivedFight> rows, double now, OverlayPendingActivity pending)
    {
      var active = new List<DerivedFight>();
      var completed = new List<DerivedFight>();
      var latestEnd = double.NegativeInfinity;
      foreach (var row in rows)
      {
        if (!HasDamage(row) || row.BeginTime > now || row.LastTime < _resetTime) continue;
        if (IsActive(row, now) || !row.Dead && row.EndReason == DerivedFightEnd.Open
          && pending?.Continues(row, now) == true)
        {
          active.Add(row);
        }
        else if (IsCompleted(row))
        {
          var ended = CompletedAt(row);
          if (!IsRecent(ended, now, CompletedDisplaySeconds) || ended < latestEnd) continue;
          if (ended > latestEnd)
          {
            completed.Clear();
            latestEnd = ended;
          }
          completed.Add(row);
        }
      }

      // Never mix a completed board into a new or surviving fight. When an entire pull arrives in one derive,
      // its last completed lives supply the readable final result even though no live frame was ever painted.
      return new DamageOverlayScope(active.Count > 0 ? active : completed, _resetTime);
    }

    private DamageOverlayScope SelectTimed(IReadOnlyList<DerivedFight> rows, double now, int mode,
      OverlayPendingActivity pending)
    {
      if (double.IsNaN(_timedStart)) _timedStart = Math.Max(_resetTime, now - mode);
      var selected = new List<DerivedFight>();
      var latest = double.NegativeInfinity;
      foreach (var row in rows)
      {
        var last = row.LastTime;
        if (!HasDamage(row) || row.BeginTime > now || last < _timedStart) continue;
        selected.Add(row);
        latest = Math.Max(latest, last);
      }

      // A stale projection cannot erase damage already captured inside the quiet period. Use that damage's
      // log time, not this tick: a stalled derive buys no extra display grace once combat really goes quiet.
      if (selected.Count > 0 && now - latest > mode && pending?.IsRecent(now, mode) != true)
      {
        selected.Clear();
        _timedStart = now;
      }
      return new DamageOverlayScope(selected, _timedStart);
    }

    private static bool HasDamage(DerivedFight row) => row.DamageTotal > 0;

    private static bool IsActive(DerivedFight row, double now) => !row.Dead && row.EndReason == DerivedFightEnd.Open
      && IsRecent(row.LastTime, now, FightProjection.EngagementGapS);

    private static bool IsCompleted(DerivedFight row) => row.EndReason is DerivedFightEnd.Slain or DerivedFightEnd.Charmed;

    private static double CompletedAt(DerivedFight row) => double.IsNaN(row.DeathTime) ? row.LastTime : row.DeathTime;

    private static bool IsRecent(double time, double now, double seconds) => time <= now && now - time <= seconds;
  }

  // A transient selection, never retained by the lifecycle: no snapshot, row, or fact table survives a closed capture here.
  internal sealed class DamageOverlayScope(IReadOnlyList<DerivedFight> rows, double fromTime)
  {
    internal IReadOnlyList<DerivedFight> Rows { get; } = rows;
    internal double FromTime { get; } = fromTime;
  }
}
