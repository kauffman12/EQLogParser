using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * "Is a fight happening right now?", asked of rows that were derived after the fact.
   *
   * The legacy meter never had to ask: FightManager kept a set of overlay fights updated per line, so "something is going
   * on" was a dictionary somebody else maintained and the meter only had to look at it. A derived row is not that — it is a
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

    /*
     * How long the board of a mob we SAW die stays readable: 8 s, and it is a usability number, not an accounting one. Legacy
     * produced about the same: its overlay fought over `FightManager`'s fight set, a dying mob LEFT that set, so the board stopped
     * updating at the kill shot and blanked 7 s of no-updates later — "about eight or ten seconds after the kill".
     *
     * What it may NOT be used for is silence alone. A quiet stretch shorter than `GapS` that no death closed is a lull — running,
     * rezting, waiting for a mob to path — and 12 s of moving between swings must not reset damage nobody killed. So the dial's
     * tolerance stays `GapS` unless `LastKillT` says a death was actually seen, and only that shortens the wait to this number.
     * (An earlier cut of this rule used it for every quiet stretch, which is exactly the bug that sentence exists to prevent.)
     */
    internal const double KillQuietCloseS = 8;

    /* What a meter's quiet window is in seconds: `OverlayDamageMode` 0 means "on kill" (= the engagement gap), any other
     * value is that many seconds. Answers liveness — should the meter be open, is something still going. */
    internal static double TimeoutFor(int damageMode) => damageMode == 0 ? GapS : damageMode;

    /*
     * "How long may a quiet board stay on screen", in seconds. Time-based modes are that many seconds and ask nothing else.
     * Mode 0 ("on kill") depends on whether a DEATH was seen (`LastKillT`): a kill we saw earns the short courtesy close, while
     * silence alone gets the full `GapS`, because the only thing that ends an unnoticed pull is the same 30 s of nothing that
     * tells the fight list to start a new row for the next swing. So: damage tolerance is 30 s unless we watched it die.
     */
    internal static double ExpiryFor(int damageMode, bool killObserved)
        => damageMode == 0 ? (killObserved ? KillQuietCloseS : GapS) : damageMode;

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

    // The newest activity anywhere in these rows (dead rows excluded: a corpse does not make a meter want to open).
    // NaN when there is nothing to ask about — no rows, or none with a window.
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

    /*
     * "Damage came in since you last looked" — the question a meter asks when it has NO window on screen and wants to know
     * whether to open one. Two halves, and both are needed: something live on this capture, and newer row activity than the
     * caller last acted on. Without the second half the event would fire every derive through an idle tail; without the first
     * it would fire on the last hit of a kill and reopen a meter over a corpse.
     *
     * This is deliberately NOT "a new fight started": legacy's `EventsNewOverlayFight` fired on every damage line of a fight,
     * which is why closing a meter with the X during a pull brought it back within a line or two rather than at the next pull.
     */
    internal static bool HasFreshDamage(IReadOnlyList<DerivedFight> rows, double lastAnnouncedActivityT, double nowT,
                                        double gapS)
    {
      var newest = LatestActivityAt(rows);
      return !double.IsNaN(newest) && newest > lastAnnouncedActivityT && AnyLive(rows, nowT, gapS);
    }

    /*
     * The seconds a meter board covers, as a rule rather than as a field on a window.
     *
     * `storedStartT` is what the last tick used (-1 = never opened, or just cleared). The start survives everything except
     * the two things that legitimately end it: an explicit clear (the caller passes -1) and quiet longer than `timeoutS`
     * — the meter's own expiry dial. That survival is what makes a window CLOSED with the X come back "where it left off":
     * legacy got this for free because its running totals lived in statics, so a reopened window kept painting the same
     * accumulation. A derived board holds nothing between ticks, so if the start lived on the window instance it would be
     * reborn at "now" and the seconds already spent on the pull would vanish from the numbers — not from the capture, but
     * from the board. Note what does NOT age the stored start while no window is open: nothing runs out there, so the
     * reopen asks this rule once and gets the answer the dial implies — still inside the range means the whole pull.
     */
    /*
     * The seconds a meter's totals cover. `timeoutS` is the dial's answer to "how long may a quiet board stay up" —
     * `ExpiryFor`, which for "on kill" is short after a seen death and the full gap otherwise, so a lull cannot eat damage.
     */
    internal static double WindowStartFor(double storedStartT, double lastFactT, double nowT, double timeoutS)
    {
      /*
       * A meter that has never opened (or was just cleared) starts its window BACK by its own quiet window, not AT now.
       * Starting at now is what made a one-shot kill invisible: the mob's whole life — its only damage line and its death —
       * landed in the second before this window existed, `BuildOverlayStats` keeps rows whose `LastTime >= fromT`, so there was
       * nothing to paint, no fact ever set `lastFactT`, `Expired` never fires on NaN, and the board stayed empty until some LATER
       * swing happened to fall inside. Legacy never had this hole because its totals lived in statics the window merely displayed.
       * Reaching back one quiet window is what makes "it should still show me the total" true: a kill you just landed is inside the
       * window the meter opens for it, and the same reach-back costs nothing on a capture that has done nothing (an empty window is
       * an empty board either way).
       */
      if (storedStartT < 0) return nowT - timeoutS;
      return Expired(nowT, lastFactT, timeoutS) ? nowT : storedStartT;
    }

    /*
     * The newest KILL this capture saw inside these seconds, or NaN. "Saw" is the whole rule and it is already recorded: a row
     * ends `Slain` when the death line names it, versus `Gap` when an inactivity stretch merely assumed it, so this asks about
     * kills and never about silence — which is exactly the difference the meter needs.
     *
     * Not every row that dies is a kill WE made: a raid member's row (a charmed raider is an encounter row) and our own pet's row
     * end when they die too, and a rez must not read as "the pull is over". So both are refused here — the person by what the
     * timeline believed at the death, the pet by the flag the projection stamped for it.
     */
    internal static double LastKillT(IReadOnlyList<DerivedFight> rows, double fromT, double toT, EntityTimeline timeline)
    {
      if (rows is null) return double.NaN;

      var newest = double.NaN;
      for (var i = 0; i < rows.Count; i++)
      {
        var row = rows[i];
        if (row.EndReason != DerivedFightEnd.Slain || row.RaidPet) continue;

        // The death lands at the end of the row's window: a slain line follows the last exchange inside one engagement.
        var t = row.LastTime;
        if (double.IsNaN(t) || t < fromT || t > toT) continue;

        if (timeline is not null && timeline.IsConfirmedRaidPersonAt(row.Name, t)) continue;

        if (double.IsNaN(newest) || t > newest) newest = t;
      }

      return newest;
    }

    /* True when the board should zero: the newest fact predates `nowT` by more than the meter's quiet window. A window with
     * no facts at all (NaN) has nothing to expire — there is no board showing, and inventing an expiry for it would move the
     * start point forward on a capture that has not started yet. */
    internal static bool Expired(double nowT, double lastFactT, double timeoutS) =>
        !double.IsNaN(lastFactT) && nowT - lastFactT > timeoutS;

  }
}
