using System;

namespace EQLogParser
{
  /*
   * The arithmetic a timer bar lives by: how long it may run, where it ends, when it is legal to wake up and say "over", and when an overlay is
   * allowed to take the row off screen. It lives here rather than inside TimerOverlayWindow/TriggerProcessor for one reason: every rule below was
   * found broken by a value that arrives from text, and text has no upper bound.
   *
   * A countdown's length comes from one of two places (TriggerProcessor.StartTimerAsync): the number typed into the trigger config, or — for the
   * timer types that allow it — a capture group named TS in the trigger's regex, through DateUtil.SimpleTimeToSeconds, which returns **uint**
   * seconds. Nothing downstream asked whether either was a sane duration: the only test on the whole path was "> 0". Measured against .NET 10 with
   * the real SimpleTimeToSeconds:
   *
   *   capture "1:30"        ->    90s  delay 90,000ms          fine
   *   capture "20:53"       -> 1,253s                          fine
   *   capture "999999999"   -> 999,999,999s  delay (int)(d*1000) = int.MaxValue, no throw
   *
   * That middle column is the stuck bar. Task.Delay takes an int of milliseconds and the cast SATURATES rather than throwing, so the detached task
   * whose only job is to take the row off the overlay goes to sleep for 24.8 days: the bar sits at 0:00 for the rest of the session, and since the
   * render loop stops only when the list is empty, that one row also keeps the overlay alive forever. Further out — a typed 1e12 — begin + TPS*d
   * wraps a signed long into the PAST, which reads to the display layer as "ended ages ago": nothing is drawn (remaining is guarded >= 0), so the
   * row becomes an invisible entry in a list that only the removal that will never arrive empties, and in "standard time" mode its DurationTicks is
   * the divisor for every other bar's progress, flattening the whole overlay.
   *
   * So: durations are clamped where they enter, arithmetic saturates instead of wrapping, and the overlay is allowed to reap a row whose
   * own owner has evidently forgotten it. All of it is on numbers, so it is testable without a window.
   */
  internal static class TimerLifecycle
  {
    /*
     * The longest a timer may run: a day. Every real use of this feature counts seconds to minutes — a buff, a cast recovery, a
     * respawn, a raid encounter — and a bar that counts down for weeks is not a timer, it is the bug described above. Clamping to a
     * generous ceiling rather than refusing keeps a misconfigured trigger working (it still fires, still speaks, still shows its bar)
     * while making the arithmetic around it safe: at this ceiling the delay is 86,400,000 ms (well inside an int) and the end stamp is
     * 8.6e9 ticks (nowhere near a long's limit), so neither saturating cast below can be reached.
     */
    public const double MaxDurationSeconds = 24 * 60 * 60;

    /*
     * How long a row may outlive its own EndTicks before the overlay reaps it. Two purposes. It gives the row's owner the right of way:
     * the scheduled removal, or an "end early" line, is the mechanism that is supposed to take the bar away and it is nearly always on
     * its way already; and it lets the last frame — the 0:00 one, which is a timer saying "it just finished" — actually be painted,
     * since the overlay's long tick is a few hundred milliseconds apart. A row that survives past this has an owner that never came,
     * which is the stuck-bar case, and it gets logged rather than quietly kept.
     */
    public const long ReapGraceTicks = 2 * TimeSpan.TicksPerSecond;

    /* "Idle forever", which is what the shipped settings mean when no idle timeout is configured: a greyed cooldown row stays until it is
       restarted or the overlay is stopped. Kept as a value rather than a bool so the reaper has one code path. */
    public const long IdleNever = -1;

    /*
     * Turn whatever the log offered into a duration the rest of the code can do arithmetic on: NaN, Infinity, negatives and zero all
     * become 0 (no timer, which is what "the capture wasn't a duration" means), and anything past the ceiling becomes the ceiling.
     * ClampDuration never throws and never returns a value that can overflow EndTicks or DelayMs.
     */
    internal static double ClampDuration(double seconds) =>
      !double.IsFinite(seconds) || seconds <= 0 ? 0 : Math.Min(seconds, MaxDurationSeconds);

