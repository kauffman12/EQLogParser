using System;

namespace EQLogParser
{
  /*
   * The arithmetic a timer bar lives by: how long it may run, where it ends, when it may wake up and say "over", and when an overlay may take the row off
   * screen. It lives here rather than in TimerOverlayWindow/TriggerProcessor because every rule below was found broken by a value that arrives from text, and
   * text has no upper bound — so all of it is testable on numbers, without a window (EQLogParser.Test/src/util/TimerLifecycleTest.cs).
   *
   * A countdown's length comes from the number typed into the trigger config or, for the timer types that allow it, from a capture group named TS, through
   * DateUtil.SimpleTimeToSeconds — which answers in **uint** seconds. Nothing downstream asked whether either was sane; the only test on the path was "> 0".
   * Measured against .NET 10 with the real parser, a capture of "999999999" offers 999,999,999s, and the removal delay `(int)(seconds * 1000)` SATURATES to
   * int.MaxValue without throwing: the detached task whose only job is taking the bar off the overlay sleeps 24.8 days, so the row sits at 0:00 for the rest of
   * the session — and since the render loop stops only when the list is empty, that row keeps the overlay alive too. Further out (a typed 1e12)
   * begin + TPS*seconds wraps a signed long into the PAST, which draws nothing (remaining is guarded >= 0), so the row cannot even be offered for removal, and in
   * "standard time" mode its DurationTicks is the divisor for every other bar's progress.
   *
   * So: clamp where the number enters, saturate rather than wrap in the arithmetic, refuse the rows that have no future, and let the overlay reclaim what its
   * owner evidently forgot.
   */
  internal static class TimerLifecycle
  {
    /*
     * The longest a timer may run: a day. Real use counts seconds to minutes — a buff, cast recovery, respawn, encounter — and a bar counting down for weeks is
     * not a timer but the bug above. Clamping to a generous ceiling rather than refusing keeps a misconfigured trigger working while making the arithmetic safe:
     * at this ceiling the delay is 86,400,000 ms (inside an int) and the span is 8.6e9 ticks, so neither saturating cast below can be reached in production.
     */
    public const double MaxDurationSeconds = 24 * 60 * 60;

    /*
     * How long a row may outlive its own EndTicks before the overlay reaps it. The row's owner — the scheduled removal, or an "end early" line — is the mechanism
     * that is supposed to take the bar away and is nearly always on its way already; and the last frame, the 0:00 one that says "it just finished", wants to be
     * painted, long ticks being a few hundred milliseconds apart. A row still unowned past this has an owner that never came: that is the stuck-bar case.
     */
    public const long ReapGraceTicks = 2 * TimeSpan.TicksPerSecond;

    /* "Idle forever", which is what the shipped settings mean when no idle timeout is configured. A value rather than a bool so the reaper has one code path. */
    public const long IdleNever = -1;

    /*
     * Turn whatever was offered into a duration the rest of the code can do arithmetic on: NaN, Infinity, negatives and zero become 0 (no timer, which is what
     * "that capture was not a duration" means), and anything past the ceiling becomes the ceiling. Never throws, never returns something EndTicks/DelayMs cannot take.
     */
    internal static double ClampDuration(double seconds) =>
      !double.IsFinite(seconds) || seconds <= 0 ? 0 : Math.Min(seconds, MaxDurationSeconds);

    /* Ticks in a span of seconds, saturating both ways so nothing upstream has to know that a day is not the largest number a capture can hold. */
    internal static long TPS(double seconds)
    {
      if (!double.IsFinite(seconds))
      {
        return long.MaxValue;
      }

      var ticks = seconds * TimeSpan.TicksPerSecond;
      if (ticks >= long.MaxValue)
      {
        return long.MaxValue;
      }

      return ticks <= long.MinValue ? 0 : (long)ticks;
    }

