using System;
using System.Collections.Generic;

namespace EQLogParser.Mirror
{
  /*
   * "Is a fight happening right now?", asked of rows that were derived after the fact.
   *
   * The legacy meter never had to ask: FightManager kept a set of overlay fights updated per line, so "something is going
   * on" was a dictionary somebody else maintained and the meter only had to look at it. A mirror row is not that — it is a
   * life reconstructed from facts, and the only thing that says whether it is STILL a life is where its last fact sits
   * relative to the newest one in the capture. So the question gets answered here, over rows, on one clock:
   *
   *   `nowT` is the capture's own newest event, not wall time. A file being read at 170k facts/s runs minutes behind the
   *   wall, and a rule keyed to the clock would call the middle of a load "live" while its reader watches a progress bar —
   *   then go quiet exactly when the tail arrives. For a tailed live log the two coincide, which is why this reads as a
   *   choice rather than a workaround.
   *
   * No identity carve-outs. A raid-pet row (hidden from the fight list) and a charmed raider's row are live when their facts
   * are recent, because the damage they carry IS on the meter: hiding a row is a display decision about the LIST, and a meter
   * that opened for some of a pull's damage and not the rest would be a bug wearing a policy.
   */
  internal static class LiveFights
  {
    /*
     * The gap that means "still going" when the asker has no dial of its own: the same 30 s that decides whether two facts
     * are one life (FightProjection.EngagementGapS), so a row cannot be finished by one rule and live by another.
     */
    internal const double GapS = FightProjection.EngagementGapS;

    /* What a meter's quiet window is in seconds: `OverlayDamageMode` 0 means "on kill" (= the engagement gap), any other
     * value is that many seconds. One expression for the overlay's expiry and for this question, so they cannot drift. */
    internal static double TimeoutFor(int damageMode) => damageMode == 0 ? GapS : damageMode;

    /* The newest thing this row did in either direction. A row exists only because facts landed on it, but both windows can
     * still be empty (a row opened by something with no direction, e.g. a death marker), so its span is the fallback. */
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

    /*
     * A fight the capture just opened: live on this pass, and not live on the one before. Keyed on name + begin, so the same
     * row growing across passes is not news while another pull of the same name is. A row that had gone quiet and caught fire
     * again counts as new too — what a caller wants to know is whether damage started moving, not whether this mob was seen
     * once before. `previous` being null is the first pass over a capture, where anything live is news.
     */
    internal static DerivedFight FindNewLive(IReadOnlyList<DerivedFight> previous, IReadOnlyList<DerivedFight> current,
                                             double nowT, double gapS)
    {
      if (current is null) return null;

      HashSet<string> seen = null;
      if (previous is not null)
      {
        seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in previous)
        {
          if (IsLive(row, nowT, gapS)) seen.Add(KeyOf(row));
        }
      }

      foreach (var row in current)
      {
        if (IsLive(row, nowT, gapS) && seen?.Contains(KeyOf(row)) != true) return row;
      }

      return null;
    }

    // Begin time is seconds with a fraction; "R" keeps it exact and short. Names arrive in whatever case the log used, so the
    // key follows every other entity lookup in this engine and ignores case (and \n cannot appear in a name).
    private static string KeyOf(DerivedFight row) => $"{row.Name}\n{row.BeginTime:R}";
  }
}
