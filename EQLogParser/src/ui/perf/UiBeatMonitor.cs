using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  /*
   * The watchdog: watches the application's one UI thread and says out loud when it stops answering.
   *
   * Why a watchdog at all, rather than timing the FCT overlay. Every window in this application draws on the same thread — the
   * overlay's paint (FctSkiaCanvas.OnRender), the damage meter's rebuild, the trigger text overlay, Syncfusion grids and charts — so
   * "the combat numbers froze for a second and then recovered without a restart" can be caused by any of them, and instrumenting only
   * the overlay would print an innocent "my paint took 1 ms" next to the freeze. This watches the thread itself: a beat is posted to
   * the dispatcher from a pool timer, and if a posted beat has not run after StallMs, the UI thread is doing something else for that
   * long — whoever it is, that is the event being hunted, and the line names the spans that were inside a timed pass at the time
   * (PerfCounters.RunningReport) plus what was open. When the beat finally runs, the same line comes back with the real duration.
   *
   * Two properties are worth keeping in mind when changing this:
   *
   *   The beat is posted at Render priority. Priorities below it starve during a window drag or resize (the classic DispatcherTimer
   *   problem), and an overlay that is being dragged is not the bug — so dragging must not look like a stall if it can be avoided.
   *   Dragging the game window in exclusive fullscreen can still starve everything; if a stall line ever coincides with a drag, that
   *   is what happened, and the line says so by naming no pass at all ("in progress: nothing").
   *
   *   Detection happens on a pool thread, before the process is well again. If the UI thread never comes back, the log still holds
   *   the "open" line with everything that was known while it was stuck; waiting only for the resumed beat would lose exactly the
   *   worst case.
   */
  internal static class UiBeatMonitor
  {
    /* A beat this late behind is a second of missing numbers — the symptom as players describe it. */
    internal const double DefaultStallMs = 1000;

    /* How often the pool timer checks on the UI thread; also the smallest delay that can be seen. */
    internal const int DefaultPollMs = 200;

    /* How long between heartbeat lines while an instrumented surface is open (~900 KB/h of log, against a rolling multi-megabyte file). */
    internal const double BeatSeconds = 20;

    /* Longer than this and the machine was asleep or the process was frozen; that is not a hitch and must not be reported as one. */
    internal const long SleepGuardMs = 30_000;

    private static readonly object _gate = new();
    private static readonly List<string> _surfaces = [];

    private static Timer _timer;
    private static Dispatcher _dispatcher;

    /* A beat handed to the dispatcher and not yet run. */
    private static int _inFlight;
    private static long _postedMs;

    /* One stall episode at a time: opened by the pool timer, closed by the beat that finally runs. */
    private static int _episodeOpen;
    private static long _episodeWaitedMs;

    private static double _stallMs = DefaultStallMs;
    private static int _stallCount;
    private static double _lastStallMs;
    private static double _worstStallMs;
    private static double _beatDelayMaxMs;

    private static long _windowStartMs;
    private static PerfGc.Reading _gcAtWindow;

    /* How many stalls have been reported since Start, and how bad they were — what the watchdog tests assert on. */
    internal static int StallCount => Volatile.Read(ref _stallCount);
    internal static double LastStallMs => Volatile.Read(ref _lastStallMs);
    internal static double WorstStallMs => Volatile.Read(ref _worstStallMs);

    /*
     * Begins watching. Called once from App.OnStartup, after logging is configured, with the dispatcher that owns the UI; the
     * thresholds are parameters so a test can wait 300 ms instead of a second and a half. Starting again replaces the previous run
     * and clears the counters, which keeps each measurement's stall count its own.
     */
    internal static void Start(Dispatcher dispatcher, double stallMs = DefaultStallMs, int pollMs = DefaultPollMs)
    {
      Stop();

      _dispatcher = dispatcher;
      _stallMs = stallMs;

      Volatile.Write(ref _lastStallMs, 0);
      Volatile.Write(ref _worstStallMs, 0);
      Volatile.Write(ref _beatDelayMaxMs, 0);
      Volatile.Write(ref _stallCount, 0);
      Interlocked.Exchange(ref _inFlight, 0);
      Interlocked.Exchange(ref _episodeOpen, 0);

      var now = Environment.TickCount64;
      _postedMs = now;
      _windowStartMs = now;
      _gcAtWindow = PerfGc.Sample();

      _timer = new Timer(Poll, null, pollMs, pollMs);
    }

    internal static void Stop()
    {
      Interlocked.Exchange(ref _inFlight, 0);
      Interlocked.Exchange(ref _episodeOpen, 0);

      var timer = _timer;
      _timer = null;
      _dispatcher = null;
      timer?.Dispose();
    }

    /*
     * Which instrumented surfaces are on screen, reported on every line. Half of "who blocked the UI thread" is knowing which
     * windows were even up: a stall with "open: fct+meter" points somewhere quite different from one with "open: fct".
     */
    internal static void NoteSurface(string name, bool open)
    {
      if (string.IsNullOrEmpty(name))
      {
        return;
      }

      lock (_gate)
      {
        if (open)
        {
          if (!_surfaces.Contains(name))
          {
            _surfaces.Add(name);
          }
        }
        else
        {
          _surfaces.Remove(name);
        }
      }
    }

    /* What is open, as one short word list. */
    internal static string Surfaces()
    {
      lock (_gate)
      {
        return _surfaces.Count == 0 ? "none" : string.Join("+", _surfaces);
      }
    }

    /*
     * The process-wide render mode, on every line: a machine that fell back to software rendering changes what every other number on the
     * page means. RenderOptions is in Media but the enum it holds is in Interop, which is why both are imported above.
     */
    internal static string RenderModeText() =>
      RenderOptions.ProcessRenderMode == RenderMode.Default ? "render:auto" : "render:software";

    /* Runs on a pool thread. Must never throw: an unhandled exception here would take the application down while reporting on it. */
    private static void Poll(object state)
    {
      try
      {
        var dispatcher = _dispatcher;

        if (dispatcher is null)
        {
          return;
        }

        var now = Environment.TickCount64;

        if (Volatile.Read(ref _inFlight) == 1)
        {
          var waited = now - Volatile.Read(ref _postedMs);

          /* The machine was asleep: the pending beat is stale, and the next one measures from now on. */
          if (waited > SleepGuardMs)
          {
            Interlocked.Exchange(ref _inFlight, 0);
            return;
          }

          if (waited >= _stallMs && Interlocked.CompareExchange(ref _episodeOpen, 1, 0) == 0)
          {
            _episodeWaitedMs = waited;

            PerfJournal.Stall($"UI STALL (open): beat posted {waited:0} ms ago has not run | open {Surfaces()} | " +
              $"in progress {PerfCounters.RunningReport(now)} | {RenderModeText()}");
          }

          return;
        }

        Volatile.Write(ref _postedMs, Environment.TickCount64);
        Interlocked.Exchange(ref _inFlight, 1);
        dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(OnBeat));
      }
      catch (Exception ex)
      {
        /* Losing the watchdog must be visible but must not become the application's problem. */
        Interlocked.Exchange(ref _inFlight, 0);
        Stop();
        PerfJournal.Note($"UI beat monitor stopped: {ex.Message}");
      }
    }

    /* Runs on the UI thread — the fact that it ran at all is the measurement. */
    private static void OnBeat()
    {
      var now = Environment.TickCount64;
      var waited = now - Volatile.Read(ref _postedMs);
      Interlocked.Exchange(ref _inFlight, 0);

      if (Interlocked.CompareExchange(ref _episodeOpen, 0, 1) == 1)
      {
        Volatile.Write(ref _lastStallMs, waited);
        Interlocked.Increment(ref _stallCount);

        if (waited > Volatile.Read(ref _worstStallMs))
        {
          Volatile.Write(ref _worstStallMs, waited);
        }

        PerfJournal.Stall($"UI STALL closed: beat ran {waited:0} ms late (first seen at {_episodeWaitedMs:0} ms) | " +
          $"open {Surfaces()} | in progress {PerfCounters.RunningReport(now)}");
      }

      if (waited > Volatile.Read(ref _beatDelayMaxMs))
      {
        Volatile.Write(ref _beatDelayMaxMs, waited);
      }

      var seconds = (now - _windowStartMs) / 1000.0;

      if (seconds < BeatSeconds)
      {
        return;
      }

      var gc = PerfGc.Sample();

      /* Heartbeats are written only while one of the instrumented surfaces is open: they exist to explain overlay stalls. */
      if (Surfaces() != "none")
      {
        PerfJournal.Beat($"UI perf {seconds:0} s | open {Surfaces()} | beat delay max {Volatile.Read(ref _beatDelayMaxMs):0} ms, " +
          $"stalls {_stallCount} worst {Volatile.Read(ref _worstStallMs):0} ms | {RenderModeText()} | " +
          $"{PerfGc.Format(_gcAtWindow, gc, seconds)} | {PerfCounters.FormatWindow()}");
      }

      _gcAtWindow = gc;
      Volatile.Write(ref _beatDelayMaxMs, 0);
      _windowStartMs = now;
    }
  }
}
