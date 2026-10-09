using System;
using System.Windows.Threading;

namespace EQLogParser.Wpf.Test
{
  /*
   * DeferredBusyState (EQLogParser/src/ui/util/DeferredBusyState.cs) — the rule behind "Calculating DPS..." appearing
   * only when a summary build is actually slow.
   *
   * The behaviour being defended is the one this app used to have: the panes cleared their grids on the STARTED event,
   * which arrives on the dispatcher at Normal priority from the builder's own thread, and the COMPLETED event is posted at
   * the same priority. When both land in one drain — a fast build (2 ms for one fight, ~50 ms for fifteen, measured in
   * EQLogParser.log) or a UI thread stuck behind a chart update or a GC pause — the completion is processed before WPF's
   * pending render pass, so the cleared table never reaches the screen and the busy state is invisible. Slower builds won
   * their frame, which made it look random.
   *
   * These tests drive a real dispatcher because the class IS dispatcher timing: a DispatcherTimer only ticks while
   * something pumps, so each body owns its thread (Sta.Run) and pumps it for a bounded window. The delay is injected at
   * four times shorter than production's so the whole file costs well under a second.
   */
  [TestClass]
  public class DeferredBusyStateTest
  {
    // Short enough to keep the suite quick, long enough that InsideDelayMs is unambiguously inside it on any machine.
    private const int DelayMs = 40;

    // A fraction of the delay: what a single fight's board rebuild costs is far below even this.
    private const int InsideDelayMs = 8;

    // Generously over the delay, because a CI box can be slow — and because "did NOT show" assertions need the window to
    // have actually passed for the timer to be provably stopped rather than merely unlucky.
    private const int PastDelayMs = 250;

    [TestMethod]
    public void ABuildThatAnswersInsideTheDelayNeverShowsTheBusyState()
    {
      var shown = 0;

      Sta.Run(() =>
      {
        var busy = new DeferredBusyState(DelayMs);

        busy.Arm(() => shown++);   // STARTED: the build has begun
        PumpFor(InsideDelayMs);
        busy.Cancel();             // COMPLETED arrives before anyone could have called the wait slow
        PumpFor(PastDelayMs);
      });

      Assert.AreEqual(0, shown,
        "a table that fills back in inside the delay must never have been emptied on screen — that is the strobe this class exists to stop");
    }

    [TestMethod]
    public void ABuildStillRunningPastTheDelaySaysSoExactlyOnce()
    {
      var shown = 0;

      Sta.Run(() =>
      {
        var busy = new DeferredBusyState(DelayMs);
        busy.Arm(() => shown++);
        PumpFor(PastDelayMs);      // nobody answered; the wait is now long enough to look like a freeze
      });

      Assert.AreEqual(1, shown, "a build slow enough to be worth reporting says so");
      // The count is 1 rather than 5 because the tick stops the timer: a ten second rebuild must not re-fire its own
      // busy state every interval and repaint whatever that invalidates.
    }

    [TestMethod]
    public void TheNewestRequestIsTheOneThatGetsShown()
    {
      var older = 0;
      var newer = 0;

      Sta.Run(() =>
      {
        var busy = new DeferredBusyState(DelayMs);

        busy.Arm(() => older++);
        PumpFor(InsideDelayMs);
        busy.Arm(() => newer++);   // the operator moved again; the pane is told about a different selection
        PumpFor(PastDelayMs);
      });

      Assert.AreEqual(0, older, "an armed busy state describes work nobody is waiting on any more");
      Assert.AreEqual(1, newer, "and each pane shows the newest request's wait, not the first one it heard about");
    }

    [TestMethod]
    public void CancellingLeavesNothingArmedToBlankResultsLater()
    {
      var shown = 0;

      Sta.Run(() =>
      {
        var busy = new DeferredBusyState(DelayMs);

        busy.Arm(() => shown++);
        busy.Cancel();
        busy.Cancel();   // every event cancels first, including ones that never armed anything
        PumpFor(PastDelayMs);
      });

      Assert.AreEqual(0, shown,
        "the danger is a late tick blanking a table that is already showing real rows");
    }

    /*
     * A DispatcherTimer needs the dispatcher running to tick at all, so the test IS the message loop: a nested frame that
     * a second timer closes. PushFrame returns as soon as the window elapses, which keeps every body here far away from
     * the Sta.Run wedge budget.
     */
    private static void PumpFor(int ms)
    {
      var frame = new DispatcherFrame();
      var pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };

      pump.Tick += (_, _) =>
      {
        pump.Stop();
        frame.Continue = false;
      };

      pump.Start();
      Dispatcher.PushFrame(frame);
    }
  }
}
