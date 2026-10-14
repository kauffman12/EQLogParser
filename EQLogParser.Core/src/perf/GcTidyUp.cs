using log4net;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;

namespace EQLogParser
{
  /*
   * The one place this app spends a forced collection, at exactly two moments, each labelled with PostLoadCleanup.Notice ("Cleaning up..") on the
   * application status line and on the fight list's band, so the stop-the-world has a name:
   *
   *   - **a load that actually read lines**: ARMED at 100 % (MainWindow.ArmLoadTidy, gated by LogReader.LoadAllocatedGarbage so a follow-from-end open
   *     that read nothing owes nothing) and PAID when that capture's first board trio answers (PayOwedLoadTidy). The arm is what holds the fight list's
   *     band up; the wait exists because there is nothing to collect at 100 % — a measured pass there took heap 366 to 341.8 MB on a 24 s read, since
   *     Server GC's concurrent collections sweep the read's temporaries during the read — while the first full derive and its boards allocate for seconds
   *     after it. Collecting on the build's pool worker, before the grids repaint, keeps our code out of the message loop and puts the pause behind a bar
   *     rather than under a grid the operator has begun to read; and
   *   - **Clear All** (MainWindow.ClearAllFights, through PayLoadTidyAsync), after its re-open: this is the one verb that drops a whole capture while the
   *     app keeps running, so returned segments are worth most here, and the re-open seeks to end of file and reads nothing.
   *
   * A log CLOSE pays nothing: closing is almost always the first half of an open, and a pass between them lands inside the next file's
   * bulk read — which is why a new OPEN resets the mark (PostLoadCleanup.Reset, with ownership asked again at the collector's own door).
   * Stats builds ask for nothing and pay nothing; only the load's FIRST trio pays, and it pays once per capture. A deferral needs its band: an earlier
   * cut deferred to the first contentful board build behind a backstop timer and a ticket gate with no loading UI left on screen, which read as "no cleanup
   * happened at all", so the timer and gate are gone and the mark — not a scheduler — does the waiting. Measurements: docs/DesignNotes.md →
   * "Garbage collection after loading a log".
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
