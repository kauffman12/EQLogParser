using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * Which 100 % gets a forced collection, and which gets left alone.
 *
 * `GcTidyUp.Request("log loaded")` fires when the reader pump reports progress complete — and two completely different
 * states report that. An open that read a file allocated gigabytes of short-lived text (the measured 11.2 s stopped
 * across the first minute of a replay soak, heap 624 → 2,997 MB, lives in docs/DesignNotes.md), so asking the
 * collector for it back at EOF is the cheapest tidy in the app. An open that FOLLOWED from end of file — the startup
 * auto-monitor, and Clear All's re-open — reaches the same 100 % having handed over zero lines: no parse ran, so a
 * blocking compacting pass would stop every thread to reclaim startup allocations it never made.
 *
 * Both directions are pinned because the default reading of "EOF happened" would collect in both, and the failure is
 * invisible: nobody notices a stop-the-world they were not waiting for except as a hitch at startup.
 *
 * No WPF is constructed — the predicate is a static on LogReader (it lives there because the question is about what a
 * reader did), which also means no Sta.Run and no thread-affinity trap in this file.
 */
[TestClass]
public sealed class LoadTidyTriggerTest
{
  [TestMethod]
  public void AnOpenThatReadAFileAsksForItsGarbageBack()
  {
    Assert.IsTrue(LogReader.LoadAllocatedGarbage(1), "one line read is parse churn, however small the file");
    Assert.IsTrue(LogReader.LoadAllocatedGarbage(4_707_447), "the reference capture's load — the case the tidy exists for");
  }

  [TestMethod]
  public void AnOpenThatFollowedFromEndOfFilesAsksForNothing()
  {
    Assert.IsFalse(LogReader.LoadAllocatedGarbage(0), "monitoring from end of file read nothing and allocated no parse churn");
    Assert.IsFalse(LogReader.LoadAllocatedGarbage(-1), "a reader that never started is not a load either");
  }
}
