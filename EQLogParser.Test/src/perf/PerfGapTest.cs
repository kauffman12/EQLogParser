using System;

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

    /* Log lines get grepped and pasted into spreadsheets: milliseconds print as whole numbers, with no ".0" to strip. */
    [TestMethod]
    public void MillisecondsPrintAsWholeNumbers()
    {
      var why = PerfGap.Classify(2292.6, 2200.4, 1, 0, 1);

      StringAssert.Contains(why, "2200 of the 2293 ms", "rounded to whole milliseconds, which is as fine as these numbers are trustworthy");
      Assert.IsFalse(why.Contains(".", StringComparison.Ordinal), $"a decimal point in a millisecond field costs a grep (actual: {why})");
    }
  }
}
