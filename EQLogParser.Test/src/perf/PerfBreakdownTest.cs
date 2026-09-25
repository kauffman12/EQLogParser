using System;
using System.Diagnostics;
using System.Threading;

namespace EQLogParser
{
  /*
   * The instrument that answers "which half of a slow redraw is slow". Two properties matter more than the arithmetic.
   *
   * The first is that a pass which never closes cleanly must not leave a span in "in progress": a stall line names whatever span is still
   * open, so a phase abandoned by an exception would accuse itself in every stall for the rest of the session, which is worse than not having
   * measured (the same rule PerfCountersTest holds Run to, and the reason opening a phase closes the one before it).
   *
   * The second is that throttling the line never throws away the measurement. Lines are refused so a log stays readable; the spans underneath
   * them go on recording whether or not anything was printed, because the heartbeat averages over all of them.
   */
  [TestClass]
  public sealed class PerfBreakdownTest
  {
    /* Rolls the shared window so each test starts from a page nobody has written on. */
    [TestInitialize]
    public void ClearWindow() => PerfCounters.FormatWindow();

    /* Blocks for at least the given time, without sleeping: sleeping has its own granularity, and these are millisecond assertions. */
    private static double Busy(int ms)
    {
      var sw = Stopwatch.StartNew();

      while (sw.ElapsedMilliseconds < ms)
      {
        Thread.SpinWait(2000);
      }

      return sw.Elapsed.TotalMilliseconds;
    }

    /* A breakdown with phases named after the test, so the heartbeat assertions cannot be satisfied by another test's residue. */
    private static PerfBreakdown Trace(string test, params string[] phases) => new($"test.{test}", phases);

    /*
     * The point of registering each phase as a span is that the heartbeat keeps averaging it even when no line was written: "chart.walk avg
     * 310 ms" across a whole raid is a stronger statement than any single redraw's breakdown.
     */
    [TestMethod]
    public void EveryPhaseIsASpanOnTheHeartbeatToo()
    {
      var trace = Trace("spans", "test.spans.one", "test.spans.two");

      var pass = trace.NewPass();
      pass.Open(0);
      Busy(15);
      pass.Open(1);
      Busy(15);
      pass.Close();

      var window = PerfCounters.FormatWindow();
      StringAssert.Contains(window, "test.spans.one", "a phase must be on the heartbeat, not only in the slow line");
      StringAssert.Contains(window, "test.spans.two");
    }

    /*
     * A phase left open - the work threw between opening and closing - has to stop naming itself as soon as the pass is finished with.
     * Opening the next phase closes the previous one, and Complete/Abort close whatever is left.
     */
    [TestMethod]
    public void APhaseNobodyClosedStopsNamingItself()
    {
      Assert.AreEqual("nothing", PerfCounters.RunningReport(Environment.TickCount64), "no span may be open before the test opens one");

      var trace = Trace("leak", "test.leak.open", "test.leak.next");

      var abandoned = trace.NewPass();
      abandoned.Open(0);
      StringAssert.Contains(PerfCounters.RunningReport(Environment.TickCount64), "test.leak.open", "an open phase is an occupant");

      /* What the caller's finally does when the work in between threw: the pass is finished with, whatever state its phases are in. */
      abandoned.Complete(100_000);
      Assert.AreEqual("nothing", PerfCounters.RunningReport(Environment.TickCount64), "a pass that finished may hold no span open");

      /* Opening the next phase closes the one before it, so work skipped over cannot accumulate open spans either. */
      var stepped = trace.NewPass();
      stepped.Open(0);
      stepped.Open(1);

      var afterStep = PerfCounters.RunningReport(Environment.TickCount64);
      StringAssert.Contains(afterStep, "test.leak.next");
      Assert.IsFalse(afterStep.Contains("test.leak.open"), "the phase that ended when the next began must have ended for real");
      stepped.Abort();

      var nested = trace.NewPass();
      nested.Open(0);
      nested.Open(1);
      nested.Abort();

      Assert.AreEqual("nothing", PerfCounters.RunningReport(Environment.TickCount64), "aborting closes the phase the pass never reached");
    }

