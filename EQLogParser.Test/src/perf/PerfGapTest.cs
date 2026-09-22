using System;
using System.Text.RegularExpressions;

namespace EQLogParser
{
  /*
   * Who gets blamed for a gap in which nothing ran. These sentences are the product: a line reading "collector (gen2 …)" sends the reader after
   * memory pressure, one reading "no collection in that gap" sends them after a profiler, a suspended process or a starved machine, and one reading
   * "3 collections account for only 120 of the 3000 ms" tells them the interesting part is still unnamed. Guessing wrong costs an afternoon, so the
   * wording is asserted rather than trusted.
   *
   * This file used to be five tests inside the Windows-only UI test assembly, next to the watchdog that calls the classifier. That is how
   * "3 collection(s) account for" shipped against an assertion wanting "3 collections": the classifier was app-side code, its only tests sat in an
   * assembly that cannot run on the machine that changed it, and "the suite passes" described a different project. The classifier is arithmetic and
   * string building with nothing WPF in it, so it lives in Core now and these tests run everywhere. Only the beat loop, the dispatcher hand-off and
   * the thread probe need Windows.
   */
  [TestClass]
  public sealed class PerfGapTest
  {
    /* A gap the pause counter covers is the collector's, and the generation has to be named: gen2 is memory pressure, gen0 is traffic. */
    [TestMethod]
    public void AGapTheCollectorOwnsIsBlamedOnTheCollector()
    {
      var why = PerfGap.Classify(2292, 2200, 1, 0, 1);

      StringAssert.Contains(why, "collector", "a gap the pause counter covers belongs to the collector");
      StringAssert.Contains(why, "gen2", "the full collection is the one worth naming; gen0 traffic would read differently");
      StringAssert.Contains(why, "2200 of the 2292 ms", "how much was accounted for, stated beside how much there was to account for");
    }

    /* A gen0-only pause must not read as a full collection: it sends the reader looking for a memory problem that is not there. */
    [TestMethod]
    public void AYoungGenerationPauseSaysSo()
    {
      var why = PerfGap.Classify(600, 580, 4, 0, 0);

      StringAssert.Contains(why, "gen0", "a gap explained by young collections should not be dressed up as a full one");
      Assert.IsFalse(why.Contains("gen2", StringComparison.Ordinal), $"naming gen2 here invents a compaction that did not happen (actual: {why})");
    }

    /* gen1 is the case a reader would otherwise misread as either of the others, so it gets named for itself. */
    [TestMethod]
    public void AMidGenerationPauseNamesItsGeneration()
    {
      var why = PerfGap.Classify(600, 580, 0, 3, 0);

      StringAssert.Contains(why, "gen1", "the generation that paused the process is the one worth printing");
      Assert.IsFalse(why.Contains("gen2", StringComparison.Ordinal), $"gen1 traffic must not borrow gen2's alarm (actual: {why})");
    }

    /* Collections happened but do not explain the wait: say both, because the remainder is the part still looking for a name. */
    [TestMethod]
    public void CollectionsThatDoNotCoverTheGapSaySo()
    {
      var why = PerfGap.Classify(3000, 120, 2, 1, 0);

      StringAssert.Contains(why, "3 collections", "the collections that did happen are still worth counting");
      StringAssert.Contains(why, "3000", "and the gap they failed to explain has to be stated with them");
      StringAssert.Contains(why, "not the collector", "so the reader does not close the case on memory");
    }

    /* One collection reads as one. The shipped version of this line was "{n} collection(s)", a template left inside a sentence. */
    [TestMethod]
    public void OneCollectionReadsAsOne()
    {
      var why = PerfGap.Classify(900, 120, 1, 0, 0);

      StringAssert.Contains(why, "1 collection accounts", "singular subject, singular verb");
      Assert.IsFalse(why.Contains("(s)", StringComparison.Ordinal), $"a parenthetical pluralizer is the thing that broke the last test (actual: {why})");
    }

    /* No collection at all means something outside the runtime froze the process - a profiler or gcdump, power management, or no CPU. */
    [TestMethod]
    public void AGapWithNoCollectionIsNotBlamedOnTheCollector()
    {
      var why = PerfGap.Classify(1500, 0, 0, 0, 0);

      StringAssert.Contains(why, "no collection", "an innocent collector must be able to say it was innocent");
      StringAssert.Contains(why, "outside", "and point at what is left: a suspended process, sleep, or starvation");
    }

    /*
     * Half the gap is where the blaming stops. It is a judgement rather than a measurement, and it cuts one way on purpose: at 40% attributed,
     * naming the collector would send a reader after memory while something else held the process, so the remainder keeps the sentence. The
     * boundary itself is pinned so a later tweak of the ratio has to be a decision, not a slip of the decimal point.
     */
    [TestMethod]
    public void HalfTheGapIsWhereItStopsBlamingTheCollector()
    {
      StringAssert.Contains(PerfGap.Classify(1000, 500, 1, 0, 0), "held every thread", "exactly half is enough to call it the collector's");
      StringAssert.Contains(PerfGap.Classify(1000, 499, 1, 0, 0), "accounts for only", "just under half is not, and the remainder has to be named");
    }

