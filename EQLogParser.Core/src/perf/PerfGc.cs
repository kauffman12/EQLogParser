using System;

namespace EQLogParser
{
  /*
   * One sample of the garbage collector and the heap, cheap enough to take on a heartbeat.
   *
   * Collection is the other half of "the numbers stopped for a second": a gen2/compacting collection stops every managed thread,
   * including the one that paints combat text, and it looks identical from the player's side to someone else's slow code. The two
   * explanations want different fixes, so both numbers go on the same log line as the beat gap - a stall with gen2 collections in
   * the same window is memory pressure, a stall with none is somebody's loop, and the heartbeat keeps that evidence without anyone
   * having to think about catching it.
   *
   * Everything is read from the runtime's own counters; no profiler, no ETW, no added package. Sample never throws: diagnostics that
   * can fault are diagnostics that get removed the first time they cost a frame, and this one runs inside the render path's process.
   */
  internal static class PerfGc
  {
    internal readonly record struct Reading(long AllocBytes, long HeapBytes, long WorkingSetBytes, int Gen0, int Gen1, int Gen2, double PauseMs);

    /*
     * A blocking collection we asked for ourselves, written down by the code that asked.
     *
     * The watchdog cannot catch these by diffing counters. The runtime publishes a collection's count and its pause once the other threads are running
     * again, so a gap classified inside that window reads "no collection happened" - measured, same millisecond: `gc.tidy log loaded: … gen2 +1, stopped
     * 794 ms` beside `STOP-THE-WORLD 969 ms … profiler or gcdump, power management, or no CPU for anybody`. Whoever calls GC.Collect knows what it did,
     * which is one fact the counters do not deliver in time, so it notes the stop here and the gap line reads it.
     */
    internal readonly struct IntentionalStop
    {
      internal readonly string Reason;   // null: nothing has ever been noted, and an empty register explains nothing
      internal readonly long StartMs;
      internal readonly long EndMs;      // 0 while the collection is still running

      internal IntentionalStop(string reason, long startMs, long endMs)
      {
        Reason = reason;
        StartMs = startMs;
        EndMs = endMs;
      }

      internal bool Finished => EndMs != 0;

      /* NaN while in flight: a collection that has not stopped has no duration, and the sentence says so rather than inventing one. */
      internal double WallMs => Finished ? EndMs - StartMs : double.NaN;

      /* Does this bear on a gap between two clock readings? One still running does; a finished one only if it ended after the gap opened. */
      internal bool Overlaps(long fromMs, long toMs) =>
        Reason is not null && StartMs <= toMs && (!Finished || EndMs >= fromMs);
    }

    private const double BytesPerMb = 1024 * 1024.0;

    private static string _stopReason;
    private static long _stopStartMs;
    private static long _stopEndMs;

    /* Reads the collector. A failure leaves a zeroed reading, which prints as silence rather than as an error. */
    internal static Reading Sample()
    {
      try
      {
        var info = GC.GetGCMemoryInfo();

        return new Reading(
          GC.GetTotalAllocatedBytes(true),
          info.HeapSizeBytes,
          Environment.WorkingSet,
          GC.CollectionCount(0),
          GC.CollectionCount(1),
          GC.CollectionCount(2),
          GC.GetTotalPauseDuration().TotalMilliseconds);
      }
      catch (Exception)
      {
        return new Reading(0, 0, 0, 0, 0, 0, 0);
      }
    }

    /*
     * "We are about to stop the world on purpose", written before the call rather than after it, because the gap it causes is attributed while the
     * collection is still running. One tidy is in flight at a time - GcTidyUp holds its own flag - so these three fields never have two writers to tell
     * apart, and a reader that straddles a start sees last tidy's reason beside this one's in-flight mark: a cosmetic mix-up in one log sentence, on a
     * path that runs when a load finishes rather than in a frame.
     */
    internal static void NoteStopStart(string reason)
    {
      Volatile.Write(ref _stopStartMs, Environment.TickCount64);
      Volatile.Write(ref _stopEndMs, 0L);
      Volatile.Write(ref _stopReason, reason);
    }

