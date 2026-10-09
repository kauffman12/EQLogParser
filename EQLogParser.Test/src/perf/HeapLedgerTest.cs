using EQLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test.src.perf;

/*
 * The heap ledger is the answer to "~330 MB of a 419 MB heap is not rows" — a sentence that could only be INFERRED from a dotnet-gcdump
 * before it existed. Its whole job is to print the parts of a session's memory so a reader does not have to guess which part grew, so what
 * gets asserted here is that the parts are ALL there and say what the numbers mean.
 *
 * It lives in Core and these tests run here for the reason PerfGap's do: an app-side wording test sits in the Windows-only assembly, which
 * builds everywhere but executes on one machine — the exact shape that let a mismatched format string ship against an assertion nobody ran.
 */
[TestClass]
public class HeapLedgerTest
{
  [TestCleanup]
  public void Cleanup() => HeapLedger.Reset();

  [TestMethod]
  public void TheLineCarriesEveryPartTheQuestionNeeds()
  {
    var s = new HeapLedger.LedgerStats(Seconds: 30, WorkingSetBytes: 419_200_000, HeapBytes: 354_400_000,
      PauseMs: 12.5, Gen0: 4, Gen1: 1, Gen2: 0,
      FactRows: 2_286_368, FactSlackBytes: 29_200_000, HealRows: 1_247_984, HealSlackBytes: 11_300_000,
      Names: 2_436, EstimatedBytes: 359_000_000);

    var line = HeapLedger.Format(s);

    // The opening word is what a grep asks for.
    Assert.IsTrue(line.StartsWith("heap:", StringComparison.Ordinal), line);
    Assert.IsFalse(line.Contains("-", StringComparison.Ordinal), "no negative belongs in a trend line: \u201cmemory was returned\u201d is not what a delta means");

    StringAssert.Contains(line, "ws=399.8 MB", line);
    StringAssert.Contains(line, "heap=338.0 MB", line);
    StringAssert.Contains(line, "pause 12.5 ms", line);

    // Generation counts are deltas over the printed window, printed as gen0/gen1/gen2.
    StringAssert.Contains(line, "gc 4/1/0", line);

    // Rows AND slack: "the capture is big" and "the capture reserved more than it wrote" have to stay two separate sentences,
    // because they have two different fixes (capture size vs the once-per-doubling trim).
    StringAssert.Contains(line, "facts rows=2,286,368", line);
    StringAssert.Contains(line, "slack=27.8 MB", line);
    StringAssert.Contains(line, "heals rows=1,247,984", line);

    StringAssert.Contains(line, "names=2,436", line);
    StringAssert.Contains(line, "row arrays est=342.4 MB", line);
    StringAssert.Contains(line, "over 30s", line);
  }

  [TestMethod]
  public void NegativeDeltasReadZeroRatherThanAMissingNumber()
  {
    // Two reads crossing on different threads can hand back a smaller counter than the baseline. A negative in a trend line is worse than
    // zero: it looks like memory was returned.
    var s = new HeapLedger.LedgerStats(30, 1_024, 1_024, -5, -3, -1, -1, 10, -4_096, 5, -1, 2, 1_024);
    var line = HeapLedger.Format(s);

    Assert.IsFalse(line.Contains("-", StringComparison.Ordinal), line);
    Assert.IsTrue(line.Contains("pause 0.0 ms", StringComparison.Ordinal), line);
    Assert.IsTrue(line.Contains("gc 0/0/0", StringComparison.Ordinal), line);
    Assert.IsTrue(line.Contains("over 0s", StringComparison.Ordinal) || line.Contains("over 30s", StringComparison.Ordinal), line);
  }

