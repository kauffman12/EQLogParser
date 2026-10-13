using log4net;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;

namespace EQLogParser
{
  /*
   * The one place this app spends a forced collection, and it runs at exactly two moments, both synchronous and both
   * labelled in the status line as "Cleaning up.." so the stop-the-world has a name: a log load finished having actually
   * read lines (MainWindow.UpdateLoadingProgress), and Clear All (MainWindow.ClearAllFights, after its re-open). A
   * labelled pause is what makes synchronous acceptable, and neither moment is followed by a bulk read — the re-open
   * seeks to end of file and reads nothing — so nothing allocates behind the collector.
   *
   * A log CLOSE arms nothing: closing is almost always the first half of an open, and a pass between them lands inside
   * the next file's bulk read. Stats builds ask for nothing — boards keep their GC to the runtime and win by
   * allocating less. Measurements: docs/DesignNotes.md → "Garbage collection after loading a log".
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