    /*
     * One redraw can plot twice (a selection change arriving inside an update), and the second plot's work belongs to the same phase. Had
     * this overwritten instead, a breakdown would have shown one third of where the time went while adding up to less than the total.
     */
    [TestMethod]
    public void APhaseOpenedTwiceAddsUp()
    {
      var trace = Trace("twice", "test.twice.walk");

      var pass = trace.NewPass();

      pass.Open(0);
      var first = Busy(25);
      pass.Close();

      pass.Open(0);
      var second = Busy(25);
      pass.Close();

      Assert.IsTrue(pass.Ms(0) >= first + second,
        $"phase reported {pass.Ms(0):0.##} ms for two runs of {first:0.##} and {second:0.##} ms");
      Assert.IsTrue(pass.TotalMs >= pass.Ms(0), "the phases may not account for more time than the pass took");
    }

    /* A quick pass leaves no trace in the log, which is what keeps this instrument worth leaving switched on. */
    [TestMethod]
    public void AQuickPassWritesNothing()
    {
      var trace = Trace("quick", "test.quick.work");
      trace.NoteIntervalMs = 0;

      var pass = trace.NewPass();
      pass.Open(0);
      Busy(5);

      Assert.IsNull(pass.Complete(10_000, "details nobody should read"), "a pass under budget has no business in the log");
      Assert.AreEqual(0, trace.NoteCount);
      Assert.AreEqual(0, trace.SuppressedCount);
    }

    /*
     * The line a slow pass produces: what it was doing, the sizes behind it, every phase in order, and the budget that made it a line at all.
     * Without the budget the numbers are unfalsifiable - the phase names are ours, not the framework's.
     */
    [TestMethod]
    public void ASlowPassNamesEveryPhaseAndItsBudget()
    {
      var trace = Trace("slow", "test.slow.walk", "test.slow.refresh");
      trace.NoteIntervalMs = 0;

      var pass = trace.NewPass();
      pass.Open(0);
      Busy(15);
      pass.Open(1);
      Busy(15);

      var line = pass.Complete(0, "walked 41233 records -> 7 lines");

      Assert.IsNotNull(line, "a pass over a zero budget must be written");
      StringAssert.StartsWith(line, "test.slow ", $"the pass name leads the line: {line}");
      StringAssert.Contains(line, "walked 41233 records", "the sizes are half the diagnosis");
      StringAssert.Contains(line, "test.slow.walk ");
      StringAssert.Contains(line, "test.slow.refresh ");
      StringAssert.Contains(line, "budget 0 ms");
      Assert.IsTrue(line.IndexOf("test.slow.walk", StringComparison.Ordinal) <
        line.IndexOf("test.slow.refresh", StringComparison.Ordinal), "phases read in the order they ran");

      /* Both phases cost something, so neither may print as zero: a phase that measured nothing would be an instrument that is not wired up. */
      Assert.IsFalse(line.Contains("test.slow.walk 0 ms"), line);
      Assert.IsFalse(line.Contains("test.slow.refresh 0 ms"), line);
    }

    /*
     * Lines are throttled, but a refused line is counted and said on the next one, because silence with no number attached is how an
     * instrument ends up being disbelieved. The measurement underneath survives the refusal.
     */
    [TestMethod]
    public void ASecondSlowPassIsSuppressedAndCounted()
    {
      var trace = Trace("throttle", "test.throttle.work");
      trace.NoteIntervalMs = 60_000;

      var first = trace.NewPass();
      first.Open(0);
      Busy(5);
      StringAssert.StartsWith(first.Complete(0) ?? "", "test.throttle ");

      var second = trace.NewPass();
      second.Open(0);
      Busy(5);

      Assert.IsNull(second.Complete(0), "a second line inside the interval is refused");
      Assert.AreEqual(1, trace.SuppressedCount, "what was swallowed is counted, not silently dropped");
      Assert.AreEqual(1, trace.NoteCount);

      /* The refused pass still recorded its span: throttling a sentence must not throw away a measurement. */
      StringAssert.Contains(PerfCounters.FormatWindow(), "test.throttle.work", "the suppressed pass still shows on the heartbeat");

      trace.NoteIntervalMs = 0;

      var third = trace.NewPass();
      third.Open(0);
      Busy(5);

      var line = third.Complete(0);
      StringAssert.Contains(line ?? "", "1 suppressed", $"the next line admits what it swallowed: {line}");
    }

    /* An unknown phase index is a programming error, not a crash in the middle of someone's raid. */
    [TestMethod]
    public void APhaseIndexOutsideTheBreakdownIsIgnored()
    {
      var trace = Trace("range", "test.range.only");

      var pass = trace.NewPass();
      pass.Open(7);
      pass.Open(-1);

      Assert.AreEqual("nothing", PerfCounters.RunningReport(Environment.TickCount64));
      Assert.AreEqual(0, pass.Ms(7));
      Assert.IsNull(pass.Complete(100_000));
    }
  }
}