  /*
   * "Not rows" is a subtraction, and a subtraction is not an answer. The first Windows field run printed `heap=655.8 MB`
   * beside `row arrays est=183.4 MB` — ~472 MB held somewhere the line could not name — so the ledger now prints the two
   * retained stores on that side of it: the cast history and the timed records, both of which grow with the capture.
   * They ride on EVERY line, zeros included, because a grep for `casts=` has to work on the whole run, and a term that is
   * absent half the time cannot be trended.
   */
  [TestMethod]
  public void TheKeptRecordsRideTheLineSoNotRowsBecomesASubtraction()
  {
    var s = new HeapLedger.LedgerStats(30, 655_800_000, 655_800_000, 0, 0, 0, 0,
      FactRows: 4_832_103, FactSlackBytes: 0, HealRows: 2_670_809, HealSlackBytes: 0,
      Names: 410, EstimatedBytes: 192_721_000, CastEntries: 164_263, TimedRecords: 656_686);

    var line = HeapLedger.Format(s);
    StringAssert.Contains(line, "kept casts=164,263", line);
    StringAssert.Contains(line, "timed records=656,686", line);

    var bare = HeapLedger.Format(new HeapLedger.LedgerStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
    StringAssert.Contains(bare, "kept casts=0 timed records=0", bare);
  }

  [TestMethod]
  public void TheFirstSampleOfACaptureIsDueRightAwayAndEveryAskAfterItWaitsTheInterval()
  {
    // A test that sleeps thirty seconds proves nothing about the rule, so the rule is arithmetic and asked directly.
    Assert.IsTrue(HeapLedger.Due(nowMs: 1_000, lastMs: 999_999, havePrevious: false),
      "the sizes are worth a line as soon as a load settles, deltas or not");

    var intervalMs = (long)(HeapLedger.IntervalSeconds * 1000);
    Assert.IsFalse(HeapLedger.Due(nowMs: 60_000, lastMs: 60_000 + 1, havePrevious: true), "one second after a line is not another line");
    Assert.IsFalse(HeapLedger.Due(60_000 + intervalMs - 1, 60_000, havePrevious: true));
    Assert.IsTrue(HeapLedger.Due(60_000 + intervalMs, 60_000, havePrevious: true));

    // The interval is expressed in SECONDS and multiplied out here rather than counted in pump ticks: the same twelve assertions hold
    // whether the pump runs at 100 ms or 1 s, which is the law (docs: "The Rule Does Not Depend On How Often It Is Asked"). There is no
    // assertion ON the constant itself — the analyzer correctly refuses a comparison of two literals, and the four boundary cases above are
    // what tie Due() to it. A cadence fast enough to bury the stall lines these are read beside would show up as those assertions moving.
  }

  [TestMethod]
  public void ASessionWithoutATablesAnswersZerosRatherThanNoLineAtAll()
  {
    // A capture that has handed over nothing yet still gets its sizes printed: zeros say "nothing captured", while a missing line cannot be
    // told apart from "the ledger never ran" — which is the diagnosis this class exists to make possible.
    var reading = default(PerfGc.Reading);
    var s = HeapLedger.Collect(null, reading, reading, 0);

    Assert.AreEqual(0, s.FactRows);
    Assert.AreEqual(0, s.HealRows);
    Assert.AreEqual(0, s.Names);
    Assert.AreEqual(0, s.EstimatedBytes);
    Assert.IsTrue(HeapLedger.Format(s).Contains("facts rows=0", StringComparison.Ordinal));
  }

  [TestMethod]
  public void TurningTheJournalOffPutsNoLineOutAndAsksForNothing()
  {
    var before = HeapLedger.LineCount;
    PerfJournal.Enabled = false;
    try
    {
      // Twice, and both must be no-ops: the ledger rides a 100 ms pump, so "returns immediately when off" is the entire cost story.
      HeapLedger.MaybeLog(null);
      HeapLedger.MaybeLog(null);
      Assert.AreEqual(before, HeapLedger.LineCount, "a diagnostics line that prints in normal play is a bug, not a feature");
    }
    finally
    {
      PerfJournal.Enabled = false;
    }
  }

  [TestMethod]
  public void ALineIsCountedSoSilenceCanBeToldApartFromQuiet()
  {
    var wasEnabled = PerfJournal.Enabled;
    var before = HeapLedger.LineCount;
    PerfJournal.Enabled = true;
    HeapLedger.Reset();
    try
    {
      HeapLedger.MaybeLog(null);   // first ask of a capture: due immediately
      Assert.AreEqual(before + 1, HeapLedger.LineCount, "the sizes should be printed without waiting out the interval");

      HeapLedger.MaybeLog(null);   // same instant: not due again
      Assert.AreEqual(before + 1, HeapLedger.LineCount);
    }
    finally
    {
      PerfJournal.Enabled = wasEnabled;
      HeapLedger.Reset();
    }
  }
}
