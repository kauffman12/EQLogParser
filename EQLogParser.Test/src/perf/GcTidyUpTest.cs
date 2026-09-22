using System.Diagnostics;
using System.Runtime;
using System.Threading;

namespace EQLogParser
{
  /*
   * Asking for a collection at chosen moments, and the two rules that keep that from becoming the problem it was added to solve.
   *
   * Forcing collections is normally bad advice, and the reason is worth restating because it is what these tests protect: a request must
   * never run on the thread that noticed the moment (the collector stops every thread, so a caller that waits takes its own UI thread down
   * with it), and it must not happen six times because the player fiddled with a filter (every forced collection promotes young survivors,
   * so a tidy storm makes the heap bigger and the next unasked-for collection longer). Hence a settle delay onto a pool thread, an interval,
   * and counters that let both be asserted instead of eyeballed in a log.
   */
  [TestClass]
  public sealed class GcTidyUpTest
  {
    private const int WaitBudgetMs = 5000;

    [TestInitialize]
    public void ClearTheCounters() => GcTidyUp.Reset();

    /* The tidy reports itself in one line, in the units a reader of the application log needs: what the heap and working set were, and what it cost. */
    [TestMethod]
    public void ATidySaysWhatItDid()
    {
      var line = GcTidyUp.TidyNow("unit test");

      StringAssert.StartsWith(line, "gc.tidy unit test");
      StringAssert.Contains(line, "heap ");
      StringAssert.Contains(line, "working set ");
      StringAssert.Contains(line, "gen2 +");
      StringAssert.Contains(line, "stopped ");
      Assert.IsFalse(line.Contains("NaN") || line.Contains("Infinity"), $"a tidy line full of arithmetic trouble: {line}");
    }

    /*
     * The request really does force a full collection rather than nudging the runtime: gen2 has to move because we asked, and the line has to
     * admit how long every thread stood still while it happened. Both are assertable in any process; "and here are the bytes back" is not -
     * HeapSizeBytes is dominated by whatever else a test run is holding (a suite reached 4.8 GB, and 100 MB of released arrays disappeared in
     * it), which is why the reclaim itself is left to the gc.tidy line in a player's log rather than claimed here. What it does return was
     * measured in a quiet process: 30 MB of heap becoming 5.
     */
    [TestMethod]
    public void AskingForACollectionActuallyForcesOne()
    {
      var junk = new List<byte[]>();

      for (var i = 0; i < 20; i++)
      {
        junk.Add(new byte[5 * 1024 * 1024]); // 100 MB on the large object heap, the fragmentation this pass exists to clear
      }

      var gen2 = GC.CollectionCount(2);
      junk.Clear();
      GC.KeepAlive(junk);

      var line = GcTidyUp.TidyNow("after junk");

      Assert.IsTrue(GC.CollectionCount(2) > gen2, "asking for a tidy has to produce a full collection, not a suggestion");
      StringAssert.Contains(line, "gen2 +1", $"the line should report the collection it caused: {line}");
    }

    /* A request returns at once and collects later on another thread: the caller is never the one waiting for the world to stop. */
    [TestMethod]
    public void ARequestDoesNotWaitForTheCollection()
    {
      var started = Stopwatch.StartNew();
      var accepted = GcTidyUp.Request("unit test", 250, 60_000);

      Assert.IsTrue(accepted, "the first request of a session should be taken");
      Assert.IsTrue(started.Elapsed.TotalMilliseconds < 100, $"the request blocked for {started.Elapsed.TotalMilliseconds:0} ms waiting on the collector");

      // The collection belongs to a pool thread now; let it finish so the counters mean what the next assertion takes them for.
      Assert.IsTrue(WaitFor(() => GcTidyUp.TidyCount == 1 && GcTidyUp.Idle), "the requested tidy never finished");
    }

    /*
     * Two triggers inside the same interval pay for one collection. This is the guard against a rebuild storm - changing the damage summary's
     * time filter five times is ordinary behaviour, and five forced full compactions would be worse than none.
     */
    [TestMethod]
    public void TheSecondRequestInsideTheIntervalIsSwallowed()
    {
      Assert.IsTrue(GcTidyUp.Request("first", 0, 60_000));
      Assert.IsTrue(WaitFor(() => GcTidyUp.TidyCount == 1 && GcTidyUp.Idle), "the first tidy never finished");

      Assert.IsFalse(GcTidyUp.Request("second", 0, 60_000), "a second tidy inside the interval should be refused");
      Assert.AreEqual(1, GcTidyUp.SuppressedCount, "what was swallowed is counted, not silently dropped");
      Assert.AreEqual(1, GcTidyUp.TidyCount, "the refusal must not still collect");
    }

    /* A request that arrives while another tidy is in flight is refused the same way, so two triggers in one millisecond cannot stack pauses. */
    [TestMethod]
    public void TwoTriggersAtOnceCollectOnce()
    {
      var accepted = 0;

      Parallel.For(0, 8, _ =>
      {
        if (GcTidyUp.Request("race", 50, 60_000))
        {
          Interlocked.Increment(ref accepted);
        }
      });

      Assert.AreEqual(1, accepted, $"eight simultaneous requests produced {accepted} collections");
      Assert.IsTrue(WaitFor(() => GcTidyUp.TidyCount == 1 && GcTidyUp.Idle), "the one accepted tidy never finished");
      Assert.AreEqual(7, GcTidyUp.SuppressedCount);
    }

    /* Once the interval has passed the tidies come back: the rate limit is a throttle, not a one-shot. */
    [TestMethod]
    public void TidiesResumeAfterTheInterval()
    {
      Assert.IsTrue(GcTidyUp.Request("one", 0, 0));
      Assert.IsTrue(WaitFor(() => GcTidyUp.TidyCount == 1 && GcTidyUp.Idle));
      Assert.IsTrue(GcTidyUp.Request("two", 0, 0), "an expired interval should accept again");
      Assert.IsTrue(WaitFor(() => GcTidyUp.TidyCount == 2 && GcTidyUp.Idle));
      Assert.AreEqual(0, GcTidyUp.SuppressedCount);
    }

    /*
     * CompactOnce is a flag the runtime clears for you, and it has to be asked for every time rather than left set: permanently compacting
     * would slow down the collections the runtime chooses on its own, which is the opposite of what this class is for.
     */
    [TestMethod]
    public void CompactionIsNotLeftSwitchedOn()
    {
      Assert.AreEqual(GCLargeObjectHeapCompactionMode.Default, GCSettings.LargeObjectHeapCompactionMode, "before");

      GcTidyUp.TidyNow("flag check");

      Assert.AreEqual(GCLargeObjectHeapCompactionMode.Default, GCSettings.LargeObjectHeapCompactionMode,
        "a tidy that leaves the runtime compacting every future collection is a tax nobody agreed to");
    }

    private static bool WaitFor(Func<bool> condition)
    {
      var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * WaitBudgetMs / 1000;

      while (!condition())
      {
        if (Stopwatch.GetTimestamp() > deadline)
        {
          return false;
        }

        Thread.Sleep(10);
      }

      return true;
    }
  }
}
