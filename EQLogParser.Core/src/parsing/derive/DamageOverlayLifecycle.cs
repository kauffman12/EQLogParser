using System;
using System.Collections.Generic;

namespace EQLogParser
{
  // Accounting is per life in On Kill mode; the completed result's display grace is not an accounting boundary.
  // See docs/DesignNotes.md → Damage overlay: fight accounting and display retention.
  internal sealed class DamageOverlayLifecycle
  {
    internal const double CompletedDisplaySeconds = 8;
    private int _sessionId = -1;
    private double _resetTime = double.NegativeInfinity;
    private double _timedStart = double.NaN;

    internal DamageOverlayLifecycle Copy() => new()
    {
      _sessionId = _sessionId,
      _resetTime = _resetTime,
      _timedStart = _timedStart
    };

    internal DamageOverlayScope Select(int sessionId, IReadOnlyList<DerivedFight> rows, double now, int mode,
      OverlayPendingActivity pending = null, Func<DerivedFight, MeterLifeActivity> activity = null)
    {
      EnsureSession(sessionId);
      return mode == 0 ? SelectOnKill(rows, now, pending, activity) : SelectTimed(rows, now, mode, pending, activity);
    }

    // Activity must inspect every eligible completion before excluded active/newer lives can be discarded.
    internal DamageOverlayScope SelectCandidates(int sessionId, IReadOnlyList<DerivedFight> rows, double now, int mode,
      OverlayPendingActivity pending = null)
    {
      var state = Copy();
      state.EnsureSession(sessionId);
      return mode == 0 ? state.SelectOnKill(rows, now, pending, null, true)
        : state.SelectTimed(rows, now, mode, pending, null);
    }

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
        if (row.DamageTotal <= 0 || row.BeginTime > now) continue;
        if (mode != 0 && IsRecent(row.LastTime, now, mode)) return true;
        if (mode == 0 && (IsActive(row, row.LastTime, now)
          || IsCompleted(row) && IsRecent(CompletedAt(row), now, CompletedDisplaySeconds))) return true;
      }
      return pending?.IsRecent(now, mode == 0 ? FightProjection.EngagementGapS : mode) == true;
    }

    internal static int LatestActivityOrdinal(IReadOnlyList<DerivedFight> rows, double now)
    {
      var newest = 0;
      foreach (var row in rows)
      {
        if (row.DamageTotal <= 0 || row.BeginTime > now) continue;
        newest = Math.Max(newest, row.FactEnd);
      }
      return newest;
    }

    private void EnsureSession(int sessionId)
    {
      if (_sessionId == sessionId) return;
      _sessionId = sessionId;
      _resetTime = double.NegativeInfinity;
      _timedStart = double.NaN;
    }

    private DamageOverlayScope SelectOnKill(IReadOnlyList<DerivedFight> rows, double now, OverlayPendingActivity pending,
      Func<DerivedFight, MeterLifeActivity> activity, bool candidates = false)
    {
      var active = new List<DerivedFight>();
      var completed = new List<DerivedFight>();
      var latestEnd = double.NegativeInfinity;
      var expires = double.NegativeInfinity;
      foreach (var row in rows)
      {
        var current = Activity(row, activity);
        if (!current.HasDamage || row.BeginTime > now || current.Last < _resetTime) continue;
        var pendingLast = pending?.ContinuedThrough(row.Name, current.Last, now) ?? double.NegativeInfinity;
        if (IsActive(row, current.Last, now)
          || !row.Dead && row.EndReason == DerivedFightEnd.Open && double.IsFinite(pendingLast))
        {
          active.Add(row);
          expires = Math.Max(expires, Math.Max(current.Last, pendingLast) + FightProjection.EngagementGapS);
        }
        else if (IsCompleted(row))
        {
          var ended = CompletedAt(row);
          if (!IsRecent(ended, now, CompletedDisplaySeconds) || !candidates && ended < latestEnd) continue;
          if (ended > latestEnd)
          {
            if (!candidates) completed.Clear();
            latestEnd = ended;
          }
          completed.Add(row);
        }
      }
      if (candidates)
      {
        active.AddRange(completed);
        return new DamageOverlayScope(active, _resetTime);
      }
      return active.Count > 0 ? new DamageOverlayScope(active, _resetTime, expires)
        : new DamageOverlayScope(completed, _resetTime, latestEnd + CompletedDisplaySeconds);
    }

    private DamageOverlayScope SelectTimed(IReadOnlyList<DerivedFight> rows, double now, int mode,
      OverlayPendingActivity pending, Func<DerivedFight, MeterLifeActivity> activity)
    {
      if (double.IsNaN(_timedStart)) _timedStart = Math.Max(_resetTime, now - mode);
      var selected = new List<DerivedFight>();
      var latest = double.NegativeInfinity;
      foreach (var row in rows)
      {
        var current = Activity(row, activity);
        if (!current.HasDamage || row.BeginTime > now || current.Last < _timedStart) continue;
        selected.Add(row);
        latest = Math.Max(latest, current.Last);
      }
      if (pending?.IsRecent(now, mode) == true) latest = Math.Max(latest, pending.Latest);
      if (now - latest > mode)
      {
        selected.Clear();
        _timedStart = now;
      }
      return new DamageOverlayScope(selected, _timedStart,
        selected.Count > 0 ? latest + mode : double.NegativeInfinity);
    }

    private static MeterLifeActivity Activity(DerivedFight row, Func<DerivedFight, MeterLifeActivity> activity) =>
      activity is null ? new MeterLifeActivity(row.DamageTotal > 0, row.LastTime) : activity(row);

    private static bool IsActive(DerivedFight row, double last, double now) =>
      !row.Dead && row.EndReason == DerivedFightEnd.Open && IsRecent(last, now, FightProjection.EngagementGapS);
    private static bool IsCompleted(DerivedFight row) => row.EndReason is DerivedFightEnd.Slain or DerivedFightEnd.Charmed;
    private static double CompletedAt(DerivedFight row) => double.IsNaN(row.DeathTime) ? row.LastTime : row.DeathTime;
    private static bool IsRecent(double time, double now, double seconds) => time <= now && now - time <= seconds;
  }

  // A transient selection and its wall-clock expiry. The dispatcher can hide expired results while a build is blocked.
  internal sealed class DamageOverlayScope(IReadOnlyList<DerivedFight> rows, double fromTime,
    double expiresAt = double.NegativeInfinity)
  {
    internal IReadOnlyList<DerivedFight> Rows { get; } = rows;
    internal double FromTime { get; } = fromTime;
    internal double ExpiresAt { get; } = expiresAt;
  }
}
