using System;
using System.Runtime;

using EQLogParser;

namespace EQLogParser;

/*
 * BulkGcMode switches a PROCESS-GLOBAL collector policy for the duration of a bulk file read, so these tests are about
 * ownership rather than about garbage collection: who sets it, who is allowed to clear it, and what happens on the paths
 * where nobody is holding it any more. The mode itself (Batch minimises total GC time; SustainedLowLatency keeps memory
 * longer to avoid stutter) is runtime behaviour and is measured in the field, not asserted here — docs/DesignNotes.md →
 * "Parallel loading: measured, shipped, and backed out".
 *
 * Every test starts from Interactive and restores whatever the process actually had, because a leaked Batch mode would
 * otherwise be inherited by every later test in this process — which is precisely the bug this class must not have.
 */
[TestClass]
[DoNotParallelize]
public class BulkGcModeTest
{
  private const long Threshold = BulkGcMode.BulkReadThresholdBytes;

  private GCLatencyMode _original;

  [TestInitialize]
  public void Setup()
  {
    _original = GCSettings.LatencyMode;
    Assert.AreEqual(0, BulkGcMode.Depth, "a previous test left a bulk read holding the mode — nothing else ran in Interactive");
    GCSettings.LatencyMode = GCLatencyMode.Interactive;
  }

  [TestCleanup]
  public void Cleanup()
  {
    Assert.AreEqual(0, BulkGcMode.Depth, "this test did not release every scope it entered");
    GCSettings.LatencyMode = _original;
  }

  // The override that exists for the Server-GC A/B rewrites the size rule, so threshold assertions only mean something
  // when a player's environment is in play (unset).
  private static bool SizeRuleActive => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EQLP_BULKGC"));

  [TestMethod]
  public void TheSizeRuleIsTheThresholdAndNothingElse()
  {
    if (!SizeRuleActive)
    {
      Assert.Inconclusive("EQLP_BULKGC is set, which overrides the size rule by design");
    }

    Assert.IsFalse(BulkGcMode.ShouldUseBatchMode(0),
      "a monitor open follows from end of file and reads nothing; a 1.2 GB capture on disk is not a bulk read");
    Assert.IsFalse(BulkGcMode.ShouldUseBatchMode(1024 * 1024), "a megabyte is not worth changing the process for");
    Assert.IsFalse(BulkGcMode.ShouldUseBatchMode(Threshold - 1), "the threshold is inclusive, so just under it stays off");
    Assert.IsTrue(BulkGcMode.ShouldUseBatchMode(Threshold));
    Assert.IsTrue(BulkGcMode.ShouldUseBatchMode(long.MaxValue));
  }

  [TestMethod]
  public void TheLastReaderHoldingTheLoadIsTheOneThatLeavesIt()
  {
    if (!SizeRuleActive)
    {
      Assert.Inconclusive("EQLP_BULKGC is set, which overrides the size rule by design");
    }

    // Two readers can be alive over one process (an open plus a trigger reader over another character's file). The mode
    // is one slot, so the inner one releasing must NOT hand the process back to Interactive while the outer still streams.
    using (BulkGcMode.EnterForBulkRead(Threshold))
    {
      Assert.AreEqual(GCLatencyMode.Batch, GCSettings.LatencyMode, "a bulk read did not ask for throughput mode");

      var outer = BulkGcMode.EnterForBulkRead(Threshold);
      var inner = BulkGcMode.EnterForBulkRead(Threshold);
      Assert.AreEqual(3, BulkGcMode.Depth);

      inner.Dispose();
      Assert.AreEqual(GCLatencyMode.Batch, GCSettings.LatencyMode,
        "a nested read finishing cleared a mode another load is still relying on");

      outer.Dispose();
      Assert.AreEqual(GCLatencyMode.Batch, GCSettings.LatencyMode);

      // (the using above is the third holder; nothing may restore before it goes)
      Assert.AreEqual(1, BulkGcMode.Depth);
    }

    Assert.AreEqual(GCLatencyMode.Interactive, GCSettings.LatencyMode, "the last holder did not restore the mode it found");
    Assert.AreEqual(0, BulkGcMode.Depth);
  }

  [TestMethod]
  public void DoubleDisposeOfOneReaderDoesNotDropAnotherS()
  {
    var a = BulkGcMode.EnterForBulkRead(Threshold);   // forced or size-passing; both hold if it returned a real scope
    var modeAfter = GCSettings.LatencyMode;
    var b = BulkGcMode.EnterForBulkRead(Threshold);

    a.Dispose();
    a.Dispose();  // must be inert: the second call cannot release a load that is not its own

    Assert.AreEqual(modeAfter, GCSettings.LatencyMode, "a double dispose changed the mode out from under the other reader");
    b.Dispose();
    Assert.AreEqual(GCLatencyMode.Interactive, GCSettings.LatencyMode);
  }

  [TestMethod]
  public void AnAbandonedReadStillGivesTheModeBack()
  {
    try
    {
      using (BulkGcMode.EnterForBulkRead(Threshold))
      {
        throw new InvalidOperationException("a cancel, a closed window, a failed line — any unwind from the read loop");
      }
    }
    catch (InvalidOperationException)
    {
      // the point of the test is what the unwind did to the mode
    }

    Assert.AreEqual(GCLatencyMode.Interactive, GCSettings.LatencyMode,
      "a load that ended by exception left the whole process in throughput mode");
  }

  [TestMethod]
  public void AnEarlierLowLatencyPromiseIsKeptRatherThanOverwritten()
  {
    // The restore returns what it FOUND, not a hardcoded Interactive: if some other part of the app (or the host) had
    // promised low latency, silently downgrading it to Interactive after a load would be its own bug.
    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

    using (BulkGcMode.EnterForBulkRead(Threshold))
    {
      Assert.AreEqual(GCLatencyMode.Batch, GCSettings.LatencyMode);
    }

    Assert.AreEqual(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
  }

  [TestMethod]
  public void AReadTooSmallToSwitchCostsNothingAndHoldsNothing()
  {
    if (!SizeRuleActive)
    {
      Assert.Inconclusive("EQLP_BULKGC is set, which overrides the size rule by design");
    }

    using (BulkGcMode.EnterForBulkRead(64 * 1024))
    {
      Assert.AreEqual(GCLatencyMode.Interactive, GCSettings.LatencyMode, "a small read changed a process-wide policy");
      Assert.AreEqual(0, BulkGcMode.Depth, "a read that did not switch still counted as holding the mode");
    }
  }
}
