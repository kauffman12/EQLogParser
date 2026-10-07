namespace EQLogParser
{
  /*
   * The clock a fact row carries.
   *
   * A fact's time used to be stored as the dotnet-epoch second itself (year 0001), which needs a long: ~6.3e10 in the
   * 2020s, and the comment on the old field records that an `int` overflowed silently and corrupted every timestamp it
   * wrote. That is still true of the RAW value. What was never necessary is storing the raw value: these rows are the most
   * numerous thing the engine keeps (a raid night is ~7 M damage facts plus ~4 M heals), and eight bytes of epoch was a
   * quarter of a damage row for a number that fits in "seconds since 2000" until the year 2068.
   *
   * So a row stores seconds-since-2000-01-01 and hands back what it always handed back — the dotnet-epoch second — through
   * `DamageFact.TimeS` / `HealFact.TimeS`. Every rule, window, digest and board downstream compares, subtracts and hashes
   * exactly the number it has always seen, which is the entire point: an epoch shift must not reach a single one of them.
   * The two functions below are the only places the base is known, and both are total rather than wrapping, because
   * wrapping IS the original defect — `unchecked((int)longValue)` produced a timestamp that every comparison still believed.
   *
   * Range, so nobody has to re-derive it: int seconds around 2000-01-01 spans 1931 through 2068. The oldest capture in the
   * local corpus is from 2022 and the client's log format postdates 1999, so the window is not a live concern; what keeps it
   * honest is that a value outside it is clamped AND counted (`OutOfRange`), never folded.
   */
  internal static class FactTime
  {
    // Seconds between .NET's epoch (year 0001) and 2000-01-01: 63,082,281,600. Timezone-free by construction — both
    // sides are DateTime-origin second counts, and EverQuest's stamps are local wall time on both ends of that.
    internal const long EpochSeconds = 63082281600L;

    /*
     * "This row has no time." A fact whose BeginTime never resolved arrives as 0 dotnet-epoch seconds today and is stored
     * as 0 today, so the pair of conversions below keeps that exact round trip (0 → sentinel → 0) rather than inventing a
     * floor of 1931 for it. Nothing treats 0 as a real second; `FightSummarySource`'s NaN/zero-window guards already own
     * that case, and this row has to keep reading the way it read before the clock shrank.
     */
    internal const int NoTime = int.MinValue;

    /// <summary>The newest second a row can carry: 2068-01-19. Past it, the clock is clamped and counted.</summary>
    internal const int MaxStored = int.MaxValue;

    /* How many timestamps fell outside the representable window and were clamped. Zero means "never", which is what a test
     * asserts: clamping keeps one bad line from failing a capture, and the counter is why a clamp can never become the new
     * silent bug. */
    internal static long OutOfRange;

    /// <summary>dotnet-epoch seconds (the number the pipeline hands around) → what a row stores.</summary>
    internal static int FromEpochSeconds(long epochSeconds)
    {
      if (epochSeconds == 0) return NoTime;

      var stored = epochSeconds - EpochSeconds;
      if (stored == NoTime) return MaxStored;   // the one colliding value: unreachable in practice (1931-10-25), never ambiguous

      if (stored < NoTime || stored > MaxStored)
      {
        OutOfRange++;
        return stored < 0 ? NoTime + 1 : MaxStored;
      }

      return (int)stored;
    }

    /// <summary>What a row stores → the dotnet-epoch second every consumer has always read.</summary>
    internal static long ToEpochSeconds(int stored) => stored == NoTime ? 0L : (long)stored + EpochSeconds;

    /// <summary>The double form the projection uses (`BeginTime` is dotnet-epoch seconds in a double). Rounds like the old seam.</summary>
    internal static int FromEpochSeconds(double epochSeconds)
      => double.IsNaN(epochSeconds) || epochSeconds <= 0 ? NoTime : FromEpochSeconds((long)Math.Round(epochSeconds));

    /// <summary>Reset the clamp tally (per capture, and between tests).</summary>
    internal static void ResetOutOfRange() => OutOfRange = 0;
  }
}
