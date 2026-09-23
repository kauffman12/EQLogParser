using log4net;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;

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
   * is not asking the interface for anything - PROVIDED the wait below is a wait for QUIET rather than a fixed nap. The stats rebuild is the
   * proof: the damage window's compute timer restarts on incoming data (MainWindow.cs:252,594), so while a fight is being written the three
   * builders ask again inside every second of it, and a 1.5 s delay measured from the FIRST request collected with the world stopped in the
   * middle of the pull that was still asking - one blocking compaction plus LOH compaction a minute, every minute of every fight, bounded only
   * by the interval. There is deliberately no "is a fight active" guard (a loaded file leaves its last fight marked active until more log lines
   * arrive to expire it, so such a guard would quietly veto the useful trigger); instead the settle is re-armed by every request, which makes
   * the quiet moment the trigger and needs no knowledge of what produced the noise. A continuous hour of logging therefore hands nothing back
   * until it stops, which is the correct trade: the memory is worth more than the pause while the log is still arriving.
   *
   * Two rules keep this from becoming the thing it was added to prevent. Requests return immediately: the collection runs on a pool thread
   * after a settle delay, so no caller - least of all the UI thread - sits inside a stop-the-world while its own work is still in flight.
   * And the interval rate-limits: fiddling a time filter five times, or loading a file straight after clearing, pays for one collection
   * rather than six, which is the same reason this class counts what it swallowed instead of doing it.
   */
  internal static class GcTidyUp
  {
    /* How long the stream has to have STOPPED before collecting: enough for the caller's own layout and binding passes to finish, short enough
       that the memory is handed back while the player is still looking at the thing they just did. Re-armed by every request - see the header. */
    internal const double DefaultSettleMs = 1500;

    /* How often the settling request re-reads the clock while waiting for quiet. A quarter of the wait, so a request that arrives late in a
       settle is noticed within a few hundred ms rather than at the end of it; bounded so neither a tiny nor a huge settle misbehaves. */
    private const double QuietPollFloorMs = 10;
    private const double QuietPollCeilMs = 250;

    /* One tidy per interval, however many triggers fire. A rebuild storm is a normal thing to do with the damage summary. */
    internal const double DefaultMinIntervalMs = 60_000;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private static int _tidyCount;
    private static int _suppressedCount;
    private static int _inFlight;
    private static long _lastTidyMs;
    private static long _lastRequestMs;
    private static int _epoch;

    /* Collections performed, for tests and for a heartbeat that wants to know whether the tidy ever ran at all. */
    internal static int TidyCount => Volatile.Read(ref _tidyCount);

    /* Requests swallowed by the interval or by an already-running tidy. */
    internal static int SuppressedCount => Volatile.Read(ref _suppressedCount);

    /*
     * False from the moment a request is taken until its collection has finished - which runs slightly past the count increment, so anything
     * waiting for "a tidy happened and I may ask again" waits on this rather than on the counter.
     */
    internal static bool Idle => Volatile.Read(ref _inFlight) == 0;

    /*
     * Back to a virgin state; the collector itself is untouched, so this only resets counters - except that it also retires every request still settling.
     * Reset takes the in-flight slot away from whoever held it, so a request that had not collected yet would be collecting on behalf of a state that no
     * longer exists: in the application that is a stop-the-world nobody is waiting for any more, and in a test run it lands inside the next class's
     * assertions - which is how an earlier StatsBuildersTest build, still sitting out its settle delay, ended up tidying in the middle of GcTidyUpTest.
     */
    internal static void Reset()
    {
      Interlocked.Increment(ref _epoch);
      Volatile.Write(ref _tidyCount, 0);
      Volatile.Write(ref _suppressedCount, 0);
      Volatile.Write(ref _inFlight, 0);
      Volatile.Write(ref _lastTidyMs, 0);
      Volatile.Write(ref _lastRequestMs, 0);
    }

    /*
     * Ask for a tidy. Returns false when it was swallowed - an interval too short or a tidy already running - which is the normal answer
     * and never an error. The collection happens later, on a pool thread, whatever thread asked.
     */
    internal static bool Request(string reason, double settleMs = DefaultSettleMs, double minIntervalMs = DefaultMinIntervalMs)
    {
      /* Stamped before anything else, including refusals: "somebody asked a moment ago" is the whole definition of busy here, and a request
         the interval swallowed still says the application is in the middle of something. This is what defers a tidy out of a live fight -
         the rebuild keeps re-arming the wait, and the collection happens when the re-arming stops. */
      Volatile.Write(ref _lastRequestMs, Environment.TickCount64);

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

      var epoch = Volatile.Read(ref _epoch);

      _ = Task.Run(async () =>
      {
        try
        {
          await WaitForQuiet(settleMs, epoch).ConfigureAwait(false);

          /* Stale by the time it woke: Reset() gave the slot to somebody else while this was settling, so the moment it was asked for is gone. */
          if (Volatile.Read(ref _epoch) != epoch)
          {
            return;
          }

          TidyNow(reason);
        }
        catch (Exception ex)
        {
          Log.Error("Problem tidying the heap.", ex);
        }
        finally
        {
          /* Releasing is only this request's to do while it still owns the slot; after a Reset the slot belongs to whoever reset, and clearing it here
             would let a second collection start beside one already running. */
          if (Volatile.Read(ref _epoch) == epoch)
          {
            Volatile.Write(ref _inFlight, 0);
          }
        }
      });

      return true;
    }

    /*
     * Wait until nothing has asked for a tidy for settleMs. This is the difference between handing memory back at the end of a rebuild and
     * stopping the world inside one: the damage window rebuilds on a 500 ms timer restarted by incoming data, so during a fight requests arrive
     * forever and any fixed delay measured from the first of them expires mid-pull. Waiting for the ASKING to stop needs no idea what is asking -
     * a fight, a filter being fiddled, a file loading - and it converges the moment whatever it was finishes.
     *
     * Polls rather than owning a timer per request because requests are allowed to keep arriving while this one waits, and a wait that can be
     * extended by somebody else's call is a deadline, not a duration. Returns early once Reset() has retired this request (the caller re-checks
     * the epoch and drops it), so a stale request neither spins nor sleeps out its whole settle.
     */
    private static async Task WaitForQuiet(double settleMs, int epoch)
    {
      if (settleMs <= 0)
      {
        return;
      }

      var pollMs = Math.Clamp(settleMs / 4, QuietPollFloorMs, QuietPollCeilMs);

      while (Volatile.Read(ref _epoch) == epoch)
      {
        var quietFor = Environment.TickCount64 - Volatile.Read(ref _lastRequestMs);
        if (quietFor >= settleMs)
        {
          return;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(pollMs, settleMs - quietFor))).ConfigureAwait(false);
      }
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

      /*
       * Announced before the call, not after it: a beat gap caused by this collection is classified while the collection runs, and the runtime publishes
       * the count and the pause only once every thread is going again. Without this note the watchdog reads "no collection in that gap" and blames a
       * profiler - which is what happened at 11:53:34, this line's own gen2 printing 794 ms stopped in the same millisecond.
       */
      PerfGc.NoteStopStart(reason);

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
        PerfGc.NoteStopEnd();
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