    /*
     * When this row ends, saturating at long.MaxValue instead of wrapping — DateTime.Ticks is already ~6.4e17, so a runaway duration puts begin + TPS*d within a
     * factor of ten of the ceiling, and the shipped expression overflowed to a negative date in testing, which reads to the display as "ended ages ago".
     */
    internal static long EndTicks(long beginTicks, double durationSeconds)
    {
      var span = TPS(ClampDuration(durationSeconds));
      return span >= long.MaxValue - beginTicks ? long.MaxValue : beginTicks + span;
    }

    /* The same saturating stamp for the reset phase, shared so the two cannot disagree about what is representable. */
    internal static long ResetTicks(long beginTicks, double durationSeconds) => EndTicks(beginTicks, durationSeconds);

    /*
     * Milliseconds for Task.Delay: never negative (Task.Delay throws on negative, and this sits in a detached task nobody observes), never above int.MaxValue,
     * and 0 for a row with nothing left to count — which fires at once and removes the row, the correct fate of a zero-length timer.
     */
    internal static int DelayMs(double durationSeconds)
    {
      var ms = TPS(ClampDuration(durationSeconds)) / TimeSpan.TicksPerMillisecond;
      return ms <= 0 ? 0 : ms >= int.MaxValue ? int.MaxValue : (int)ms;
    }

    /*
     * Should the overlay still be holding this counting-down row? The owner is supposed to stop its own timers, so a row in the live list past its end plus the
     * grace has an owner that never came — and keeping it is what sticks: the render loop only stops when the list is empty, and the row stopped producing anything
     * to draw, so nothing else would ever offer it for deletion again.
     *
     * This ignores the "show reset" phase on purpose. A cooldown in that mode is displayed either while its owner still holds it (milliseconds past the end, well
     * inside the grace) or after the owner stopped it and moved it to the idle list, where RetainIdleRow applies. A row surviving in the live list this long past
     * its end is not being shown as a cooldown by design; letting that phase vouch for it is how the leak stays.
     */
    internal static bool RetainRow(long endTicks, long nowTicks) =>
      endTicks == long.MaxValue || nowTicks <= endTicks + ReapGraceTicks;

    /*
     * May this row go on the overlay at all? Same clock as RetainRow, plus two conditions that only matter on the way in.
     *
     * Not canceled: Start is fire-and-forget (TriggerOverlayManager posts it and moves on) while Stop waits on the overlay's render semaphore, so nothing anywhere
     * keeps Add before Stop — and a Stop only removes what is already in the list, so one that arrives first is lost. That happens on a short countdown whose removal
     * task wakes while its own row is still queued, and under "restart timer" when the second of three messages in one batch cancels the first row before that
     * insert lands. Either way the insert would land a row whose end has passed: no model (the display guards on remaining >= 0), so nothing could ever take it away.
     * Canceled is set before any Stop is dispatched, in every cancellation path, which is what makes it safe to believe here.
     *
     * And not lengthless: a duration that came out unusable leaves DurationTicks at 0, and the display cannot draw "nothing" — DateUtil.FormatTicks answers "00:00"
     * for zero and for every negative value alike. One wrong frame in the normal mode; in "show reset" mode permanent, because the cooldown text *is*
     * FormatTime(DurationTicks) with IsRemoved false by design — an immortal greyed 00:00 under a spell that should read 02:00.
     */
    internal static bool AcceptsRow(long endTicks, long durationTicks, bool canceled, long nowTicks) =>
      !canceled && durationTicks > 0 && RetainRow(endTicks, nowTicks);

    /*
     * Rows parked in the overlay's idle list — the greyed "on cooldown" placeholders a player asked for. They age per row, from when they stopped being live
     * (whichever stamp is later: the countdown, or the reset after it), rather than only when every other timer on the overlay has finished, which is how the shipped
     * code does it and why a raid with something always running never clears one. With no idle timeout configured the shipped meaning stands: idle forever.
     */
    internal static bool RetainIdleRow(long idleSinceTicks, long nowTicks, long idleTimeoutTicks) =>
      idleTimeoutTicks == IdleNever || idleTimeoutTicks <= 0 || nowTicks <= idleSinceTicks + idleTimeoutTicks;
  }
}
