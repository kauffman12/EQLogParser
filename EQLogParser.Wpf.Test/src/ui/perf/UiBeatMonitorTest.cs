// <file>
//   Name:        UiBeatMonitorTest.cs
//   Author:      John S
//   Created:     2026-07-29
//   Purpose:     Tests for the UI watchdog: a blocked dispatcher is reported as a stall, an ordinary queue of work is not.
//   Notes:       These run on Windows only — they need the WPF test host (EnableWindowsTargeting), and they stop working if that changes.
//                The thresholds sit well inside the ones the monitor reports on in production: a stall here needs 250 ms of blocking and
//                40 ms to notice, which leaves room for slow machines without waiting for anything suspicious.
//                The priorities below are load-bearing, stated as the numbers WPF actually uses: Send 10, Normal 9, Render 7 (where beats
//                go), Background 4, ContextIdle 3. Work queued by a test stays below Render so a beat can always reach it, and the pump's
//                budget check sits below Render but above that work — otherwise one of the two never gets a turn.
//                The first version of this file ended pumping with an item queued at Normal priority, and the blocked-thread test reported
//                nothing: when the sleep finished WPF picked that Normal-priority "stop" item ahead of the Render-priority beat, so the
//                message loop returned before the late beat ran. Looking these numbers up in the reference assembly is what fixed it.
// </file>

