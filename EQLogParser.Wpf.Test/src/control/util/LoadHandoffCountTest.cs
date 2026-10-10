using EQLogParser;

namespace EQLogParser;

/*
 * What a load reports about itself is PRODUCT code, not diagnostics. `HandedOverLines` decides the load-complete log line, whether the
 * fight list's loading band may show over an empty-looking grid, and whether the collector is asked for memory at all (docs/DesignNotes.md
 * → "The slots a finished load stopped writing"): a follow-from-end open reaches 100 % having read no line, and must therefore ask for
 * nothing — no band claiming a build that is not owed, no blocking compaction over an empty session.
 *
 * The reason this file exists at all: the count used to be incremented behind the same `PerfJournal.Enabled` guard as a once-per-two-seconds
 * progress line printed next to it, so a normal session reported "the read handed nothing over" after reading a whole capture. A switch about
 * what a load PRINTS may never gate what a load DID. The printing half is gone — the perf journal and its load line went with it — and what
 * stays pinned here is the half still standing: the count and the two questions product code asks of it.
 *
 * THIS IS IN THE WINDOWS-ONLY ASSEMBLY because LogReader compiles into the WPF app (EQLogParser/src/control/util) while the plain assembly
 * references Core only — the same boundary DerivedSnapshotCostTest lives behind.
 */
[TestClass]
public sealed class LoadHandoffCountTest
{
  [TestMethod]
  public void AReaderThatHandedNothingOverSaysSo()
  {
    var reader = new LogReader(null!, "some-capture.log", "test", -1);

    Assert.AreEqual(0L, reader.HandedOverLines, "a reader that handed nothing over says so");
    Assert.IsFalse(LogReader.LoadAllocatedGarbage(reader.HandedOverLines),
      "a read that captured no lines made no garbage, so no tidy is owed at the end of it");
  }

  [TestMethod]
  public void AReadThatHandedLinesOverIsOneThatAllocatedGarbage()
  {
    Assert.IsTrue(LogReader.LoadAllocatedGarbage(5000),
      "lines that crossed into the parse lane are lines whose text the collector has to be asked for");
  }
}
