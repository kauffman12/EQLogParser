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
   *
   * The waits here are deliberately awkward, because two failures used to read as one. A single wait on `TidyCount == n && Idle` cannot tell "the collection is
   * slow" from "it finished and never handed the slot back", and the first is this machine compacting whatever the rest of the suite left allocated - five
   * seconds was measured insufficient on Windows, on exactly the two tests that throttle requests. So each half waits and fails separately, quoting the counts,
   * whether the slot is claimed and how big the heap was: the numbers that decide whether to look at this file or at the machine.
   */
  [TestClass]
  public sealed class GcTidyUpTest
  {
    /*
     * Generous on purpose. These tests force a full compacting collection in a test host whose heap is whatever the suite has been parsing, and that is the
     * slowest thing in the run: five seconds timed out on a Windows box on the two request-throttle tests while passing here. A budget is not an assertion - a
     * tidy that needs 15 s still passes, one that never releases its slot fails, and the message says which.
     */
    private const int WaitBudgetMs = 30_000;

    [TestInitialize]
    public void ClearTheCounters() => GcTidyUp.Reset();

    /* The tidy reports itself in one line, in the units a reader of the application log needs: what the heap and working set were, and what it cost. */
    [TestMethod]
    public void ATidySaysWhatItDid()
    {
      var line = GcTidyUp.TidyNow("unit test");

      StringAssert.StartsWith(line, "gc.tidy unit test");

      /* The tidy also has to leave itself on the register: a gap classified while it runs cannot see it in the GC counters, and would blame the machine. */
      var stop = PerfGc.ReadStop();
      Assert.AreEqual("unit test", stop.Reason, $"the collection that just ran should be nameable as ours (register said '{stop.Reason}')");
      Assert.IsTrue(stop.Finished, "a finished tidy carries a duration so the gap line can quote how much of itself it was");
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
      WaitForCollection(1);
      WaitForIdle();
    }

    /*
     * Two triggers inside the same interval pay for one collection. This is the guard against a rebuild storm - changing the damage summary's
     * time filter five times is ordinary behaviour, and five forced full compactions would be worse than none.
     */
    [TestMethod]
    public void TheSecondRequestInsideTheIntervalIsSwallowed()
    {
      Assert.IsTrue(GcTidyUp.Request("first", 0, 60_000));
      WaitForCollection(1);
      WaitForIdle();

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
      WaitForCollection(1);
      WaitForIdle();
      Assert.AreEqual(7, GcTidyUp.SuppressedCount);
    }

    /*
     * A rebuild stream must not collect inside itself. The damage window's compute timer is restarted by incoming data, so while a fight is being
     * written the three builders ask again every second or so, and a wait measured from the FIRST of them stops the world in the middle of the pull
     * that was still asking (a blocking full compact plus LOH compaction, once a minute, every fight). Requests therefore re-arm the wait: four asks
     * arrive 80 ms apart against a 300 ms settle, no collection may land while they are still coming, and exactly one lands afterwards.
     */
    [TestMethod]
    public void AStreamOfRequestsCollectsAfterTheStreamEnds()
    {
      Assert.IsTrue(GcTidyUp.Request("first of a stream", 300, 60_000));

      for (var i = 0; i < 4; i++)
      {
        Thread.Sleep(80);

        // Refused as a duplicate, and still a statement that the application is in the middle of something.
        GcTidyUp.Request($"rebuild {i}", 300, 60_000);
        Assert.AreEqual(0, GcTidyUp.TidyCount, $"the tidy landed while rebuilds were still arriving (request {i + 1}, {GcTidyUp.TidyCount} collections)");
      }

      WaitForCollection(1);
      WaitForIdle();

      Assert.AreEqual(1, GcTidyUp.TidyCount, "a stream of five asks pays for one collection, not five");
      Assert.AreEqual(4, GcTidyUp.SuppressedCount, "the re-arming asks are counted as swallowed, since none of them got a collection of its own");
    }

    /* Once the interval has passed the tidies come back: the rate limit is a throttle, not a one-shot. */
    [TestMethod]
    public void TidiesResumeAfterTheInterval()
    {
      Assert.IsTrue(GcTidyUp.Request("one", 0, 0));
      WaitForCollection(1);
      WaitForIdle();
      Assert.IsTrue(GcTidyUp.Request("two", 0, 0), "an expired interval should accept again");
      WaitForCollection(2);
      WaitForIdle();
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

    /*
     * A request whose moment got reset away must not collect later. This is the ghost that made this class order-dependent: a stats builder finishing its work
     * asks for a tidy 1.5 s hence, the next test wipes the counters and takes the slot, and the stale request turns up mid-assertion to stop the world on behalf
     * of state that no longer exists - it can even take the slot back. Asked for, reset during its settle delay, then given every chance to run: no collection
     * may arrive from it, while the request made after the reset goes ahead normally.
     */
    [TestMethod]
    public void ARequestStaleByTheTimeItWakesDoesNotCollect()
    {
      Assert.IsTrue(GcTidyUp.Request("stale", 150, 60_000));

      GcTidyUp.Reset();

      Assert.IsTrue(GcTidyUp.Request("current", 0, 60_000), "a reset hands the slot to whoever asks next, not to the request that was settling");
      WaitForCollection(1);
      WaitForIdle();

      Thread.Sleep(400);

      Assert.AreEqual(1, GcTidyUp.TidyCount, $"a request retired by Reset collected on its own schedule ({GcTidyUp.TidyCount} collections)");
    }

    /* "The collection we asked for never happened." Counted as reached once the expected number have run, so a straggler cannot make the count walk past it. */
    private static void WaitForCollection(int expected)
    {
      Assert.IsTrue(WaitUntil(() => GcTidyUp.TidyCount >= expected),
        $"no collection after {WaitBudgetMs} ms: count {GcTidyUp.TidyCount} of {expected}, suppressed {GcTidyUp.SuppressedCount}, " +
        $"slot idle {GcTidyUp.Idle}, heap {PerfGc.Sample().HeapBytes / (1024.0 * 1024):0} MB - a big heap means this machine is slow, not this code");
    }

    /* "It happened and never handed the slot back", which is the bug: every later request would be refused forever by a tidy that stopped answering. */
    private static void WaitForIdle()
    {
      Assert.IsTrue(WaitUntil(() => GcTidyUp.Idle),
        $"a tidy is still holding the slot after {WaitBudgetMs} ms, so every request after it is refused forever: " +
        $"{GcTidyUp.TidyCount} collected, heap {PerfGc.Sample().HeapBytes / (1024.0 * 1024):0} MB");
    }

    private static bool WaitUntil(Func<bool> condition)
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