using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The application's sources live in one flat namespace regardless of folder, which is what UiBeatMonitor is reached through here.
namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class UiBeatMonitorTest
  {
    private const double StallMs = 250;
    private const int PollMs = 40;
    private const int BlockMs = 900;
    private const int BusyMs = 700;

    // When the self-re-arming callback should stop re-posting, in Stopwatch units. Touched from the dispatcher thread only.
    private static long _busyUntilMs;

    [TestCleanup]
    public void TestCleanup() => UiBeatMonitor.Stop();

    [TestMethod]
    public void ABlockedDispatcherIsReportedAsAStall()
    {
      var dispatcher = Dispatcher.CurrentDispatcher;

      UiBeatMonitor.Start(dispatcher, StallMs, PollMs);

      // An ordinary queued callback that holds the thread: the same shape as a paint pass that took too long.
      dispatcher.InvokeAsync(() => Thread.Sleep(BlockMs));
      PumpFor(dispatcher, BlockMs + 600);

      Assert.IsTrue(UiBeatMonitor.StallCount >= 1,
        $"a thread blocked for {BlockMs} ms with a {StallMs:0} ms threshold must be reported (actual: stalls={UiBeatMonitor.StallCount})");

      // The late beat drained before the pump's budget expired, so the episode knows how long it actually lasted as well.
      Assert.IsTrue(UiBeatMonitor.LastStallMs >= StallMs,
        $"the stall should measure {StallMs:0} ms or more (actual: last={UiBeatMonitor.LastStallMs:0} ms, beats={UiBeatMonitor.BeatCount})");
      Assert.IsTrue(UiBeatMonitor.WorstStallMs >= UiBeatMonitor.LastStallMs, "the worst cannot be under the last");
    }

    [TestMethod]
    public void AWorkingDispatcherIsNotReportedAsAStall()
    {
      var dispatcher = Dispatcher.CurrentDispatcher;

      UiBeatMonitor.Start(dispatcher, StallMs, PollMs);

      QueueBusyWork(dispatcher, BusyMs);
      PumpFor(dispatcher, BusyMs + 600);

      // This is the assertion the whole exercise hangs on: a queue that never emptied and an interface that never froze are not the same
      // thing, and every false positive found later traces back to confusing them here.
      Assert.AreEqual(0, UiBeatMonitor.StallCount,
        $"{BusyMs} ms of queued work is not a stall (worst: {UiBeatMonitor.WorstStallMs:0} ms)");

      // Without this the test above could pass merely because the watchdog never got going.
      Assert.IsTrue(UiBeatMonitor.BeatCount > 5, $"beats should flow through a busy queue (actual: {UiBeatMonitor.BeatCount})");
    }

    [TestMethod]
    public void StoppingEndsTheWatch()
    {
      var dispatcher = Dispatcher.CurrentDispatcher;

      UiBeatMonitor.Start(dispatcher);
      UiBeatMonitor.Stop();

      QueueBusyWork(dispatcher, 400);
      PumpFor(dispatcher, 600);

      Assert.AreEqual(0, UiBeatMonitor.StallCount, "a stopped monitor should report nothing");
      Assert.IsFalse(UiBeatMonitor.IsRunning, "the watchdog should report itself stopped");
    }

    [TestMethod]
    public void TheReportSaysWhichSurfacesWereOpen()
    {
      try
      {
        // Nothing registered: an overlay that never opened says so, rather than leaving a blank field beside the stall line.
        Assert.AreEqual("none", UiBeatMonitor.Surfaces(), "no open surface should read as none, not as nothing at all");

        UiBeatMonitor.NoteSurface("fct", true);
        Assert.AreEqual("fct", UiBeatMonitor.Surfaces(), "the overlay should name itself once open");

        // The reason the field exists: "fct" and "fct+meter" are different suspects, so every open window has to appear.
        UiBeatMonitor.NoteSurface("meter", true);
        Assert.AreEqual("fct+meter", UiBeatMonitor.Surfaces(), "every open surface should be listed");

        // Closing has to actually remove. A surface left listed after its window closed would point every later line at the wrong one.
        UiBeatMonitor.NoteSurface("fct", false);
        Assert.AreEqual("meter", UiBeatMonitor.Surfaces(), "a closed surface should drop out of the report");

        // Re-registering an open surface must not list it twice: the overlay opens and closes windows repeatedly in a session.
        UiBeatMonitor.NoteSurface("meter", true);
        Assert.AreEqual("meter", UiBeatMonitor.Surfaces(), "opening an already-open surface should not duplicate it");

        // Every line carries the render mode; which mode a machine picked is that machine's business, the field always being there is ours.
        StringAssert.StartsWith(UiBeatMonitor.RenderModeText(), "render:", "the render mode field should always be present");
      }
      finally
      {
        UiBeatMonitor.NoteSurface("meter", false);
        UiBeatMonitor.NoteSurface("fct", false);
      }
    }

    /*
     * A gap the collections cover is the collector, and it has to be named as such with the generation that did it: a gen2 collection is a
     * memory-pressure problem while a gen0 is ordinary traffic, and the two are different fixes. This attribution exists because the watchdog
     * cannot report a stop-the-world as a stall — the beat was never late, the whole process was frozen — so these words are the only trace of
     * the freeze in the log (measured on a real soak: 2,292 ms stopped inside a window whose "beat delay max" read 0 ms).
     */
    [TestMethod]
    public void AGapTheCollectorOwnsIsBlamedOnTheCollector()
    {
      var why = UiBeatMonitor.ClassifyGap(2292, 2200, 1, 0, 1);

      StringAssert.Contains(why, "collector", "a gap the pause counter covers belongs to the collector");
      StringAssert.Contains(why, "gen2", "the full collection is the one worth naming; gen0 traffic would read differently");
      StringAssert.Contains(why, $"{2200:0}", "how much of the gap was actually accounted for, so a reader can see the remainder");
    }

    /* A gen0-only pause must not read as a full collection: it sends the reader looking for a memory problem that is not there. */
    [TestMethod]
    public void AYoungGenerationPauseSaysSo()
    {
      StringAssert.Contains(UiBeatMonitor.ClassifyGap(600, 580, 4, 0, 0), "gen0",
        "a gap explained by young collections should not be dressed up as a full one");
    }

    /* Collections happened but do not explain the wait: say both, because the remainder is the part still looking for a name. */
    [TestMethod]
    public void CollectionsThatDoNotCoverTheGapSaySo()
    {
      var why = UiBeatMonitor.ClassifyGap(3000, 120, 2, 1, 0);

      StringAssert.Contains(why, "3 collections", "the collections that did happen are still worth counting");
      StringAssert.Contains(why, $"{3000:0}", "and the gap they failed to explain has to be stated with them");
    }

    /* No collection at all means something outside the runtime froze the process — a profiler or gcdump, power management, or no CPU. */
    [TestMethod]
    public void AGapWithNoCollectionIsNotBlamedOnTheCollector()
    {
      var why = UiBeatMonitor.ClassifyGap(1500, 0, 0, 0, 0);

      StringAssert.Contains(why, "no collection", "an innocent collector must be able to say it was innocent");
      StringAssert.Contains(why, "outside", "and point at what is left: a suspended process, sleep, or starvation");
    }

    /*
     * The counters are read on the watchdog's pool thread from several places, so a delta can come back negative when two reads cross. A line
     * that prints "-42 ms" costs the reader time on arithmetic instead of on the freeze, so the floor is zero.
     */
    [TestMethod]
    public void BackwardsCounterReadsDoNotPrintANegative()
    {
      var why = UiBeatMonitor.ClassifyGap(900, -42, -1, -1, -1);

      Assert.IsFalse(why.Contains("-"), $"attribution built from backwards reads should clamp to zero (actual: {why})");
    }

    /*
     * Queues one self-re-arming callback at ContextIdle: continuous queue work that stays below the beat priority on purpose, so this
     * measures "the interface had things to do" rather than "the interface was denied". A chain at Normal would starve a beat outright,
     * and the monitor calling that a stall would be correct.
     */
    private static void QueueBusyWork(Dispatcher dispatcher, int ms)
    {
      _busyUntilMs = Stopwatch.GetTimestamp() + (long)(ms / 1000.0 * Stopwatch.Frequency);

      void Busy()
      {
        Thread.SpinWait(2000);

        if (Stopwatch.GetTimestamp() < Volatile.Read(ref _busyUntilMs))
        {
          dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(Busy));
        }
      }

      dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(Busy));
    }

    /*
     * Pumps this thread's dispatcher for a wall-clock budget and no longer, by re-posting a check at Background priority — below the beats,
     * above whatever a test queues. A frame is ended from inside its own callback, which is the documented way; none of this depends on
     * Dispatcher.Run or on its shutdown semantics.
     *
     * If the pump ever fails to end on budget, a watchdog thread shuts the dispatcher down an extra ten seconds later: a test failure
     * arriving as a failure instead of as a hung IDE run. That leaves this thread's dispatcher unusable afterwards, which matters only for
     * whatever the runner schedules on this thread next, and only when the pump was already broken.
     */
    private static void PumpFor(Dispatcher dispatcher, int ms)
    {
      var frame = new DispatcherFrame();
      var sw = Stopwatch.StartNew();
      var done = new ManualResetEventSlim(false);

      void Budget()
      {
        if (sw.ElapsedMilliseconds >= ms)
        {
          done.Set();
          frame.Continue = false;
          return;
        }

        // A re-posted item at a fixed priority would otherwise spin the thread; the pause is what makes an idle pump idle.
        Thread.Sleep(1);
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Budget));
      }

      var watchdog = new ShutdownWatchdog(dispatcher, done, ms + 10_000);

      try
      {
        Budget();
        Dispatcher.PushFrame(frame);
      }
      finally
      {
        done.Set();
        watchdog.Join(2000);
      }
    }

    /* Watchdog insurance for PumpFor: shuts the dispatcher down if the pump outlives its budget plus grace. */
    private sealed class ShutdownWatchdog
    {
      private readonly Dispatcher _dispatcher;
      private readonly ManualResetEventSlim _done;
      private readonly int _ms;
      private readonly Thread _thread;

      internal ShutdownWatchdog(Dispatcher dispatcher, ManualResetEventSlim done, int ms)
      {
        _dispatcher = dispatcher;
        _done = done;
        _ms = ms;
        _thread = new Thread(Wait) { IsBackground = true };
        _thread.Start();
      }

      internal void Join(int ms) => _thread.Join(ms);

      private void Wait()
      {
        if (_done.Wait(_ms))
        {
          return;
        }

        try
        {
          _dispatcher.InvokeShutdown();
        }
        catch (Exception)
        {
          // The pump was already coming apart; nothing here can make it more broken than it is.
        }
      }
    }
  }
}
