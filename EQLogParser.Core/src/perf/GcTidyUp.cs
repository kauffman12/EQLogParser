using log4net;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;

namespace EQLogParser
{
  /*
   * The one place this app spends a forced collection, at exactly two moments, both paid through MainWindow.PayLoadTidyAsync
   * and both labelled in the status line as "Cleaning up.." so the stop-the-world has a name:
   *
   *   - **a load that actually read lines** (MainWindow's progress pump, at 100 %), because that is the last moment the operator is
   *     still looking at loading UI, and the fight list keeps its loading band UP while a pass holds the mark (PostLoadCleanup), so the pause
   *     lands behind a bar instead of under a grid the operator has begun to read. The blocking collection suspends every managed thread — the
   *     pool thread it runs on is about who is inside the pause (our code, not the message loop), not about painting through it — and what the
   *     mark buys is that the bar still stands when threads resume, which is when this capture's first rows land: the cadence starts the first
   *     derive as soon as the lanes drain. LogReader.LoadAllocatedGarbage keeps a follow-from-end open (which read no line) from paying at all; and
   *   - **Clear All** (MainWindow.ClearAllFights), after its re-open: this is the one verb that drops a whole capture while the app
   *     keeps running, so returned segments are worth most here, and the re-open seeks to end of file and reads nothing.
   *
   * A log CLOSE pays nothing: closing is almost always the first half of an open, and a pass between them lands inside the next file's
   * bulk read — which is why a new OPEN resets the mark (PostLoadCleanup.Reset, with ownership asked again at the collector's own door).
   * Stats builds ask for nothing and pay nothing: they win by allocating less, and what they allocate is the runtime's to collect.
   * An earlier cut deferred the load tidy to the first contentful board build with a backstop timer behind it; that put the freeze in
   * the middle of an already-answered grid, so the deferral, the timer and the gate holding its tickets are all gone — one seam at 100 %,
   * and the band does the waiting. Measurements: docs/DesignNotes.md → "Garbage collection after loading a log".
   */
  internal static class GcTidyUp
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    /*
     * The collection. Callers run it where a pause is accepted and label it where a user can see. CompactOnce is set on
     * every call because the runtime resets it afterwards: leaving it permanently on would compact every full collection
     * the runtime picks for itself, which is the opposite of what this file is for.
     * The Debug line answers "did asking work" — heap and working-set deltas, gen2 count, wall time.
     */
    internal static void TidyNow(string reason)
    {
      var beforeHeap = GC.GetTotalMemory(forceFullCollection: false);
      var beforeWorkingSet = Environment.WorkingSet;
      var beforeGen2 = GC.CollectionCount(2);
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

      Log.Debug($"gc.tidy {reason}: heap {Mb(beforeHeap):0.#} to {Mb(GC.GetTotalMemory(forceFullCollection: false)):0.#} MB, " +
        $"working set {Mb(beforeWorkingSet):0.#} to {Mb(Environment.WorkingSet):0.#} MB, " +
        $"gen2 +{Math.Max(0, GC.CollectionCount(2) - beforeGen2)}, wall {watch.Elapsed.TotalMilliseconds:0} ms");
    }

    private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);
  }
}
