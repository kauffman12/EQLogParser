using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using log4net;

namespace EQLogParser
{
  /*
   * Asking the collector for its memory back at the three moments this app knows something just died.
   *
   * The runtime's own heuristics are good and this does not replace them: they decide when a collection is worth what it costs, and
   * forcing one on a timer is strictly worse than leaving it alone, because every forced collection promotes the young survivors into
   * older generations permanently - more resident memory, and a bigger, longer collection later. That is the trap the advice "never call
   * GC.Collect" is about, and it is why there is no timer here.
   *
   * What the heuristics do not know is what this program is doing. Three moments are worth asking anyway:
   *
   *   **a log file finished loading** - the parse allocated gigabytes of short-lived text, which is most of a load's pause (measured on a
   *      replay soak: 11.2 s stopped across the first minute while the heap went 624 to 2,997 MB), and the garbage is gone by definition;
   *   **the fight list was cleared** - the record cache and every parsed event drop together, so this is when a compacting pass has the most
   *      to return, and returned segments are the difference between a player's next log loading into free space or into fragmentation;
   *   **stats finished building** - a rebuild walks every record again and hands back a much smaller result.
   *
   * A collection stops every thread, so the only thing that matters about it is when it happens, and each of those is a moment the player
   * is not asking the interface for anything. That is also what makes it safe to fire on the stats rebuild: whichever way the wind blows on
   * someone's machine, the cost lands while the numbers are already on screen, never mid-pull. There is deliberately no "is a fight active"
   * guard - a loaded file leaves its last fight marked active until more log lines arrive to expire it, so such a guard would quietly veto the
   * useful trigger, and the interval below bounds what a rebuild can cost anyway.
   *
   * Two rules keep this from becoming the thing it was added to prevent. Requests return immediately: the collection runs on a pool thread
   * after a settle delay, so no caller - least of all the UI thread - sits inside a stop-the-world while its own work is still in flight.
   * And the interval rate-limits: fiddling a time filter five times, or loading a file straight after clearing, pays for one collection
   * rather than six, which is the same reason this class counts what it swallowed instead of doing it.
   */
  internal static class GcTidyUp
  {
    /* How long a request waits before collecting: enough for the caller's own layout and binding passes to finish, short enough that the
       memory is handed back while the player is still looking at the thing they just did. */
    internal const double DefaultSettleMs = 1500;

    /* One tidy per interval, however many triggers fire. A rebuild storm is a normal thing to do with the damage summary. */
    internal const double DefaultMinIntervalMs = 60_000;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private static int _tidyCount;
    private static int _suppressedCount;
    private static int _inFlight;
    private static long _lastTidyMs;

    /* Collections performed, for tests and for a heartbeat that wants to know whether the tidy ever ran at all. */
    internal static int TidyCount => Volatile.Read(ref _tidyCount);

    /* Requests swallowed by the interval or by an already-running tidy. */
    internal static int SuppressedCount => Volatile.Read(ref _suppressedCount);

    /*
     * False from the moment a request is taken until its collection has finished - which runs slightly past the count increment, so anything
     * waiting for "a tidy happened and I may ask again" waits on this rather than on the counter.
     */
    internal static bool Idle => Volatile.Read(ref _inFlight) == 0;

    /* Back to a virgin state; the collector itself is untouched, so this only resets counters. */
    internal static void Reset()
    {
      Volatile.Write(ref _tidyCount, 0);
      Volatile.Write(ref _suppressedCount, 0);
      Volatile.Write(ref _inFlight, 0);
      Volatile.Write(ref _lastTidyMs, 0);
    }

    /*
     * Ask for a tidy. Returns false when it was swallowed - an interval too short or a tidy already running - which is the normal answer
     * and never an error. The collection happens later, on a pool thread, whatever thread asked.
     */
    internal static bool Request(string reason, double settleMs = DefaultSettleMs, double minIntervalMs = DefaultMinIntervalMs)
    {
      // Claiming the slot first means two triggers in the same millisecond cannot both start a collection, which would be one long pause
      // followed by a second, pointless one over a heap the first just emptied.
      if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
      {
        Interlocked.Increment(ref _suppressedCount);
        return false;
      }

      var last = Volatile.Read(ref _lastTidyMs);
      if (last != 0 && Environment.TickCount64 - last < minIntervalMs)
      {
        Volatile.Write(ref _inFlight, 0);
        Interlocked.Increment(ref _suppressedCount);
        return false;
      }

      _ = Task.Run(async () =>
      {
        try
        {
          if (settleMs > 0)
          {
            await Task.Delay(TimeSpan.FromMilliseconds(settleMs)).ConfigureAwait(false);
          }

          TidyNow(reason);
        }
        catch (Exception ex)
        {
          Log.Error("Problem tidying the heap.", ex);
        }
        finally
        {
          Volatile.Write(ref _inFlight, 0);
        }
      });

      return true;
    }

    /*
     * The collection itself, synchronous and reported. CompactOnce is set on every call because the runtime resets it afterwards: leaving it
     * permanently on would compact every full collection, including the ones the runtime picks, which is the opposite of what this file is for.
     * Returns the log line it wrote so a caller - or a test - can read what happened without parsing the log.
     */
    internal static string TidyNow(string reason)
    {
      var before = PerfGc.Sample();
      var watch = Stopwatch.StartNew();

      try
      {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
      }
      catch (Exception ex)
      {
        Log.Error("Problem tidying the heap.", ex);
      }
      finally
      {
        Volatile.Write(ref _lastTidyMs, Environment.TickCount64);
        Interlocked.Increment(ref _tidyCount);
      }

      var after = PerfGc.Sample();
      var line = $"gc.tidy {reason}: heap {Mb(before.HeapBytes)} to {Mb(after.HeapBytes)} MB, working set {Mb(before.WorkingSetBytes)} to {Mb(after.WorkingSetBytes)} MB, " +
        $"gen2 +{Math.Max(0, after.Gen2 - before.Gen2)}, stopped {PerfGc.WindowPauseMs(before, after):0} ms, wall {watch.Elapsed.TotalMilliseconds:0} ms";

      PerfJournal.Note(line);
      return line;
    }

    private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);
  }
}
