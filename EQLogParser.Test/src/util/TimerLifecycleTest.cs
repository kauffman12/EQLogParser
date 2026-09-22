using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  /*
   * The rules a timer bar lives by, tested on numbers instead of behind a window — which is the point of TimerLifecycle living in Core. Each case
   * below is a value that reached the shipped code through a log line or a config box and what it did there: a removal task that went to sleep for
   * 24.8 days, an end stamp that wrapped into the past, a row nothing would ever take off the overlay. The two sweep tests at the foot are the
   * "worst case" tester: they push the real capture parser's whole output range through the clamp and then ask, for every row that could ever be
   * created, whether some moment exists at which the overlay is allowed to remove it. A "no" there is a stuck bar, and that is the whole bug class.
   */
  [TestClass]
  public class TimerLifecycleTest
  {
    private const long Tps = TimeSpan.TicksPerSecond;

    /* A plausible DateTime.UtcNow.Ticks, so the arithmetic runs at the magnitude it runs in production. */
    private const long Begin = 638_000_000_000_000_000L;

    // ---- the clamp: what may become a countdown ----------------------------------------------

    [TestMethod]
    public void ClampDurationRejectsJunk()
    {
      foreach (var junk in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d, -0.5d })
      {
        Assert.AreEqual(0, TimerLifecycle.ClampDuration(junk), $"{junk} is not a duration");
        Assert.IsTrue(TimerLifecycle.NeedsClamping(junk), $"{junk} must be reported as clamped");
      }

      // Zero is not clamped, it is simply nothing, and the warning exists for the misconfigured trigger rather than for a timer
      // that legitimately has no time left to show.
      Assert.AreEqual(0, TimerLifecycle.ClampDuration(0d));
      Assert.IsFalse(TimerLifecycle.NeedsClamping(0d), "nothing asked for is not a misconfiguration");
    }

    [TestMethod]
    public void ClampDurationCeilsWhatItCannotRun()
    {
      // 999,999,999s is what a TS capture of "999999999" really produces (SimpleTimeToSeconds answers in uint seconds);
      // the rest are the magnitudes that break the end stamp and the delay cast outright.
      foreach (var huge in new[] { 86_401d, 1e6d, 999_999_999d, 4_294_967_295d, 1e12d, double.MaxValue })
      {
        Assert.AreEqual(TimerLifecycle.MaxDurationSeconds, TimerLifecycle.ClampDuration(huge), $"{huge:0} must be cut to the ceiling");
        Assert.IsTrue(TimerLifecycle.NeedsClamping(huge), $"{huge:0} must be reported as clamped");
      }
    }

    [TestMethod]
    public void ClampDurationLeavesRealCountdownsAlone()
    {
      // A day exactly on the ceiling is still allowed: the clamp is a fence, not a policy about what counts down.
      foreach (var real in new[] { 0.001d, 1.5d, 90d, 1253d, 15653d, TimerLifecycle.MaxDurationSeconds })
      {
        Assert.AreEqual(real, TimerLifecycle.ClampDuration(real), 1e-9, $"{real:0.###} must survive untouched");
        Assert.IsFalse(TimerLifecycle.NeedsClamping(real), $"{real:0.###} must not be logged as clamped");
      }
    }

    // ---- the stamps and the delay: arithmetic that may not wrap or saturate ---------------------

    [TestMethod]
    public void EndTicksSaturatesInsteadOfWrapping()
    {
      double runaway = 1e12;                                  // not const: the shipped arithmetic has to RUN to wrap
      var wrapped = Begin + (long)(Tps * runaway);            // the expression as it shipped, for the record
      Assert.IsTrue(wrapped < Begin || wrapped <= 0, $"expected the shipped arithmetic to wrap, got {wrapped}");

      // Clamping first is what makes the guard unreachable in production — which is the point, so the guard is checked on its own terms:
      // a duration that gets through the clamp always lands a stamp after the row began, and only an already-clamped value can be at the ceiling.
      var guarded = TimerLifecycle.EndTicks(Begin, runaway);
      Assert.AreEqual(Begin + (long)(TimerLifecycle.MaxDurationSeconds * Tps), guarded, "the worst duration ends a day out, not in the past");

      Assert.AreEqual(TimerLifecycle.TPS(90d), TimerLifecycle.EndTicks(Begin, 90d) - Begin);
      Assert.AreEqual(long.MaxValue, TimerLifecycle.TPS(runaway), "a span nobody can wait out sits at the ceiling instead of wrapping");
      Assert.AreEqual(Begin, TimerLifecycle.EndTicks(Begin, 0d), "a junk duration ends where it started");
    }

    [TestMethod]
    public void DelayMsIsAlwaysInsideWhatTaskDelayAccepts()
    {
      foreach (var seconds in new[] { 0d, -5d, double.NaN, double.PositiveInfinity, 0.2d, 90d, 86_400d, 999_999_999d, 4_294_967_295d, 1e12d, double.MaxValue })
      {
        var ms = TimerLifecycle.DelayMs(seconds);
        Assert.IsTrue(ms >= 0 && ms <= int.MaxValue, $"DelayMs({seconds:0}) = {ms} is outside what Task.Delay accepts");

        // Proof rather than arithmetic: a pre-cancelled token means the call returns at once, so the only way this throws
        // ArgumentOutOfRangeException is the argument itself being illegal. A saturated cast (which is what shipped) would
        // have shown up here as a 24.8 day sleep rather than as a failure, which is why the assertion is on the call.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
          _ = Task.Delay(ms, cts.Token);
        }
        catch (ArgumentOutOfRangeException ex)
        {
          Assert.Fail($"Task.Delay rejected {ms}ms from duration {seconds:0}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
          // expected: the token was already cancelled, which is the proof that the delay value itself was fine
        }
      }
    }

    [TestMethod]
    public async Task DelayMsOfNothingFiresImmediately()
    {
      // A zero-length timer must be removed at once. Task.Delay(-1) would never return and a negative cast would throw
      // inside a detached task nobody is watching, which is how a row becomes permanent.
      await Task.Delay(TimerLifecycle.DelayMs(0d));
      await Task.Delay(TimerLifecycle.DelayMs(double.NaN));
    }

    [TestMethod]
    public void DelayMsOfTheWorstCaptureIsADayNotForever()
    {
      // 86,400,000 ms is the longest anything may now wait to delete a bar. The shipped cast gave int.MaxValue = 24.8 days.
      Assert.AreEqual(86_400_000, TimerLifecycle.DelayMs(DateUtil.SimpleTimeToSeconds("999999999")));
      Assert.AreEqual(int.MaxValue, (int)(DateUtil.SimpleTimeToSeconds("999999999") * 1000d));
    }

    // ---- the reaper: who may still be on screen ----------------------------------------------

    [TestMethod]
    public void RetainRowGivesTheOwnerRightOfWay()
    {
      var end = Begin + 90 * Tps;

      // The owner's own removal — the scheduled end, or an "end early" line — is the mechanism that takes a bar away. Nothing here
      // may beat it to the row, and the 0:00 frame ("it just finished") is worth painting once.
      Assert.IsTrue(TimerLifecycle.RetainRow(end, end), "a row at its end is still the owner's");
      Assert.IsTrue(TimerLifecycle.RetainRow(end, end + TimerLifecycle.ReapGraceTicks / 2), "still inside the grace");
      Assert.IsTrue(TimerLifecycle.RetainRow(end, end + TimerLifecycle.ReapGraceTicks));

      // One tick past it, nobody came, and the row is gone rather than permanent.
      Assert.IsFalse(TimerLifecycle.RetainRow(end, end + TimerLifecycle.ReapGraceTicks + 1), "past the grace the overlay reaps");
    }

    [TestMethod]
    public void RetainRowReapsTheRowWhoseRemovalTaskSleptForever()
    {
      // The measured stuck bar: a capture of "999999999" clamped to a day, still on screen half an hour after that day.
      var end = TimerLifecycle.EndTicks(Begin, DateUtil.SimpleTimeToSeconds("999999999"));
      Assert.AreEqual(Begin + TimerLifecycle.MaxDurationSeconds * Tps, end);
      Assert.IsFalse(TimerLifecycle.RetainRow(end, end + 1_800 * Tps), "30 minutes past the ceiling is a stuck bar");

      // And the row whose end stamp wrapped into the past: it never drew anything, so nothing else would ever offer it for removal.
      double runaway = 1e12;
      var wrapped = Begin + (long)(Tps * runaway);
      Assert.IsTrue(wrapped < Begin, "the shipped stamp lands in the past");
      Assert.IsFalse(TimerLifecycle.RetainRow(wrapped, Begin), "an invisible row must not be kept forever");
    }

    [TestMethod]
    public void RetainRowKeepsARowWhoseStampSaturated()
    {
      // long.MaxValue is "as long as it may run", not "stale since the epoch".
      Assert.IsTrue(TimerLifecycle.RetainRow(long.MaxValue, Begin));
    }

    [TestMethod]
    public void IdleRowsAgeFromTheirOwnClock()
    {
      var idleSince = Begin + 90 * Tps;
      var timeout = 60 * Tps;

      Assert.IsTrue(TimerLifecycle.RetainIdleRow(idleSince, idleSince + 59 * Tps, timeout), "a fresh cooldown row stays");
      Assert.IsTrue(TimerLifecycle.RetainIdleRow(idleSince, idleSince + timeout, timeout));
      Assert.IsFalse(TimerLifecycle.RetainIdleRow(idleSince, idleSince + timeout + 1, timeout),
        "the cooldown row dies on its own clock: in a raid something else is always live, and the shipped per-overlay rule " +
        "only cleared idle rows once every timer had gone, which never happened");

      // No idle timeout configured means the shipped meaning: leave it up until it restarts.
      Assert.IsTrue(TimerLifecycle.RetainIdleRow(idleSince, idleSince + 10L * 365 * Tps, TimerLifecycle.IdleNever));
      Assert.IsTrue(TimerLifecycle.RetainIdleRow(idleSince, idleSince + 10L * 365 * Tps, 0));
    }

    // ---- the worst-case tester ----------------------------------------------------------------

    /* Captures a TS group could realistically be handed, plus the shapes a regex is happy to match. */
    private static readonly string[] HostileCaptures =
    [
      "1:30", "20:53", "4h:20m:53s", "0:01", "0", "-5", "abc", "", "999999999", "4294967295", "49710d", "99:99:99:99",
      "1:0:0", "86400", "86401", "2147483647", "1e30", "00:00:00", "1d", "24h", "9999:59:59"
    ];

    /*
     * Everything the shipped code did with a duration, run over the whole range of what a capture can offer — and, for each row that could ever
     * be put on screen, one question: is there a moment at which the overlay may remove it? A row for which there is not is a bar stuck at 0:00
     * for the rest of the session, which is the bug this whole pass exists to close. Also checks that no capture can make the delay illegal or
     * put an end stamp before the row began.
     */
    [TestMethod]
    public void WorstCaseCaptureSweep()
    {
      var report = new StringBuilder();
      var worstDelayMs = 0;
      var clamped = new List<string>();
      var zeroed = new List<string>();

      foreach (var capture in HostileCaptures)
      {
        // Straight through the real parser, so this sweep cannot drift away from what the trigger path actually computes.
        var offered = DateUtil.SimpleTimeToSeconds(capture);
        var duration = TimerLifecycle.ClampDuration(offered);
        var end = TimerLifecycle.EndTicks(Begin, duration);
        var delayMs = TimerLifecycle.DelayMs(duration);
        var reapAt = end + TimerLifecycle.ReapGraceTicks + 1;

        Assert.IsTrue(duration >= 0 && duration <= TimerLifecycle.MaxDurationSeconds, $"'{capture}' produced an unusable duration {duration:0}");
        Assert.IsTrue(delayMs >= 0 && delayMs <= int.MaxValue, $"'{capture}' produced a delay Task.Delay cannot take: {delayMs}ms");
        Assert.IsTrue(end >= Begin, $"'{capture}' put the end stamp before the row began: {end} < {Begin}");
        Assert.IsFalse(TimerLifecycle.RetainRow(end, reapAt), $"'{capture}' created a row the overlay could never remove");
        Assert.IsTrue(TimerLifecycle.RetainRow(end, end + TimerLifecycle.ReapGraceTicks), $"'{capture}' was reaped while its owner still had the right of way");

        worstDelayMs = Math.Max(worstDelayMs, delayMs);
        if (TimerLifecycle.NeedsClamping(offered))
        {
          clamped.Add(capture);
        }

        if (duration == 0)
        {
          zeroed.Add(capture);
        }

        report.AppendLine($"'{capture}' -> {offered,12}s  clamped={duration,9:0}s  delay={delayMs,9}ms");
      }

      // What the real parser makes of these strings. Pinned here so the sweep keeps meaning something if SimpleTimeToSeconds ever changes:
      // it answers in uint seconds, which is why a capture can offer 136 years and why nothing downstream may trust it.
      Assert.AreEqual(90u, DateUtil.SimpleTimeToSeconds("1:30"));
      Assert.AreEqual(15653u, DateUtil.SimpleTimeToSeconds("4h:20m:53s"));
      Assert.AreEqual(86400u, DateUtil.SimpleTimeToSeconds("1d"));
      Assert.AreEqual(4294944000u, DateUtil.SimpleTimeToSeconds("49710d"), "the parser expresses 136 years");
      Assert.AreEqual(0u, DateUtil.SimpleTimeToSeconds("99:99:99:99"), "segments out of range are not a time at all");
      Assert.AreEqual(0u, DateUtil.SimpleTimeToSeconds("abc"));

      // The ceiling in numbers, so a future change to MaxDurationSeconds has to explain itself here, and a count of both failure directions:
      // captures far too big to run (cut down, and warned about) and captures that were never a time to begin with (zero, no timer).
      Assert.AreEqual(86_400_000, worstDelayMs, report.ToString());
      Assert.AreEqual("2147483647, 4294967295, 49710d, 86401, 999999999", string.Join(", ", clamped.Order()),
        $"the captures too big to run changed:{report}");
      Assert.AreEqual(", -5, 0, 00:00:00, 1e30, 99:99:99:99, 9999:59:59, abc", string.Join(", ", zeroed.Order()),
        $"the captures that were never a time changed:{report}");
    }

    /*
     * The burst case: one trigger hit by three messages in the same batch with "restart" set, each new message cancelling the row before it. Add is
     * fire-and-forget and both add and stop queue on the overlay's render semaphore, which does not promise to serve them in the order they were posted, so
     * every service order is a candidate reality. Enumerated for all six operations (A1 S1 A2 S2 A3 S3): with the check at the door every order ends with an
     * empty list, and the same enumeration under the shipped unconditional insert strands a row in most of them — which is what "stuck at 0:00, never
     * removed" looked like from the outside.
     */
    [TestMethod]
    public void ThreeMessagesAtOnceNeverLeaveARowBehind()
    {
      // A "quick time" trigger: short enough that the removal task can wake while its own row is still queued behind a semaphore wait.
      var ends = new[]
      {
        TimerLifecycle.EndTicks(Begin, 0.4d),
        TimerLifecycle.EndTicks(Begin + Tps / 10, 0.4d),
        TimerLifecycle.EndTicks(Begin + 2 * Tps / 10, 0.4d),
      };

      // All three stops have been issued by the time the overlay gets around to serving any of this.
      var now = Begin + Tps;
      var strandedWithoutTheCheck = 0;

      for (var rank = 0; rank < 720; rank++)
      {
        var order = NthPermutation(6, rank);
        var trail = new StringBuilder("service order: ");
        var list = new List<int>(3);
        var oldList = new List<int>(3);
        var canceled = new bool[3];

        for (var step = 0; step < order.Length; step++)
        {
          var op = order[step];
          var row = op / 2;
          trail.Append(op % 2 == 0 ? $"add{row} " : $"stop{row} ");

          if (op % 2 == 0)
          {
            // The window's StartTimerAsync, with the check at the door.
            if (TimerLifecycle.AcceptsRow(ends[row], canceled[row], now))
            {
              list.Add(row);
            }

            // ...and without it, which is how this shipped.
            oldList.Add(row);
          }
          else
          {
            // StopTimerAsync: it cancels, and removes whatever is in the list at that moment. Nothing it does can reach a row that has not arrived.
            canceled[row] = true;
            list.Remove(row);
            oldList.Remove(row);
          }
        }

        Assert.AreEqual(0, list.Count, $"a cancelled row survived this interleaving — {trail}");

        if (oldList.Count > 0)
        {
          strandedWithoutTheCheck++;
        }
      }

      Assert.IsTrue(strandedWithoutTheCheck > 500,
        $"expected most service orders to have stranded a row before the check, got {strandedWithoutTheCheck}");
    }

    /* The rank-th permutation of 0..count-1, so the enumeration above is deterministic and every ordering gets its turn. */
    private static int[] NthPermutation(int count, int rank)
    {
      var pool = new List<int>(count);
      for (var i = 0; i < count; i++)
      {
        pool.Add(i);
      }

      var result = new int[count];
      var remaining = rank;

      for (var slot = 0; slot < count; slot++)
      {
        var block = 1;
        for (var f = pool.Count - 1; f > 0; f--)
        {
          block *= f;
        }

        var index = remaining / block;
        remaining %= block;
        result[slot] = pool[index];
        pool.RemoveAt(index);
      }

      return result;
    }

    [TestMethod]
    public void ARowThatArrivesAfterItsOwnStopIsRefused()
    {
      var quick = TimerLifecycle.EndTicks(Begin, 0.4d);

      // Cancelled: its owner is finished with it, and the stop that would have removed it has already been spent.
      Assert.IsFalse(TimerLifecycle.AcceptsRow(quick, true, Begin), "a cancelled row must not be inserted just because nothing stopped it");

      // Not cancelled but already past its end plus the grace: same fate, nothing could take it away afterwards.
      Assert.IsFalse(TimerLifecycle.AcceptsRow(quick, false, quick + TimerLifecycle.ReapGraceTicks + 1));

      // And the ordinary cases must keep working — a slow dispatcher may not cost a player their timer.
      Assert.IsTrue(TimerLifecycle.AcceptsRow(quick, false, Begin));
      Assert.IsTrue(TimerLifecycle.AcceptsRow(quick, false, quick + TimerLifecycle.ReapGraceTicks / 2),
        "a row that arrives late but still inside its own grace is drawn, not refused");
    }

    /*
     * Same question under a raid: thousands of rows, hostile durations, random starts, and one row in four whose owner never sends the stop that
     * was supposed to take it away (a lost dispatch, a stop routed to another window, a saturated sleep). Every one of them must be reapable, none
     * may be reaped early, and the whole overlay must empty. This is the case that used to be unbounded: with a saturated removal task the answer was
     * "the row leaves when the process does".
     */
    [TestMethod]
    public void WorstCaseOverlaySoak()
    {
      var rand = new Random(20260802);
      var rows = new List<(long End, bool OwnerComing)>();

      for (var i = 0; i < 5_000; i++)
      {
        var capture = HostileCaptures[rand.Next(HostileCaptures.Length)];
        var duration = TimerLifecycle.ClampDuration(DateUtil.SimpleTimeToSeconds(capture) + rand.NextDouble() * 600);
        var begin = Begin + rand.Next(0, 3_600) * Tps;

        rows.Add((TimerLifecycle.EndTicks(begin, duration), rand.Next(4) != 0));
      }

      var reapedEarly = 0;
      var neverReaped = 0;
      var lastReap = long.MinValue;

      foreach (var (end, ownerComing) in rows)
      {
        // Not one tick before the grace runs out, the reaper must leave a live row alone...
        if (!TimerLifecycle.RetainRow(end, end + TimerLifecycle.ReapGraceTicks))
        {
          reapedEarly++;
        }

        // ...and by a second past it, whether or not the owner ever shows up.
        if (TimerLifecycle.RetainRow(end, end + TimerLifecycle.ReapGraceTicks + 1))
        {
          neverReaped++;
        }

        lastReap = Math.Max(lastReap, end + TimerLifecycle.ReapGraceTicks + 1);
      }

      Assert.AreEqual(0, reapedEarly, "the reaper took a row that was still the owner's to remove");
      Assert.AreEqual(0, neverReaped, "these rows can never leave the overlay: that is the stuck-bar bug");

      // The long tick has to see every one of those moments: it runs for as long as the list is non-empty, and a day plus
      // the grace is the most any row can now be waiting for.
      Assert.IsTrue(lastReap - Begin <= (long)(TimerLifecycle.MaxDurationSeconds * Tps) + 3_700 * Tps,
        "a row outlived the ceiling plus its start window, so the overlay cannot still be holding it");

      // And a bound on the grace itself, checked against rows that actually exist rather than against its own constant: long enough to cover a
      // long tick (the bar gets its last frame painted), short enough that nobody ever waits on a lost row.
      var grace = TimerLifecycle.ReapGraceTicks;
      Assert.IsTrue(rows.Count > 0 && grace >= Tps && grace <= 5 * Tps, $"grace of {grace / Tps:0.###}s is not a bar of time");
    }
  }
}
