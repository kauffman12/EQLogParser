using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;

namespace EQLogParser
{
  /*
   * Runs a test body on a thread WPF will accept.
   *
   * MSTest hands a test method an MTA thread, and WPF refuses to construct a `FrameworkElement` on one: the constructor walks into
   * `EnsureFrameworkServices`, which wants the thread's `InputManager`, and throws before any of our code has run. None of that matters to
   * a paint test — no message loop, no input, nothing is being pumped — but the check happens in the constructor, so a test for a class
   * derived from `UIElement` has to own an STA thread rather than merely borrow one. `Dispatcher.CurrentDispatcher` does not help: it hands
   * out a dispatcher on any thread and fails later, in the same place.
   *
   * Each call gets its own thread, which is also what makes the tests independent: WPF caches per-thread state, so two canvases built in
   * two tests do not share the services one of them may have configured. The body's exception travels back and is rethrown where the
   * assertion was written, so a failure reads as a failing test with its own stack rather than as a thread that stopped.
   *
   * A wedge is process-shaped, not body-shaped: this suite runs `[assembly: DoNotParallelize]`, one process end to end, and a wedged
   * body is a BACKGROUND thread that `Assert.Fail` does not kill - its residue (the measured case: an indeterminate bar's infinite
   * animation taking the render-thread path out from under a windowless test thread) can wedge later bodies in the same run. So every
   * wedge stays recorded until the process ends, and a timeout names every earlier still-stuck thread alongside this one: an empty
   * "earlier" list proves this body is the first wedge of the run and the suspect is its own body; a non-empty one says look back.
   */
  internal static class Sta
  {
    /* Generously long: these bodies assert on drawing, and the timeout exists to turn a wedge into a reported failure. */
    private const int TimeoutMs = 60_000;

    private sealed class Wedge
    {
      public Thread Thread = null!;
      public DateTime At = DateTime.UtcNow;
    }

    private static readonly ConcurrentDictionary<int, Wedge> _wedged = new();
    private static int _nextId;

    internal static void Run(Action body)
    {
      Exception? failure = null;
      var id = Interlocked.Increment(ref _nextId);

      var thread = new Thread(() =>
      {
        try
        {
          body();
        }
        catch (Exception ex)
        {
          failure = ex;
        }
      })
      { IsBackground = true, Name = $"EQLogParser test (STA) #{id}" };

      /* The apartment has to be claimed before the thread runs; afterwards WPF would take whatever it got and fail in a constructor. */
      if (!thread.TrySetApartmentState(ApartmentState.STA))
      {
        Assert.Fail("the test thread could not be made STA, so no WPF type can be constructed on it");
      }

      thread.Start();

      if (thread.Join(TimeoutMs))
      {
        if (failure is not null)
        {
          ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return;
      }

      // It is past the budget - now make the failure say WHERE. The interrupt probe reads as a second measurement: an interrupt that
      // LANDS means the body sat in an interruptible managed wait (the exception it raises often names the wait), and taking the thread
      // down also hands later bodies a cleaner process; one that lands nowhere means the body is inside code that does not yield - a
      // render-thread path - which points at whatever it last painted. Either way the list of still-stuck earlier threads says whether
      // THIS body started the wedging or merely inherited it.
      _wedged[id] = new Wedge { Thread = thread, At = DateTime.UtcNow };
      thread.Interrupt();
      var probeExited = thread.Join(5_000);

      if (probeExited)
      {
        if (failure is null)
        {
          // Finished cleanly between the budget and the probe - slow, not wedged. The body's asserts will rule on it; keep the
          // wedge list clean so a later timeout does not name this thread.
          _wedged.TryRemove(id, out _);
          return;
        }

        if (failure is not ThreadInterruptedException)
        {
          // The body DID finish - just past the budget - and it failed on its own: report that failure with its own stack rather
          // than a timeout. (A ThreadInterruptedException is the probe landing in an interruptible wait, which is the diagnosis.)
          _wedged.TryRemove(id, out _);
          ExceptionDispatchInfo.Capture(failure).Throw();
        }
      }

      var now = DateTime.UtcNow;
      var message = new StringBuilder($"a body running on an STA thread (#{id}) did not finish within {TimeoutMs / 1000} s");
      foreach (var entry in _wedged.OrderBy(entry => entry.Key))
      {
        var stuckFor = (int)(now - entry.Value.At).TotalSeconds;
        message.Append($"\n  STA #{entry.Key}: wedged ~{stuckFor} s ago{(entry.Key == id ? " (this body)" : " - an earlier body, still stuck")}");
      }

      message.Append(probeExited
        ? $"\n  the interrupt probe exited the body with: {failure}"
        : "\n  the interrupt probe landed nowhere: the body is inside code that does not yield (render-thread path)");

      Assert.Fail(message.ToString());
    }
  }
}
