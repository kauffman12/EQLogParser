using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace EQLogParser
{
  /*
   * The watchdog, tested the way it fails in the field: by occupying the UI thread and seeing whether anything says so.
   *
   * This is the test that protects the diagnosis rather than the renderer. The freeze being chased is not in the FCT overlay — the
   * overlay paints in a couple of milliseconds and the numbers recover by themselves, which is the behaviour of a thread somebody else
   * was holding. So the instrument has to sit on the thread and name the occupant, and the risk in this design is not that it misses a
   * stall but that it cries wolf (flagging healthy work would make every stall line in the log worthless). Both directions are asserted
   * here, with a dispatcher actually pumping on this thread — one callback that blocks past the threshold, against a queue kept busy but
   * never blocked. The thresholds come in as parameters so the whole pair runs in about two seconds instead of waiting on a real second
   * and a half; the production defaults are the ones App.Start uses.
   */
  [TestClass]
  public sealed class UiBeatMonitorTest
  {
    private const int PollMs = 40;
    private const double StallMs = 250;
    private const int BlockMs = 900;

    [TestCleanup]
    public void StopTheWatch() => UiBeatMonitor.Stop();

    /* One callback holding the thread for longer than the threshold: this is the shape of "the numbers stopped for a second". */
    [TestMethod]
    public void ABlockedDispatcherIsReportedAsAStall()
    {
      var dispatcher = Dispatcher.CurrentDispatcher;
      UiBeatMonitor.Start(dispatcher, StallMs, PollMs);

      dispatcher.InvokeAsync(() => Thread.Sleep(BlockMs));
      dispatcher.InvokeAsync(() => Dispatcher.ExitAllFrames());

      try
      {
        Dispatcher.Run();
      }
      finally
      {
        UiBeatMonitor.Stop();
      }

      Assert.IsTrue(UiBeatMonitor.StallCount >= 1, "a thread blocked for nearly a second with a 250 ms threshold must be reported");
      Assert.IsTrue(UiBeatMonitor.WorstStallMs >= StallMs, $"the stall was measured at {UiBeatMonitor.WorstStallMs:0} ms");

      /* Starting again is a fresh count, so a report about one session cannot bleed into the next one's. */
      UiBeatMonitor.Start(dispatcher, StallMs, PollMs);
      Assert.AreEqual(0, UiBeatMonitor.StallCount);
    }

    /* The other direction, and the one that keeps the log worth reading: a queue that is busy but moving is not a stall. */
    [TestMethod]
    public void AWorkingDispatcherIsNotReportedAsAStall()
    {
      var dispatcher = Dispatcher.CurrentDispatcher;
      UiBeatMonitor.Start(dispatcher, StallMs, PollMs);

      var sw = Stopwatch.StartNew();

      void Busy()
      {
        Thread.SpinWait(20000);

        if (sw.ElapsedMilliseconds < 700)
        {
          dispatcher.InvokeAsync(Busy);
        }
        else
        {
          Dispatcher.ExitAllFrames();
        }
      }

      dispatcher.InvokeAsync(Busy);

      try
      {
        Dispatcher.Run();
      }
      finally
      {
        UiBeatMonitor.Stop();
      }

      Assert.AreEqual(0, UiBeatMonitor.StallCount, $"ordinary work was reported as {UiBeatMonitor.StallCount} stall(s) of " +
        $"{UiBeatMonitor.WorstStallMs:0} ms; a heartbeat nobody trusts explains nothing");
    }

    /* Which windows were up is half of what a stall line means, so the bookkeeping is worth pinning to the report it feeds. */
    [TestMethod]
    public void TheReportSaysWhichSurfacesWereOpen()
    {
      UiBeatMonitor.NoteSurface("fct", true);
      Assert.AreEqual("fct", UiBeatMonitor.Surfaces());

      // Shown twice is still one window on screen; the meter joins it, and the line has to read as a list rather than a tally.
      UiBeatMonitor.NoteSurface("fct", true);
      UiBeatMonitor.NoteSurface("meter", true);
      Assert.AreEqual("fct+meter", UiBeatMonitor.Surfaces());

      UiBeatMonitor.NoteSurface("fct", false);
      Assert.AreEqual("meter", UiBeatMonitor.Surfaces());

      UiBeatMonitor.NoteSurface("meter", false);
      Assert.AreEqual("none", UiBeatMonitor.Surfaces(), "nothing open prints nothing rather than an empty list");
    }

    /* A stopped watchdog must stop, or the pool timer it leaves behind keeps posting beats into a dispatcher that may be gone. */
    [TestMethod]
    public void StoppingEndsTheWatch()
    {
      var dispatcher = Dispatcher.CurrentDispatcher;
      UiBeatMonitor.Start(dispatcher, 100, PollMs);
      UiBeatMonitor.Stop();

      // Long past the threshold that was configured, with no monitor left to notice.
      Thread.Sleep(600);
      Assert.AreEqual(0, UiBeatMonitor.StallCount);
    }
  }
}
