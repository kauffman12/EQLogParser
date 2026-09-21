using System;
using System.Diagnostics;
using System.Threading;

namespace EQLogParser
{
  /*
   * The accounting behind a stall report. Nothing here is clever, and that is the point: this code runs inside frame paths during a
   * raid, so what has to be true about it is that it is cheap, that its window rolls over, and — the one property with real
   * consequences — that the name it prints for "what was in progress when the UI thread stopped" is the pass that is actually still
   * open. A span left marked running by an exception would go on accusing itself in every stall line afterwards, which is worse than
   * not measuring, so the nesting and closing rules are tested rather than trusted.
   */
  [TestClass]
  public sealed class PerfCountersTest
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

    /* One name, one row. Two windows measuring the same span must not print it twice and split its worst frame between them. */
    [TestMethod]
    public void RegisteringTheSameNameTwiceReturnsOneRow()
    {
      var first = PerfCounters.Register("test.dup");
      var again = PerfCounters.Register("test.dup");
      var other = PerfCounters.Register("test.other");

      Assert.AreEqual(first, again, "a name registered twice is one span, not two rows");
      Assert.AreNotEqual(first, other);
    }

    /* End hands its duration back, because the FCT header keeps its own copy of the paint time rather than reading it out of here. */
    [TestMethod]
    public void EndingATimedPassReturnsWhatItCost()
    {
      var id = PerfCounters.Register("test.timed");

      var mark = PerfCounters.Begin(id);
      var busy = Busy(25);
      var measured = PerfCounters.End(mark);

      Assert.IsTrue(measured >= busy, $"End reported {measured:0.##} ms for a pass that was busy for {busy:0.##} ms");
      Assert.IsTrue(PerfCounters.FormatWindow().Contains("test.timed"), "a span that ran must appear in the heartbeat");
    }

    /* The heartbeat is a short list, so it is ordered by damage done: one 30 ms pass belongs above thirty passes of nothing. */
    [TestMethod]
    public void ThePassThatCostsMostIsNamedFirst()
    {
      var many = PerfCounters.Register("test.many");
      var few = PerfCounters.Register("test.few");

      for (var i = 0; i < 40; i++)
      {
        PerfCounters.End(PerfCounters.Begin(many));
      }

      var mark = PerfCounters.Begin(few);
      Busy(30);
      PerfCounters.End(mark);

      var line = PerfCounters.FormatWindow();
      Assert.IsTrue(line.IndexOf("test.few", StringComparison.Ordinal) < line.IndexOf("test.many", StringComparison.Ordinal),
        $"sorted by milliseconds spent, not by how often it ran: {line}");
    }

    /* Reading the window closes it. A number that never resets reads as a leak, and the whole purpose here is to tell "now" from "since launch". */
    [TestMethod]
    public void RollingTheWindowClearsIt()
    {
      var id = PerfCounters.Register("test.window");

      for (var i = 0; i < 5; i++)
      {
        PerfCounters.End(PerfCounters.Begin(id));
      }

      Assert.IsTrue(PerfCounters.FormatWindow().Contains("n=5"));
      Assert.AreEqual("quiet", PerfCounters.FormatWindow(), "an idle window prints nothing rather than repeating the last second");
    }

    /* Counts and levels are not durations, so they print differently: "bake×7" and "hits=9" beside "paint n=60 avg 1.3 max 4.2 ms". */
    [TestMethod]
    public void CountersAndGaugesHaveTheirOwnShape()
    {
      var counter = PerfCounters.Register("test.bakes");
      var level = PerfCounters.Register("test.hits");

      PerfCounters.Note(counter, 7);
      PerfCounters.Gauge(level, 9);

      var line = PerfCounters.FormatWindow();
      StringAssert.Contains(line, "test.bakes×7");
      StringAssert.Contains(line, "test.hits=9");

      // A level is a level: it prints at its last value even when nothing happened since, which is what makes 0 on screen mean something.
      PerfCounters.Gauge(level, 0);
      StringAssert.Contains(PerfCounters.FormatWindow(), "test.hits=0");
    }

    /*
     * The attribution itself. A stall line names whatever is inside a span at the moment the beat fails to arrive, so a closed span
     * must have taken its name out of circulation - and nesting has to put the outer name back rather than clear it, or the overlay's
     * feed would stop naming itself the first time it called something timed.
     */
    [TestMethod]
    public void OnlyWhatIsStillRunningIsNamedInAStallLine()
    {
      Assert.AreEqual("nothing", PerfCounters.RunningReport(Environment.TickCount64), "no span may be open before the test opens one");

      var outer = PerfCounters.Register("test.outer");
      var inner = PerfCounters.Register("test.inner");

      var outerMark = PerfCounters.Begin(outer);
      var innerMark = PerfCounters.Begin(inner);

      var both = PerfCounters.RunningReport(Environment.TickCount64);
      StringAssert.Contains(both, "test.outer");
      StringAssert.Contains(both, "test.inner");

      PerfCounters.End(innerMark);
      var onlyOuter = PerfCounters.RunningReport(Environment.TickCount64);
      StringAssert.Contains(onlyOuter, "test.outer");
      Assert.IsFalse(onlyOuter.Contains("test.inner"), "a span that finished must stop naming itself in stall lines");

      PerfCounters.End(outerMark);
      Assert.AreEqual("nothing", PerfCounters.RunningReport(Environment.TickCount64));
    }

    /* A pass leaves its name behind if it faults, and the pass that faults is exactly the one worth measuring; Run's finally is the fix. */
    [TestMethod]
    public void AFaultedPassStopsNamingItself()
    {
      var id = PerfCounters.Register("test.faults");

      try
      {
        PerfCounters.Run(id, () => throw new InvalidOperationException("the pass blew up"));
      }
      catch (InvalidOperationException)
      {
        // the point of the test is what is left behind, not the exception
      }

      Assert.AreEqual("nothing", PerfCounters.RunningReport(Environment.TickCount64));
    }

    /*
     * "in progress" means the UI thread was inside this when it stopped, so work measured somewhere else must keep its cost without
     * taking its place in that sentence - the damage meter rebuilds on a pool thread, and naming it in a stall line points at a window
     * that was not holding anything.
     */
    [TestMethod]
    public void ABackgroundPassIsMeasuredButNeverNamedAsTheOccupant()
    {
      var offThread = PerfCounters.Register("test.background", uiThread: false);

      var mark = PerfCounters.Begin(offThread);
      Busy(15);

      Assert.AreEqual("nothing", PerfCounters.RunningReport(Environment.TickCount64));
      Assert.IsTrue(PerfCounters.End(mark) > 0, "registering off the UI thread costs the measurement, not just the attribution");
    }

    /* A handler that runs late every second gets one line about it, but a different span is not allowed to inherit that silence. */
    [TestMethod]
    public void TheSameSpanCannotWarnTwiceInsideItsThrottleWindow()
    {
      Assert.IsTrue(PerfJournal.SlowPass("test.throttle", PerfJournal.SlowPassMs + 40), "the first warning must be written");
      Assert.IsFalse(PerfJournal.SlowPass("test.throttle", PerfJournal.SlowPassMs + 40), "a repeating span is one line, not a log per frame");
      Assert.IsTrue(PerfJournal.SlowPass("test.otherSpan", PerfJournal.SlowPassMs + 40), "throttling is per span name");
    }
  }
}
