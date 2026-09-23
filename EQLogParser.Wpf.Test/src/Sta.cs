using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Runtime.ExceptionServices;
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
   */
  internal static class Sta
  {
    /* Generously long: these bodies assert on drawing, and the timeout exists to turn a wedge into a reported failure. */
    private const int TimeoutMs = 60_000;

    internal static void Run(Action body)
    {
      Exception? failure = null;

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
      { IsBackground = true, Name = "EQLogParser test (STA)" };

      /* The apartment has to be claimed before the thread runs; afterwards WPF would take whatever it got and fail in a constructor. */
      if (!thread.TrySetApartmentState(ApartmentState.STA))
      {
        Assert.Fail("the test thread could not be made STA, so no WPF type can be constructed on it");
      }

      thread.Start();

      if (!thread.Join(TimeoutMs))
      {
        Assert.Fail($"a body running on an STA thread did not finish within {TimeoutMs / 1000} s");
      }

      if (failure is not null)
      {
        ExceptionDispatchInfo.Capture(failure).Throw();
      }
    }
  }
}
