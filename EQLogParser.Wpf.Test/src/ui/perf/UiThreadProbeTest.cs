// <file>
//   Name:        UiThreadProbeTest.cs
//   Author:      John S
//   Created:     2026-09-21
//   Purpose:     Tests for the stall probe: a thread held by something outside itself reads as blocked, one doing work reads as running.
//   Notes:       Windows only, like the monitor it belongs to: the probe asks the operating system what a thread is doing, and that answer
//                exists nowhere else.
//                These are the two cases the whole probe exists to tell apart, so they are made to happen on purpose — one thread parked on an
//                event, one spinning — and asserted on the wording that reaches the stall line rather than on a wait reason's name. A reason
//                (UserRequest, EventPairPort, LpcReceive) is the operating system's vocabulary and differs between what a thread waits on and
//                which Windows is answering, so pinning one here would test Microsoft's naming rather than our reading of it.
//                Samples are taken by the test rather than by a timer, which keeps the counting deterministic; the watchdog takes the same
//                calls on its own 200 ms cadence. The tallies are process-wide, so one episode at a time — as in the application.
// </file>

using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The application's sources live in one flat namespace regardless of folder, which is how UiThreadProbe is reached from here.
namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class UiThreadProbeTest
  {
    /* How long a watched thread is held in its state before being sampled: long enough that the kernel has it, short enough to be cheap. */
    private const int HoldMs = 250;

    [TestCleanup]
    public void TestCleanup() => UiThreadProbe.Reset();

    /*
     * The two halves of the probe, checked separately: it must be able to name the calling thread, and that name must appear in this
     * process's own thread list. Everything else here is built on those, so when an episode reports nothing this is the test that says
     * which half broke — a missing operating-system call rather than a matching problem, and not, as it would otherwise look, a probe that
     * silently stopped listening.
     */
    [TestMethod]
    public void TheProbeNamesTheThreadItIsStandingOn()
    {
      var id = UiThreadProbe.CaptureThreadId();

      Assert.IsTrue(id > 0, $"the probe must name the calling thread (actual: {id})");
      Assert.IsTrue(UiThreadProbe.IsWatchable(id),
        $"thread {id} should be findable among this process's own threads; without that no episode can be reported");

      // A number that is not a thread must not be watchable, or the n/a path would never be reachable.
      Assert.IsFalse(UiThreadProbe.IsWatchable(0), "no thread has id zero");
      Assert.IsFalse(UiThreadProbe.IsWatchable(int.MaxValue), "a thread this process never made should not be found");
    }

    [TestMethod]
    public void AThreadHeldBySomethingElseReadsAsBlocked()
    {
      var inside = new ManualResetEventSlim(false);
      var release = new ManualResetEventSlim(false);
      int threadId = 0;

      /* The watched thread records its own operating-system id — the one a Dispatcher cannot give — then parks on an event. */
      var worker = new Thread(() =>
      {
        threadId = UiThreadProbe.CaptureThreadId();
        UiThreadProbe.BeginEpisode(threadId);
        inside.Set();
        release.Wait();
      })
      { IsBackground = true };

      worker.Start();
      Assert.IsTrue(inside.Wait(2000), "the watched thread should have started");

      Thread.Sleep(HoldMs);
      UiThreadProbe.SampleEpisode(threadId);
      UiThreadProbe.SampleEpisode(threadId);

      release.Set();
      worker.Join(2000);

      var observed = UiThreadProbe.EndEpisode();

      // The watchability of the id goes into every message below: when this fails, the answer wanted is which half of the probe broke.
      var context = $"actual: {observed}; captured id {threadId}, watchable {UiThreadProbe.IsWatchable(threadId)}";

      StringAssert.Contains(observed, "blocked", $"a thread waiting on an event should read as blocked ({context})");
      StringAssert.Contains(observed, "2/2 samples waiting", $"both samples were taken while it waited ({context})");

      // The reason is the useful half of the line — it is what points at a lock rather than at the render thread. Its name belongs to the
      // operating system (UserRequest, EventPairPort, LpcReceive) and differs by what the thread waits on, so only its presence is pinned.
      StringAssert.Contains(observed, "(", $"a wait should say what it waited on ({context})");
    }

    [TestMethod]
    public void AThreadDoingWorkReadsAsRunning()
    {
      var started = new ManualResetEventSlim(false);
      var finished = new ManualResetEventSlim(false);
      int threadId = 0;

      var worker = new Thread(() =>
      {
        threadId = UiThreadProbe.CaptureThreadId();
        UiThreadProbe.BeginEpisode(threadId);
        started.Set();

        var until = Environment.TickCount64 + HoldMs * 2;

        while (Environment.TickCount64 < until)
        {
          Thread.SpinWait(2000);
        }

        finished.Set();
      })
      { IsBackground = true };

      worker.Start();
      Assert.IsTrue(started.Wait(2000), "the spinning thread should have started");

      Thread.Sleep(HoldMs);
      UiThreadProbe.SampleEpisode(threadId);
      UiThreadProbe.SampleEpisode(threadId);

      var observedMidFlight = UiThreadProbe.EndEpisode();
      finished.Wait(4000);

      var context = $"actual: {observedMidFlight}; captured id {threadId}, watchable {UiThreadProbe.IsWatchable(threadId)}";

      // The case this exists to separate from the one above: a thread burning a core and a thread holding nothing look the same from a beat.
      StringAssert.Contains(observedMidFlight, "running", $"a spinning thread should read as running ({context})");
      Assert.IsFalse(observedMidFlight.Contains("blocked"), $"a spinning thread is not blocked ({context})");

      // CPU time on the line is what makes the count checkable: two samples of a spin are tens of milliseconds of it.
      StringAssert.Contains(observedMidFlight, "cpu", $"the line should carry the cpu the thread used ({context})");
    }

    [TestMethod]
    public void AThreadThatCannotBeWatchedSaysSo()
    {
      /*
       * An id that does not exist, and the zero a failed capture leaves behind, must report the absence rather than guess — and say which
       * absence it is, because "n/a" alone cannot tell a missing id from an id the thread list does not contain.
       */
      UiThreadProbe.BeginEpisode(999_999);
      UiThreadProbe.SampleEpisode(999_999);

      var observed = UiThreadProbe.EndEpisode();
      StringAssert.Contains(observed, "n/a", $"a thread the probe cannot find should report nothing rather than a verdict (actual: {observed})");
      StringAssert.Contains(observed, "999999", $"the line should name the id it could not find (actual: {observed})");

      UiThreadProbe.BeginEpisode(0);
      var uncaptured = UiThreadProbe.EndEpisode();
      StringAssert.Contains(uncaptured, "no thread id", $"an uncaptured id should say so rather than read as healthy (actual: {uncaptured})");

      // Ending with nothing counted also covers the watchdog's own stop path, which resets without ever having sampled.
      UiThreadProbe.Reset();
      Assert.AreEqual("ui thread: n/a", UiThreadProbe.EndEpisode(), "a reset probe should have no episode to report");
    }

    [TestMethod]
    public void SamplingAnotherThreadDoesNotCount()
    {
      var id = UiThreadProbe.CaptureThreadId();

      // Watching this thread, then sampling with a different id: the tally belongs to the episode that opened it, and a stale watchdog
      // restart must not be able to fill it from outside.
      UiThreadProbe.BeginEpisode(id);
      UiThreadProbe.SampleEpisode(id + 1);

      Assert.AreEqual("ui thread: n/a", UiThreadProbe.EndEpisode(), "a sample taken for another thread should be ignored");
    }
  }
}
