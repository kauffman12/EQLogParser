using System.Reflection;

using EQLogParser;

namespace EQLogParser;

/*
 * How many lines a reader handed to the parser is asked by PRODUCT code after a load finishes, never by the perf journal:
 * LogReader.LoadAllocatedGarbage(count) decides the load-complete log line, whether the fight list's loading band may show over an
 * empty-looking grid, and whether the collector is asked for memory at all (docs/DesignNotes.md → "The slots a finished load stopped
 * writing"). It used to be incremented under the `PerfJournal.Enabled` guard belonging to the once-per-two-seconds diagnostic line next
 * to it, so a normal session — one whose settings.txt says nothing about PerfReport — reported "the read handed nothing over" after
 * reading a whole capture: no band over an empty grid, a wrong log line, no GC pass. A perf flag may gate what a load PRINTS and never
 * what a load DID.
 *
 * THIS IS IN THE WINDOWS-ONLY ASSEMBLY because LogReader compiles into the WPF app (EQLogParser/src/control/util) and the plain
 * assembly references Core only — the same boundary DerivedSnapshotCostTest/UiBeatMonitor live behind. It is driven through the private
 * seam rather than over a file on purpose: a real reader links a LogProcessor, closes the static handoff queue when it finishes and
 * would leave the next test in the process parsing into a disposed collection, while nothing about this law needs a file at all.
 */
[TestClass]
public sealed class LoadHandoffCountTest
{
  private static readonly MethodInfo NoteLoadProgress =
    typeof(LogReader).GetMethod("NoteLoadProgress", BindingFlags.NonPublic | BindingFlags.Instance)!;

  [TestMethod]
  public void TheHandedOverCountAccruesWithPerfReportingOff()
  {
    var perfWasOn = PerfJournal.Enabled;
    PerfJournal.Enabled = false;
    try
    {
      var reader = new LogReader(null!, "some-capture.log", "test", -1);
      Assert.AreEqual(0L, reader.HandedOverLines, "a reader that handed nothing over says so");

      NoteLoadProgress.Invoke(reader, [5000]);
      NoteLoadProgress.Invoke(reader, [3000]);

      Assert.AreEqual(8000L, reader.HandedOverLines,
        "the count is what the load-complete question reads, so it accrues whether or not anyone is logging");
      Assert.IsTrue(LogReader.LoadAllocatedGarbage(reader.HandedOverLines),
        "a read that handed lines over is a read that allocated garbage worth tidying");
    }
    finally
    {
      PerfJournal.Enabled = perfWasOn;
    }
  }

  [TestMethod]
  public void PerfReportingOffStillWritesNoLoadLine()
  {
    var perfWasOn = PerfJournal.Enabled;
    PerfJournal.Enabled = false;
    try
    {
      var reader = new LogReader(null!, "some-capture.log", "test", -1);

      NoteLoadProgress.Invoke(reader, [5000]);

      /*
       * The diagnostic line advances its own window marks (_handedOverLast/_diagSeconds) when it prints; both staying at zero is the
       * observable half of "the PRINT stayed gated" while the count above it moved. Without that guard a player's raid log gains one
       * load line every two seconds, which is the reason the guard exists.
       */
      var last = (long)typeof(LogReader).GetField("_handedOverLast", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
      var seconds = (double)typeof(LogReader).GetField("_diagSeconds", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;

      Assert.AreEqual(0L, last, "no diagnostic line may print with PerfJournal off");
      Assert.AreEqual(0d, seconds, "no diagnostic window may open with PerfJournal off");
    }
    finally
    {
      PerfJournal.Enabled = perfWasOn;
    }
  }

  [TestMethod]
  public void AnEmptyBatchChangesNothing()
  {
    var perfWasOn = PerfJournal.Enabled;
    PerfJournal.Enabled = false;
    try
    {
      var reader = new LogReader(null!, "some-capture.log", "test", -1);

      NoteLoadProgress.Invoke(reader, [0]);

      Assert.AreEqual(0L, reader.HandedOverLines, "a flush that handed nothing over must not move the count either way");
    }
    finally
    {
      PerfJournal.Enabled = perfWasOn;
    }
  }
}
