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
    internal readonly record struct Reading(long AllocBytes, long HeapBytes, long WorkingSetBytes, int Gen0, int Gen1, int Gen2, double PausePercent, double PauseMs);

    private const double BytesPerMb = 1024 * 1024.0;

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
          info.PauseTimePercentage,
          GC.GetTotalPauseDuration().TotalMilliseconds);
      }
      catch (Exception)
      {
        return new Reading(0, 0, 0, 0, 0, 0, 0, 0);
      }
    }

    /*
     * How many milliseconds of a window the collector spent with every thread stopped, from the runtime's own cumulative total. This is
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
     * collection rather than the byte count itself. The pause figure is the runtime's own share of run time spent stopped, which is a
     * lifetime level and printed as one, with the label on it because everything beside it is a window: managed code cannot get per-collection
     * timings without registering an event listener, and that is not a price worth paying for a tidier number in a log line - the collection
     * deltas next to the allocation rate are what tell memory pressure from somebody's loop, which is the question the line exists to answer.
     * Read it as dilution: a soak that reports 1.9% since start while the allocation rate sits at 800 MB/s says the worst of the collector is
     * behind the line, not that the window was cheap. (Measured on a 21 minute replay: 22.4 s of pause total, 10.7 s of it in the first minute.)
     */
    internal static string Format(in Reading from, in Reading to, double seconds)
    {
      /* Clamped because these are read on a pool thread and compared against a sample taken on another: a reading can arrive behind the
         one it is subtracted from, and a heartbeat that prints negative bytes spends the reader's time on our arithmetic. */
      var allocBytes = Math.Max(0, to.AllocBytes - from.AllocBytes);

      return $"gc0 {Math.Max(0, to.Gen0 - from.Gen0)} gc1 {Math.Max(0, to.Gen1 - from.Gen1)} gc2 {Math.Max(0, to.Gen2 - from.Gen2)} stopped {WindowPauseMs(from, to):0} ms" +
        $" | heap {to.HeapBytes / BytesPerMb:0} MB ws {to.WorkingSetBytes / BytesPerMb:0} MB" +
        $" alloc {(seconds <= 0 ? 0 : allocBytes / BytesPerMb / seconds):0.0} MB/s" +
        $" paused {to.PausePercent:0.##}% since start";
    }
  }
}