    /* True when the raw figure was not usable as offered — either rejected outright (junk, negative) or cut down to the ceiling. The
       caller logs this: a TS capture of "999999999" is not a duration anyone meant, and that is worth a line in the log rather than a bar that
       never leaves the screen. */
    internal static bool NeedsClamping(double seconds) => seconds != ClampDuration(seconds);

    /*
     * When this row ends, saturating at long.MaxValue instead of wrapping. The guard exists because DateTime.Ticks is already ~6.4e17, so
     * a duration of the size a runaway capture produces puts begin + TPS*d within a factor of ten of the ceiling: the shipped expression
     * overflowed to a negative date in testing, which reads to the display layer as "ended a very long time ago".
     */
    internal static long EndTicks(long beginTicks, double durationSeconds)
    {
      var span = TPS(ClampDuration(durationSeconds));
      return span >= long.MaxValue - beginTicks ? long.MaxValue : beginTicks + span;
    }

    /* The same saturating span, shared by the end stamp and the reset stamp so the two cannot disagree about what is representable. */
    internal static long ResetTicks(long beginTicks, double durationSeconds) => EndTicks(beginTicks, durationSeconds);

    /*
     * Milliseconds for Task.Delay: never negative (Task.Delay throws on a negative value, and this call sits in a detached task whose
     * exception nobody would ever see), never above int.MaxValue, and 0 for a row with nothing left to count — which fires immediately
     * and removes the row, the correct fate for a zero-length timer.
     */
    internal static int DelayMs(double durationSeconds)
    {
      var ms = TPS(ClampDuration(durationSeconds)) / TimeSpan.TicksPerMillisecond;
      return ms <= 0 ? 0 : ms >= int.MaxValue ? int.MaxValue : (int)ms;
    }

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
     * Should the overlay still be holding this counting-down row? The owner is supposed to stop its own timers — the scheduled removal, or an
     * "end early" line — so a row still in the live list past its end, and past the grace that gives the owner right of way, is one whose owner
     * never came. Those are what stick: keep them and the overlay holds a row forever (the render loop only stops when the list is empty), and
     * the row has already stopped producing anything to draw, so it is invisible weight in a list that nothing else will ever offer for deletion.
     *
     * Note this ignores the "show reset" phase on purpose. A countdown in that mode shows its greyed cooldown bar either while its owner still
     * holds it (a few milliseconds past the end, well inside the grace) or after the owner stopped it and moved it to the overlay's idle list,
     * where RetainIdleRow applies. A row that survives in the live list this long past its end is not being displayed as a cooldown by design;
     * it is a lost row, and letting the reset phase vouch for it is how the leak stays.
     */
    internal static bool RetainRow(long endTicks, long nowTicks) =>
      endTicks == long.MaxValue || nowTicks <= endTicks + ReapGraceTicks;

    /*
     * Rows parked in the overlay's idle list — the greyed "this is on cooldown" placeholders a player asked for. They age per row, from the
     * moment they stopped being live (whichever of their two stamps is later: the countdown, or the reset that follows it), instead of only
     * when every other timer on the overlay has finished, which is how the shipped code does it and why a raid with something always running
     * never clears one. With no idle timeout configured the shipped meaning stands: idle forever.
     */
    internal static bool RetainIdleRow(long idleSinceTicks, long nowTicks, long idleTimeoutTicks) =>
      idleTimeoutTicks == IdleNever || idleTimeoutTicks <= 0 || nowTicks <= idleSinceTicks + idleTimeoutTicks;

    /* How far past its end this row already was when the reaper took it, for the log line that says which lifecycle hole it came through. */
    internal static double StaleSeconds(long endTicks, long nowTicks) =>
      endTicks == long.MaxValue ? 0 : Math.Max(0, nowTicks - endTicks) / (double)TimeSpan.TicksPerSecond;
  }
}
