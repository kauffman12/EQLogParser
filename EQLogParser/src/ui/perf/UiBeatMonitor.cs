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
   *   The beat is posted at Render priority — 7 on a scale where DispatcherTimer's default Background is 4 and ordinary work is Normal 9.
   *   That puts it above the band which starves while a window is being dragged or resized (the classic DispatcherTimer problem), so
   *   dragging an overlay does not look like a stall; an overlay under the cursor is not the bug being hunted. It stays below Normal on
   *   purpose: the delay in a beat line then includes time spent behind work the player was also waiting for, and the monitor never
   *   reports more than the thread actually owed the interface. Dragging the game window in exclusive fullscreen can still starve
   *   everything; if a stall line ever coincides with a drag, that is what happened, and the line says so by naming no pass at all.
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

    /*
     * A poll this late behind its own interval means the pool timer did not run when it was told to, which no beat can report: whatever
     * stopped it stopped every managed thread in the process, including the one that posts beats. Two and a half times the poll interval, so
     * an ordinary scheduling wobble stays silent; a whole-app freeze is what this is listening for, and those measure in hundreds of milliseconds.
     */
    internal const double GapReportMs = 500;

    /* Two of these inside a second would be one event told twice, so the shout is throttled while every gap keeps being counted. */
    private const long GapShoutMs = 5_000;

    /*
     * Where the process stopped as a row on the heartbeat: the count is how many times the world stopped in that window, the max is the
     * length of the worst freeze. It measures the process rather than any thread, hence uiThread:false - and it is the only in-app number
     * that sees a stop-the-world at all, because the collection freezes the watchdog's own timer: the beat posted afterwards is punctual, so
     * "beat delay max" stays at 0 ms while the player watched two seconds of nothing (measured: a 2,292 ms gen2 pause inside a window whose
     * beat delay read 0 ms, found only because a collector was running outside the app).
     *
     * Recorded through Record, which also raises the generic slow-pass warning for anything over its threshold - so a big stop writes two
     * lines, one that says which span was slow and one that says who did it. Both are throttled; keeping them separate is cheaper than
     * teaching the slow-pass rule to make an exception.
     */
    private static readonly int WorldStopId = PerfCounters.Register("ui.worldstop", uiThread: false);

    private static readonly object _gate = new();
    private static readonly List<string> _surfaces = [];

    private static Timer _timer;
    private static Dispatcher _dispatcher;

    /* Which operating-system thread the interface lives on, so a stall episode can ask the kernel what it was doing. */
    private static int _uiThreadId;

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
    private static int _beatCount;

    private static long _windowStartMs;
    private static PerfGc.Reading _gcAtWindow;

    /*
     * Where the last poll ran, and what the collector had done by then. The interval between this timer's own callbacks is a measurement of
     * the whole process rather than of the UI thread: see GapReportMs. The collector marks are read every poll because they are four field
     * reads, and a watchdog that is expensive enough to be turned off has measured nothing.
     */
    private static long _lastPollMs;
    private static double _pollPauseMs;
    private static int _pollGen0;
    private static int _pollGen1;
    private static int _pollGen2;
    private static long _lastShoutMs;

    /*
     * How many stalls have been detected since Start, and how bad they were. Counted at detection rather than on the closing beat, so an
     * episode whose process never comes back still counts: it is the one that was reported and never explained. Durations are the other
     * half, and can only be filled in by the late beat — an episode the process did not survive leaves them at zero.
     */
    internal static int StallCount => Volatile.Read(ref _stallCount);
    internal static double LastStallMs => Volatile.Read(ref _lastStallMs);
    internal static double WorstStallMs => Volatile.Read(ref _worstStallMs);

    /*
     * Whether a watch is running, and how many beats have landed since it started. The count exists for the tests: "no stalls were
     * reported" only means something when beats were actually arriving, and without it a watchdog that never got going would look
     * exactly like a healthy interface.
     */
    internal static bool IsRunning => Volatile.Read(ref _timer) is not null;
    internal static int BeatCount => Volatile.Read(ref _beatCount);

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
      Volatile.Write(ref _beatCount, 0);
      Interlocked.Exchange(ref _inFlight, 0);
      Interlocked.Exchange(ref _episodeOpen, 0);

      /* Asked for here, while the caller is still known to be the UI thread; see UiThreadProbe for what it is used for. */
      _uiThreadId = UiThreadProbe.CaptureThreadId();
      UiThreadProbe.Reset();

      var now = Environment.TickCount64;
      _postedMs = now;
      _windowStartMs = now;
      _lastPollMs = now;
      _lastShoutMs = 0;
      MarkCollector();
      _gcAtWindow = PerfGc.Sample();

      _timer = new Timer(Poll, null, pollMs, pollMs);
    }

    internal static void Stop()
    {
      Interlocked.Exchange(ref _inFlight, 0);
      Interlocked.Exchange(ref _episodeOpen, 0);
      UiThreadProbe.Reset();

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

        /*
         * The gap between this callback and the last one is taken before anything else, so it carries the delay rather than the cost of
         * reporting it. Sleep-sized gaps are not reported at all: a laptop that was closed for an hour has not frozen.
         */
        var gap = now - _lastPollMs;
        _lastPollMs = now;

        if (gap >= GapReportMs && gap < SleepGuardMs)
        {
          NoteGap(gap, now);
        }
        else
        {
          MarkCollector();
        }

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
            Interlocked.Increment(ref _stallCount);

            UiThreadProbe.BeginEpisode(_uiThreadId);

            PerfJournal.Stall($"UI STALL (open): beat posted {waited:0} ms ago has not run | open {Surfaces()} | " +
              $"in progress {PerfCounters.RunningReport(now)} | {RenderModeText()}");
          }

          /*
           * Watching the thread itself for as long as the episode lasts. Whether it is running or waiting is the half of the answer that no
           * span can give, and it is only knowable while the stall is happening: by the time the late beat runs, the thread is by definition
           * free again and its state says nothing about what held it.
           */
          if (Volatile.Read(ref _episodeOpen) == 1)
          {
            UiThreadProbe.SampleEpisode(_uiThreadId);
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

    /*
     * Who stopped the world, in as many words as these four numbers can carry. The runtime's cumulative pause counter is the tie-breaker:
     * it counts time the collector held every thread, so a gap that the collections do not explain was stopped by something outside the
     * managed runtime - a profiler or gcdump suspending the process, power management, or a machine with no CPU left for anybody.
     */
    internal static string ClassifyGap(double gapMs, double pauseMs, int gen0, int gen1, int gen2)
    {
      var collections = Math.Max(0, gen0) + Math.Max(0, gen1) + Math.Max(0, gen2);

      if (pauseMs >= gapMs * 0.5)
      {
        return $"collector ({(gen2 > 0 ? "gen2, the full collection" : gen1 > 0 ? "gen1" : "gen0")}), it held every thread for {Math.Max(0, pauseMs):0} of the {gapMs:0} ms";
      }

      return collections > 0
        ? $"{collections} collection(s) account for only {Math.Max(0, pauseMs):0} of the {gapMs:0} ms - the rest is not the collector"
        : $"no collection in that gap: {gapMs:0} ms stopped from outside the runtime (profiler or gcdump, power management, or no CPU for anybody)";
    }

    /* Remembers what the collector had done as of this poll, so the next gap can be attributed. Cheap enough to do every poll. */
    private static void MarkCollector()
    {
      _pollGen0 = GC.CollectionCount(0);
      _pollGen1 = GC.CollectionCount(1);
      _pollGen2 = GC.CollectionCount(2);
      _pollPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
    }

    /*
     * Says out loud that every thread in the process was stopped for a while, and by whom as far as it can be known. Recorded on every gap
     * over GapReportMs - the count and the worst one then ride along on the heartbeat as ui.worldstop - and written as its own line once the
     * gap reaches the stall threshold, because that is the event a player describes as "everything stopped" and it needs its own timestamp:
     * the beat monitor cannot report it as a UI stall, since the beat was never late; the beat's own process was frozen with everything else.
     */
    private static void NoteGap(double gapMs, long now)
    {
      var gen0 = GC.CollectionCount(0);
      var gen1 = GC.CollectionCount(1);
      var gen2 = GC.CollectionCount(2);
      var pauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;

      PerfCounters.Record(WorldStopId, gapMs);

      if (gapMs >= _stallMs && now - Volatile.Read(ref _lastShoutMs) >= GapShoutMs)
      {
        Volatile.Write(ref _lastShoutMs, now);

        /* The heap is read here rather than every poll: this line is the only thing that wants it, and it wants it to show what a full collection was asked to walk. */
        PerfJournal.Stall($"STOP-THE-WORLD {gapMs:0} ms with no late beat | {ClassifyGap(gapMs, pauseMs - _pollPauseMs, gen0 - _pollGen0, gen1 - _pollGen1, gen2 - _pollGen2)}" +
          $" | heap {PerfGc.Sample().HeapBytes / (1024 * 1024.0):0} MB | open {Surfaces()} | {RenderModeText()}");
      }

      _pollGen0 = gen0;
      _pollGen1 = gen1;
      _pollGen2 = gen2;
      _pollPauseMs = pauseMs;
    }

    /* Runs on the UI thread — the fact that it ran at all is the measurement. */
    private static void OnBeat()
    {
      var now = Environment.TickCount64;
      var waited = now - Volatile.Read(ref _postedMs);
      Interlocked.Exchange(ref _inFlight, 0);
      Interlocked.Increment(ref _beatCount);

      // The count went up when the episode was detected; this is where its length becomes knowable.
      if (Interlocked.CompareExchange(ref _episodeOpen, 0, 1) == 1)
      {
        Volatile.Write(ref _lastStallMs, waited);

        if (waited > Volatile.Read(ref _worstStallMs))
        {
          Volatile.Write(ref _worstStallMs, waited);
        }

        PerfJournal.Stall($"UI STALL closed: beat ran {waited:0} ms late (first seen at {_episodeWaitedMs:0} ms) | " +
          $"open {Surfaces()} | in progress {PerfCounters.RunningReport(now)} | {UiThreadProbe.EndEpisode()}");
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
