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
  // Scalar log-time high-water mark survives calculator/window replacement, never a PC timestamp.
  private long _windowSecond = MeterRecentWindow.NoSecond;
  // Accounting keeps this panel choice's receipt high-water mark; filtering can hide, not expire it early.
  private MeterArrival _retirementArrival = MeterArrival.None;
  private MeterPanels _panels = MeterPanels.Both;

  internal DamageOverlayLifecycle Copy() => new()
  {
    _sessionId = _sessionId,
    _firstOrdinal = _firstOrdinal,
    _timedStart = _timedStart,
    _windowSecond = _windowSecond,
    _retirementArrival = _retirementArrival,
    _panels = _panels
  };

  // False means an accounting bound changed: refresh activity from SelectCandidates before selecting again.
  internal bool TrySelect(int sessionId, IReadOnlyList<DerivedFight> rows, double now, int mode,
    MeterArrival arrival, int capturedCount, out DamageOverlayScope scope, OverlayPendingActivity pending = null,
    Func<DerivedFight, MeterLifeActivity> activity = null, MeterPanels panels = MeterPanels.Both)
  {
    scope = null;
    EnsureSession(sessionId);
    if (_panels != panels)
    {
      // Widening retains every prior lane; only removing a lane can invalidate its retirement clock.
      if ((panels & _panels) != _panels) _retirementArrival = MeterArrival.None;
      _panels = panels;
    }
    if (arrival.Through > _firstOrdinal && arrival.Time >= _retirementArrival.Time) _retirementArrival = arrival;
    if (mode != 0 && arrival.FirstOrdinal > _firstOrdinal)
    {
      // A missed quiet interval cuts BEFORE the new run, even if every receipt shares one log second.
      _firstOrdinal = arrival.FirstOrdinal;
      _timedStart = double.NegativeInfinity;
      _windowSecond = MeterRecentWindow.NoSecond;
      return false;
    }
    var selected = new List<DerivedFight>();
    var edge = CaptureEdge(rows, activity, out var latestActivity, out var latestSequence);
    if (mode != 0)
    {
      if (double.IsNaN(_timedStart) && double.IsFinite(edge) && arrival.Through > _firstOrdinal)
      {
        _timedStart = edge - mode;
        return false;
      }
      foreach (var row in rows)
        if (Eligible(row, activity) && Activity(row, activity).Last >= _timedStart) selected.Add(row);
    }
    else
    {
      // Accepted attempts move the boundary even before their life has positive damage, on either side of projection.
      latestActivity = Math.Max(latestActivity, pending?.Latest ?? double.NegativeInfinity);
      latestSequence = Math.Max(latestSequence, pending?.LatestSequence ?? -1);
      edge = Math.Max(edge, latestActivity);
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
        var completedSequence = -1;
        var completionOrderKnown = true;
        foreach (var row in rows)
        {
          if (!Eligible(row, activity) || !IsCompleted(row)) continue;
          var end = CompletedAt(row);
          if (edge - end > CompletedDisplaySeconds || end < latestEnd || latestActivity > end) continue;
          if (end > latestEnd)
          {
            selected.Clear();
            latestEnd = end;
            completedSequence = -1;
            completionOrderKnown = true;
          }
          selected.Add(row);
          completionOrderKnown &= row.EndReason == DerivedFightEnd.Slain && row.DeathSequence >= 0;
          completedSequence = Math.Max(completedSequence,
            Math.Max(row.DeathSequence, Activity(row, activity).LastSequence));
        }
        // Inferred charm completions have no captured death order. Never guess that order from their last hit.
        if (completionOrderKnown && latestSequence > completedSequence) selected.Clear();
      }
    }

    var completedOnly = mode == 0 && selected.Count > 0 && IsCompleted(selected[0]);
    var timeout = mode != 0 ? mode : completedOnly ? CompletedDisplaySeconds : FightProjection.EngagementGapS;
    var deadline = (arrival.Through > _firstOrdinal ? arrival.Time : _retirementArrival.Time) + timeout;
    if (arrival.Through <= _firstOrdinal || now > deadline)
    {
      selected.Clear();
      if (mode != 0 && _retirementArrival.Through > _firstOrdinal && now > _retirementArrival.Time + timeout)
      {
        // A pruned receipt still expires, but exclusions alone do not spend its remaining time.
        // Retire only the checked prefix, never records appended while the receipt scan ran.
        _firstOrdinal = Math.Max(_firstOrdinal, capturedCount);
        _timedStart = double.NaN;
        _windowSecond = MeterRecentWindow.NoSecond;
      }
    }
    foreach (var row in selected)
      _windowSecond = Math.Max(_windowSecond, MeterRecentWindow.SecondOf(row.LastTime));
    scope = new DamageOverlayScope(selected, FromTime(mode), deadline, _firstOrdinal, _windowSecond);
    return true;
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
    _windowSecond = MeterRecentWindow.NoSecond;
    _retirementArrival = MeterArrival.None;
    _panels = MeterPanels.Both;
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

  private double CaptureEdge(IReadOnlyList<DerivedFight> rows, Func<DerivedFight, MeterLifeActivity> activity,
    out double latestActivity, out int latestSequence)
  {
    var edge = double.NegativeInfinity;
    latestActivity = double.NegativeInfinity;
    latestSequence = -1;
    foreach (var row in rows)
    {
      if (row.FactEnd <= _firstOrdinal) continue;
      var current = Activity(row, activity);
      latestActivity = Math.Max(latestActivity, current.Last);
      latestSequence = Math.Max(latestSequence, current.LastSequence);
      // Timed initialization and death grace still require positive, accepted damage.
      if (!current.HasDamage) continue;
      edge = Math.Max(edge, IsCompleted(row) ? Math.Max(current.Last, CompletedAt(row)) : current.Last);
    }
    return edge;
  }

  private static MeterLifeActivity Activity(DerivedFight row, Func<DerivedFight, MeterLifeActivity> activity) =>
    activity is null ? new MeterLifeActivity(row.DamageTotal > 0, row.LastTime, row.LastSequence) : activity(row);

  private static bool IsCompleted(DerivedFight row) => row.EndReason is DerivedFightEnd.Slain or DerivedFightEnd.Charmed;
  private static double CompletedAt(DerivedFight row) => double.IsNaN(row.DeathTime) ? row.LastTime : row.DeathTime;
}

internal sealed class DamageOverlayScope(IReadOnlyList<DerivedFight> rows, double fromTime,
  double expiresAt = double.NegativeInfinity, int firstOrdinal = 0, long windowSecond = MeterRecentWindow.NoSecond)
{
  internal IReadOnlyList<DerivedFight> Rows { get; } = rows;
  internal double FromTime { get; } = fromTime;
  internal double ExpiresAt { get; } = expiresAt;
  internal int FirstOrdinal { get; } = firstOrdinal;
  internal long WindowSecond { get; } = windowSecond;
}
