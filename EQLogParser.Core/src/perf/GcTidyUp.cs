using log4net;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;

namespace EQLogParser
{
  /*
   * The only place this app forces a collection, at two labelled moments (PostLoadCleanup.Notice on the status line and on the fight list's band):
   *   - a load that read lines: ARMED at 100 % (MainWindow.ArmingLoadTidy) and PAID when that capture's first board trio answers, on the build's pool
   *     worker before the grids repaint — there is nothing to collect at 100 % because concurrent GC swept the read during it, the first derive is what
   *     allocates, and a pause behind a bar reads better than one under a grid the operator has started to read;
   *   - Clear All, after its re-open — the one verb that drops a whole capture while the app keeps running.
   * A log CLOSE pays nothing (closing is the first half of an open), stats builds ask and pay nothing, and only a capture's FIRST trio pays.
   * Measurements, and the retired backstop timer and ticket gate: docs/DesignNotes.md → "Garbage collection after loading a log".
   */
  internal static class GcTidyUp
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

        /*
         * The collection itself. CompactOnce is set on every call because the runtime resets it; leaving it on would compact every collection the
         * runtime picks for itself. The Debug line (heap, working set, gen2 count, wall) answers "did asking work".
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
