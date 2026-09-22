using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The collector half of a stall report. Two explanations look identical to a player — somebody else's code held the UI thread, or
   * the runtime stopped the world to collect — and they want opposite fixes, so the numbers that tell them apart are worth pinning:
   * collection counts are differences over the heartbeat's window rather than lifetime levels (a level says "this process has run for
   * hours", a delta says "three collections happened during that freeze"), allocation is stated as a rate because the byte count
   * itself predicts nothing, and no reading may ever print a negative.
   */
  [TestClass]
  public sealed class PerfGcTest
  {
    /* Allocating has to show up as allocated, which is the only reason to take a sample at all. */
    [TestMethod]
    public void TheSampleSeesWhatWasAllocated()
    {
      var before = PerfGc.Sample();
      var kept = new List<byte[]>();

      for (var i = 0; i < 8; i++)
      {
        kept.Add(new byte[256 * 1024]); // 2 MB, held so the count cannot be argued away as garbage
      }

      var after = PerfGc.Sample();
      GC.KeepAlive(kept);

      Assert.IsTrue(after.AllocBytes - before.AllocBytes >= 1024 * 1024,
        $"2 MB allocated and the runtime reported {(after.AllocBytes - before.AllocBytes) / 1024} KB");
      Assert.IsTrue(after.WorkingSetBytes > 0, "the working set is what says whether the overlay's buffers are the problem");
      Assert.IsTrue(after.Gen0 >= before.Gen0 && after.Gen2 >= before.Gen2, "collection counts never run backwards");
    }

    /* A window in which nothing was collected must read as nothing collected: this is the line that clears the collector of a freeze. */
    [TestMethod]
    public void CollectionCountsAreDeltasOverTheWindow()
    {
      var from = new PerfGc.Reading(1000, 2048, 4096, 5, 2, 1, 3.0, 100);
      var to = new PerfGc.Reading(9000, 2048, 4096, 8, 2, 1, 7.5, 380);

      var line = PerfGc.Format(from, to, 10);
      StringAssert.Contains(line, "gc0 3");
      StringAssert.Contains(line, "gc1 0");
      StringAssert.Contains(line, "gc2 0");
    }

    /* Eight megabytes over ten seconds is 0.8 MB/s: the rate is the number that predicts the next collection, not the total. */
    [TestMethod]
    public void AllocationIsPrintedAsARate()
    {
      var from = new PerfGc.Reading(0, 0, 0, 0, 0, 0, 0, 0);
      var to = new PerfGc.Reading(8L * 1024 * 1024, 0, 0, 0, 0, 0, 0, 0);

      // Built the same way the code builds it, so a locale that writes decimals with a comma is not read as a failure.
      var expected = $"alloc {(8.0 * 1024 * 1024 / (1024 * 1024) / 10):0.0} MB/s";
      StringAssert.Contains(PerfGc.Format(from, to, 10), expected);
    }

    /*
     * Counters are read on one thread and compared on another, so a reading can arrive behind the one it is subtracted from — and a
     * heartbeat that prints "alloc -3.1 MB/s" spends the reader's time on arithmetic instead of on the freeze. The clamp is the
     * decision; zero is the honest answer to a window that cannot be measured.
     */
    [TestMethod]
    public void ABackwardsReadingNeverPrintsANegativeRate()
    {
      var from = new PerfGc.Reading(50L * 1024 * 1024, 0, 0, 0, 0, 0, 0, 800);
      var to = new PerfGc.Reading(1024, 0, 0, 0, 0, 0, 0, 40);

      Assert.IsFalse(PerfGc.Format(from, to, 10).Contains("-"), "no part of a heartbeat may read as negative bytes");
    }

    /*
     * The pause figure is the runtime's lifetime share, not the window's, so it has to say so: a replay that spends its first minute at 18%
     * and the next twenty at 0.9% prints 1.9% by the end, and a reader who took that for the window lets the collector off the hook.
     */
    [TestMethod]
    public void ALifetimePauseFigureSaysItIsLifetime()
    {
      var from = new PerfGc.Reading(0, 0, 0, 0, 0, 0, 3.0, 100);
      var to = new PerfGc.Reading(0, 0, 0, 0, 0, 0, 7.5, 100);

      // Built the same way the code builds it, so a comma-decimal locale is not read as a failure.
      StringAssert.Contains(PerfGc.Format(from, to, 10), $" paused {(7.5):0.##}% since start");
    }

    /*
     * The window's own stopped time, next to the collection deltas. This is the figure the beat monitor structurally cannot produce: a
     * collection stops the watchdog's timer along with every other thread, so the beat posted after it is on time and the heartbeat reads
     * "beat delay max 0 ms" through a two second freeze (measured: 2,292 ms stopped at 19:03:47 with the beats in that window punctual). The
     * runtime's cumulative pause counter has no such appointment to keep, so the difference between two readings says how much of the window
     * was lost no matter who was watching.
     */
    [TestMethod]
    public void AWindowReportsTheTimeTheWorldWasStopped()
    {
      var from = new PerfGc.Reading(0, 0, 0, 0, 0, 0, 4.0, 1000);
      var to = new PerfGc.Reading(0, 0, 0, 0, 0, 1, 8.0, 3292);

      StringAssert.Contains(PerfGc.Format(from, to, 20), $"stopped {2292:0} ms",
        "a window that lost two seconds to the collector has to say so even when no beat was late");
      Assert.AreEqual(2292, PerfGc.WindowPauseMs(from, to), 1, "the same figure stands on its own for attribution");
    }

    /* A window with no collection prints a zero rather than inheriting the lifetime total, which would accuse every window of everything. */
    [TestMethod]
    public void AWindowWithNoCollectionReportsNoStoppedTime()
    {
      var from = new PerfGc.Reading(0, 0, 0, 0, 0, 0, 8.0, 3292);
      var to = new PerfGc.Reading(1024, 0, 0, 0, 0, 0, 8.0, 3292);

      StringAssert.Contains(PerfGc.Format(from, to, 20), $"stopped {0:0} ms", "an innocent window should read as innocent");
      Assert.AreEqual(0, PerfGc.WindowPauseMs(to, from), 0, "a backwards pair is zero, not negative");
    }

    /* A window of zero seconds is reachable (two beats in the same millisecond) and must not become a divide-by-zero or infinity. */
    [TestMethod]
    public void AWindowOfZeroSecondsStillPrintsALine()
    {
      var reading = PerfGc.Sample();
      var line = PerfGc.Format(reading, reading, 0);

      StringAssert.Contains(line, $"alloc {(0.0):0.0} MB/s");
      Assert.IsFalse(line.Contains("Infinity") || line.Contains("NaN"), $"unmeasurable window produced {line}");
    }

    /*
     * The register a blocking collection writes to, so the watchdog can name ours without catching it in a counter. It cannot: the runtime publishes a
     * collection's count and pause after the other threads are running again, which is how a `gc.tidy` that stopped the world for 794 ms sat beside a
     * STOP-THE-WORLD line blaming a profiler at the same millisecond. Whatever asked for the collection knows, so it writes that down here - readable while
     * the collection runs, since that is exactly when a gap gets classified around it.
     */
    [TestMethod]
    public void AStopWeAskForIsReadableWhileItRuns()
    {
      var now = Environment.TickCount64;

      PerfGc.NoteStopStart("register test");
      var inFlight = PerfGc.ReadStop();

      Assert.AreEqual("register test", inFlight.Reason, "the reason rides along, because 'log loaded' explains a freeze in a way 'gen2' does not");
      Assert.IsFalse(inFlight.Finished, "a collection still running has no duration yet and must not claim one");
      Assert.IsTrue(inFlight.Overlaps(now - 5000, now), "a collection running now overlaps a gap that ends now");

      PerfGc.NoteStopEnd();
      var done = PerfGc.ReadStop();

      Assert.IsTrue(done.Finished, "closing the note is what gives it a duration");
      Assert.IsTrue(done.WallMs >= 0 && done.WallMs < 60_000, $"a start-and-stop pair measures something small, not {done.WallMs} ms");
      Assert.IsFalse(done.Overlaps(now + 100_000, now + 200_000), "a stop in the past must not explain a gap in the future");
    }

    /* An empty register explains nothing at any width: an ordinary gap has to keep the attribution the counters support. */
    [TestMethod]
    public void AStopNobodyAskedForExplainsNothing()
    {
      var none = default(PerfGc.IntentionalStop);

      Assert.IsNull(none.Reason, "nothing noted reads as nothing noted");
      Assert.IsFalse(none.Overlaps(0, long.MaxValue), "an empty register must not match a gap of any width");
    }
  }
}