    internal static void NoteStopEnd() => Volatile.Write(ref _stopEndMs, Environment.TickCount64);

    internal static IntentionalStop ReadStop() =>
      new(Volatile.Read(ref _stopReason), Volatile.Read(ref _stopStartMs), Volatile.Read(ref _stopEndMs));

    /* How many milliseconds of a window the collector spent with every thread stopped, from the runtime's own cumulative total. This is
     * the number that catches what the beat cannot see: a collection stops the beat monitor's timer along with everything else, so a beat
     * posted after the world resumed is on time and the watchdog reports nothing (measured: 2,292 ms stopped at 19:03:47 with "beat delay
     * max 0 ms" in the same window, and the only reason it was found at all is that a soak collector was running beside the app). The
     * cumulative counter keeps no such appointment - it adds the pause up whether or not anybody was watching, so subtracting two readings
     * says how much of the window was lost even when the window's beats were all punctual.
     */
    internal static double WindowPauseMs(in Reading from, in Reading to) => Math.Max(0, to.PauseMs - from.PauseMs);

    /*
     * The work done between two samples. Collection counts are differences - that is what a window means - while the heap and the
     * working set are levels, and allocation is a rate because a megabyte per second of garbage is the thing that predicts the next
     * collection rather than the byte count itself.
     *
     * There is deliberately no percentage here. `GC.GetGCMemoryInfo().PauseTimePercentage` used to print as "paused N% since start", and both halves of
     * that turned out to be false on a 69 minute run read against its own windows: it is a snapshot as of whichever collection the runtime last reported rather
     * than a running total: it sat at exactly `5.15%` across three heartbeats spanning 46 quiet minutes, one of which reports 762 ms of collector stop inside it, and where it did speak it disagreed with the summed
     * `stopped` figures on this same line by up to 2.7x (0.32% against 35.5 s of pause in 4,166 s of life = 0.85%) - two numbers one clause apart telling
     * a reader two different stories about the same hour. A lifetime ratio is worth printing, so the beat monitor builds one out of the same cumulative
     * counter as `stopped`, which makes the pair agree by construction; see PausePercent below.
     */
    internal static string Format(in Reading from, in Reading to, double seconds)
    {
      /* Clamped because these are read on a pool thread and compared against a sample taken on another: a reading can arrive behind the
         one it is subtracted from, and a heartbeat that prints negative bytes spends the reader's time on our arithmetic. */
      var allocBytes = Math.Max(0, to.AllocBytes - from.AllocBytes);

      return $"gc0 {Math.Max(0, to.Gen0 - from.Gen0)} gc1 {Math.Max(0, to.Gen1 - from.Gen1)} gc2 {Math.Max(0, to.Gen2 - from.Gen2)} stopped {WindowPauseMs(from, to):0} ms" +
        $" | heap {to.HeapBytes / BytesPerMb:0} MB ws {to.WorkingSetBytes / BytesPerMb:0} MB" +
        $" alloc {(seconds <= 0 ? 0 : allocBytes / BytesPerMb / seconds):0.0} MB/s";
    }

    /*Milliseconds of collector pause over a stretch of wall, as a percentage - the arithmetic behind "paused N%", which needs a span to divide by and
     * therefore belongs to whoever is holding one. The span is a TimeSpan on purpose: this takes milliseconds in its first argument, and every version of
     * "just pass the number you have" between those two has ended with a percentage off by a thousand. A zero or negative span is zero rather than NaN - an
     * unmeasured window has no opinion about the pause - and nothing here clamps to 100, because a figure over 100% is the evidence that someone divided by
     * the wrong thing, and a clamp would throw away the only clue.*/
    internal static double PausePercent(double pauseMs, TimeSpan span) =>
      span <= TimeSpan.Zero ? 0 : pauseMs / span.TotalMilliseconds * 100;
  }
}
