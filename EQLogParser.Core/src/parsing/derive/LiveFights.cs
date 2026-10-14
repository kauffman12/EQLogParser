using System;
using System.Collections.Generic;

namespace EQLogParser
{
  // Capture-relative liveness for row queries. Overlay accounting/display use DamageOverlayLifecycle instead.
  internal static class LiveFights
  {
    internal const double GapS = FightProjection.EngagementGapS;

    internal static double TimeoutFor(int damageMode) => damageMode == 0 ? GapS : damageMode;

    internal static double LastActivityAt(DerivedFight row)
    {
      var t = row.LastDamageTime;
      if (!double.IsNaN(row.LastTankingTime) && (double.IsNaN(t) || row.LastTankingTime > t)) t = row.LastTankingTime;
      return double.IsNaN(t) ? row.LastTime : t;
    }

    internal static bool IsLive(DerivedFight row, double nowT, double gapS) =>
      row is { Dead: false } && nowT - LastActivityAt(row) <= gapS;

    internal static bool AnyLive(IReadOnlyList<DerivedFight> rows, double nowT, double gapS)
    {
      if (rows is null) return false;
      for (var i = 0; i < rows.Count; i++)
      {
        if (IsLive(rows[i], nowT, gapS)) return true;
      }
      return false;
    }

    internal static double LatestActivityAt(IReadOnlyList<DerivedFight> rows)
    {
      if (rows is null) return double.NaN;
      var newest = double.NaN;
      for (var i = 0; i < rows.Count; i++)
      {
        var row = rows[i];
        if (row.Dead) continue;
        var t = LastActivityAt(row);
        if (!double.IsNaN(t) && (double.IsNaN(newest) || t > newest)) newest = t;
      }
      return newest;
    }

    // A live-row census, not an overlay-opening rule: completed one-shots still deserve an overlay.
    internal static bool HasFreshDamage(IReadOnlyList<DerivedFight> rows, double lastAnnouncedActivityT, double nowT, double gapS)
    {
      var newest = LatestActivityAt(rows);
      return !double.IsNaN(newest) && newest > lastAnnouncedActivityT && AnyLive(rows, nowT, gapS);
    }
  }
}
