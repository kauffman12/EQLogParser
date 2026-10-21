using System;
using System.Collections.Generic;

namespace EQLogParser;

// Data selects membership; receipt time controls visibility. Reset/expiry cut at a captured ordinal, never a PC timestamp.
// docs/DesignNotes.md → "Damage overlay: fight accounting and display retention".
internal sealed class DamageOverlayLifecycle
{
  internal const double CompletedDisplaySeconds = 8;
  private int _sessionId = -1;
  private int _firstOrdinal;
  private double _timedStart = double.NaN;

  internal DamageOverlayLifecycle Copy() => new()
  {
    _sessionId = _sessionId,
    _firstOrdinal = _firstOrdinal,
    _timedStart = _timedStart
  };

  internal DamageOverlayScope Select(int sessionId, IReadOnlyList<DerivedFight> rows, double now, int mode,
    MeterArrival arrival, int capturedCount, OverlayPendingActivity pending = null,
    Func<DerivedFight, MeterLifeActivity> activity = null)
  {
    EnsureSession(sessionId);
    var selected = new List<DerivedFight>();
    var edge = CaptureEdge(rows, activity);
    if (mode != 0)
    {
      if (double.IsNaN(_timedStart) && double.IsFinite(edge)) _timedStart = edge - mode;
      foreach (var row in rows)
        if (Eligible(row, activity) && Activity(row, activity).Last >= _timedStart) selected.Add(row);
    }
    else
    {
      // Pending data may move a data boundary, but its age never changes membership.
      edge = Math.Max(edge, pending?.Latest ?? double.NegativeInfinity);
      foreach (var row in rows)
      {
        if (!Eligible(row, activity) || row.Dead || row.EndReason != DerivedFightEnd.Open) continue;
        var last = Activity(row, activity).Last;
        last = Math.Max(last, pending?.ContinuedThrough(row.Name, last) ?? double.NegativeInfinity);
        if (edge - last <= FightProjection.EngagementGapS) selected.Add(row);
      }
      if (selected.Count == 0)
      {
        var latestEnd = double.NegativeInfinity;
        foreach (var row in rows)
        {
          if (!Eligible(row, activity) || !IsCompleted(row)) continue;
          var end = CompletedAt(row);
          if (edge - end > CompletedDisplaySeconds || end < latestEnd || pending?.Latest > end) continue;
          if (end > latestEnd) { selected.Clear(); latestEnd = end; }
          selected.Add(row);
        }
      }
    }

    var completedOnly = mode == 0 && selected.Count > 0 && IsCompleted(selected[0]);
    var timeout = mode != 0 ? mode : completedOnly ? CompletedDisplaySeconds : FightProjection.EngagementGapS;
    var deadline = arrival.Time + timeout;
    if (arrival.Through <= _firstOrdinal || now > deadline)
    {
      selected.Clear();
      if (mode != 0 && arrival.Through > _firstOrdinal && now > deadline)
      {
        // A closed timed board starts its next accumulation after the captured prefix, including unprojected damage.
        _firstOrdinal = Math.Max(_firstOrdinal, capturedCount);
        _timedStart = double.NaN;
      }
    }
    return new DamageOverlayScope(selected, FromTime(mode), deadline, _firstOrdinal);
  }

  // Inspect all post-reset lives before exclusions choose a data edge. An excluded newer life must not hide an older accepted one.
  internal DamageOverlayScope SelectCandidates(int sessionId, IReadOnlyList<DerivedFight> rows, int mode)
  {
    EnsureSession(sessionId);
    var selected = new List<DerivedFight>();
    foreach (var row in rows)
      if (row.FactEnd > _firstOrdinal) selected.Add(row);
    return new DamageOverlayScope(selected, FromTime(mode), firstOrdinal: _firstOrdinal);
  }

  internal void Reset(int sessionId, int capturedCount)
  {
    _sessionId = sessionId;
    _firstOrdinal = capturedCount;
    _timedStart = double.NaN;
  }

  internal static bool HasRecentActivity(MeterArrival arrival, double now, int mode) =>
    arrival.Through > 0 && now <= arrival.Time + (mode == 0 ? FightProjection.EngagementGapS : mode);

  private void EnsureSession(int sessionId)
  {
    if (_sessionId != sessionId) Reset(sessionId, 0);
  }

  private double FromTime(int mode) => mode == 0 || double.IsNaN(_timedStart)
    ? double.NegativeInfinity : _timedStart;

  private bool Eligible(DerivedFight row, Func<DerivedFight, MeterLifeActivity> activity) =>
    row.FactEnd > _firstOrdinal && Activity(row, activity).HasDamage;

  private double CaptureEdge(IReadOnlyList<DerivedFight> rows, Func<DerivedFight, MeterLifeActivity> activity)
  {
    var edge = double.NegativeInfinity;
    foreach (var row in rows)
    {
      if (!Eligible(row, activity)) continue;
      var last = Activity(row, activity).Last;
      edge = Math.Max(edge, IsCompleted(row) ? Math.Max(last, CompletedAt(row)) : last);
    }
    return edge;
  }

  private static MeterLifeActivity Activity(DerivedFight row, Func<DerivedFight, MeterLifeActivity> activity) =>
    activity is null ? new MeterLifeActivity(row.DamageTotal > 0, row.LastTime) : activity(row);

  private static bool IsCompleted(DerivedFight row) => row.EndReason is DerivedFightEnd.Slain or DerivedFightEnd.Charmed;
  private static double CompletedAt(DerivedFight row) => double.IsNaN(row.DeathTime) ? row.LastTime : row.DeathTime;
}

internal sealed class DamageOverlayScope(IReadOnlyList<DerivedFight> rows, double fromTime,
  double expiresAt = double.NegativeInfinity, int firstOrdinal = 0)
{
  internal IReadOnlyList<DerivedFight> Rows { get; } = rows;
  internal double FromTime { get; } = fromTime;
  internal double ExpiresAt { get; } = expiresAt;
  internal int FirstOrdinal { get; } = firstOrdinal;
}