    /*
     * The counters are read on a pool thread from several places, so a delta can come back negative when two reads cross. A line that prints
     * "-42 ms" costs the reader time on arithmetic instead of on the freeze, so every number is floored.
     */
    [TestMethod]
    public void BackwardsCounterReadsDoNotPrintANegative()
    {
      var why = PerfGap.Classify(900, -42, -1, -1, -1);

      Assert.IsFalse(why.Contains("-"), $"attribution built from backwards reads should clamp to zero (actual: {why})");
    }

    /* The floor has to hold on the branch that keeps a hyphen as a separator too, or a negative pause would read as "-42 of the 900 ms". */
    [TestMethod]
    public void APartialPauseIsFlooredWhereTheSentenceHasADash()
    {
      var why = PerfGap.Classify(900, -42, 1, 0, 0);

      StringAssert.Contains(why, "for only 0 of the 900 ms", "a backwards pause read prints as nothing accounted for, not as a negative");
      StringAssert.Contains(why, "1 collection accounts", "while still counting the collection that did happen");
    }

    /*
     * The case a log line got wrong. A tidy collection we asked for ourselves runs on a pool thread, and the runtime publishes its count and its pause
     * after the other threads are already running again - so a gap classified in between saw "no collection" and blamed the machine. Measured, same
     * millisecond: `gc.tidy log loaded: … gen2 +1, stopped 794 ms` beside `STOP-THE-WORLD 969 ms … profiler or gcdump, power management, or no CPU for
     * anybody`. The code that called GC.Collect knows what it did, so it says so, and that sentence outranks the arithmetic.
     */
    [TestMethod]
    public void AStopWeAskedForIsNamedAsOursAndNotBlamedOnTheMachine()
    {
      var why = PerfGap.Classify(969, 0, 0, 0, 0, "log loaded", 794);

      StringAssert.Contains(why, "gc.tidy", "ours has to read as ours");
      StringAssert.Contains(why, "log loaded", "and carry the reason the tidy was asked for, which is the part that explains it");
      StringAssert.Contains(why, "794", "timed by the code that ran it, beside the gap it was inside");
      Assert.IsFalse(why.Contains("outside", StringComparison.Ordinal), $"the machine must not be blamed for our own collection (actual: {why})");
      Assert.IsFalse(why.Contains("profiler", StringComparison.Ordinal), $"no profiler hunt on a sentence about our own tidy (actual: {why})");
    }

    /* Ours outranks the arithmetic even when the counters also cover the gap: "gen2" says memory pressure, "gc.tidy (log loaded)" says a moment we chose. */
    [TestMethod]
    public void AStopWeAskedForIsNamedEvenWhenTheCountersAgree()
    {
      var why = PerfGap.Classify(2292, 2200, 1, 0, 1, "log loaded", 2210);

      StringAssert.Contains(why, "gc.tidy", "the counters agreeing is confirmation, not a reason to leave the cause unnamed");
      StringAssert.Contains(why, "pause counter agrees", "and the reader should know both sources point the same way");
    }

    /* A tidy still running when the line is written has no duration yet. Say that instead of printing an invented number, or a negative one. */
    [TestMethod]
    public void ATidyStillRunningSaysItIsStillRunning()
    {
      var why = PerfGap.Classify(1200, 0, 0, 0, 0, "log loaded", double.NaN);

      StringAssert.Contains(why, "still", "a collection in flight is a different fact from a finished one");
      StringAssert.Contains(why, "gc.tidy", "and it is still ours either way");
      Assert.IsFalse(why.Contains("-") || why.Contains("NaN"), $"no invented or negative number for a duration not measured yet (actual: {why})");
    }

    /*
     * When nothing was seen at all the sentence still points outside the runtime - but it now admits the counters can be late, because that is exactly how
     * the line above got believed for a run in which our own gen2 was the answer. A reader should hunt the profiler knowing which way the doubt falls.
     */
    [TestMethod]
    public void AnInnocentCollectorSaysTheCountersMightHaveBeenLate()
    {
      var why = PerfGap.Classify(1500, 0, 0, 0, 0);

      StringAssert.Contains(why, "outside", "the machine is still what is left when the collector is clear");
      StringAssert.Contains(why, "publish", $"and the known way this sentence can be wrong belongs in it (actual: {why})");
    }

    /* Log lines get grepped and pasted into spreadsheets: milliseconds print as whole numbers, with no ".0" to strip. */
    [TestMethod]
    public void MillisecondsPrintAsWholeNumbers()
    {
      var why = PerfGap.Classify(2292.6, 2200.4, 1, 0, 1);

      StringAssert.Contains(why, "2200 of the 2293 ms", "rounded to whole milliseconds, which is as fine as these numbers are trustworthy");
      Assert.IsFalse(why.Contains(".", StringComparison.Ordinal), $"a decimal point in a millisecond field costs a grep (actual: {why})");

      var ours = PerfGap.Classify(969.6, 0, 0, 0, 0, "log loaded", 794.4);

      StringAssert.Contains(ours, "794 of the 970 ms", "the sentence about our own collection rounds the same way");

      /* The label gc.tidy carries a dot of its own, so "no periods" is the wrong rule here: what must not appear is a digit, a point, a digit. */
      Assert.IsFalse(Regex.IsMatch(ours, @"\d\.\d"), $"a decimal fraction in a millisecond field costs a grep (actual: {ours})");
    }
  }
}
