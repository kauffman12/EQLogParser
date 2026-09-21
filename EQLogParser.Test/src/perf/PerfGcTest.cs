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
      var from = new PerfGc.Reading(1000, 2048, 4096, 5, 2, 1, 3.0);
      var to = new PerfGc.Reading(9000, 2048, 4096, 8, 2, 1, 7.5);

      var line = PerfGc.Format(from, to, 10);
      StringAssert.Contains(line, "gc0 3");
      StringAssert.Contains(line, "gc1 0");
      StringAssert.Contains(line, "gc2 0");
    }

    /* Eight megabytes over ten seconds is 0.8 MB/s: the rate is the number that predicts the next collection, not the total. */
    [TestMethod]
    public void AllocationIsPrintedAsARate()
    {
      var from = new PerfGc.Reading(0, 0, 0, 0, 0, 0, 0);
      var to = new PerfGc.Reading(8L * 1024 * 1024, 0, 0, 0, 0, 0, 0);

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
      var from = new PerfGc.Reading(50L * 1024 * 1024, 0, 0, 0, 0, 0, 0);
      var to = new PerfGc.Reading(1024, 0, 0, 0, 0, 0, 0);

      Assert.IsFalse(PerfGc.Format(from, to, 10).Contains("-"), "no part of a heartbeat may read as negative bytes");
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
  }
}
