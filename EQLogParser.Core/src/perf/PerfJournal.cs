using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using log4net;

namespace EQLogParser
{
  /*
   * What the instrumentation decides to say, in the few lines it is allowed to say.
   *
   * Everything lands in the application log (%APPDATA%\EQLogParser\logs\EQLogParser.log), because the freeze being chased happens
   * at someone's keyboard during a raid and not on a development machine: the evidence has to be sitting in a file afterwards. That
   * same log carries game-data errors and rolls over at a few megabytes, which sets the two rules here - one line per event, and a
   * throttle on anything that could otherwise repeat every frame. A heartbeat is worth reading only if it did not push out the stall
   * from ten minutes earlier.
   *
   * Levels are chosen for the reader, not for verbosity: Warn for the two things that mean "the interface stopped", Info for the
   * context line around them.
   *
   * **None of it is written unless someone asks.** A heartbeat is one line every 20 seconds of uptime, which is 90 lines an hour of a game
   * whose normal complaints also have to fit in this file — and a session that is going well has nothing in those lines but numbers nobody
   * asked for. So the whole journal sits behind Enabled (settings.txt `PerfReport=True`; `Debug` implies it), and normal use writes nothing
   * here at all. The counters and the watchdog's measurements are not gated by it: they are a few interlocked adds per pass, cost roughly
   * nothing, and this switch decides only whether they get said out loud — except for the watchdog itself, which App does not start when the
   * journal is off, since a watchdog with nowhere to report is only the timer it posts on.
   */
  internal static class PerfJournal
  {
    /*
     * A span of UI-thread work this long has cost nine frames at 60 Hz, which is the point where a player stops reading a number in
     * time. It is deliberately far above a healthy frame (the FCT overlay paints in one to four milliseconds) and far below the
     * symptom being hunted, so it fires on real offenders rather than on ordinary work.
     */
    internal const double SlowPassMs = 150;

    /* How soon the same span may complain again: a pass slow every single second is one line, not three hundred. */
    internal const double RepeatSeconds = 5;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Dictionary<string, long> _lastWarn = [];
    private static readonly object _gate = new();

    /* Whether anything here reaches the log. Set once at startup from settings.txt; see the class note. Volatile because the watchdog
       thread reads it while the UI thread is the one that turns it on. */
    internal static volatile bool Enabled;

    /* The context line: what the process was doing between two heartbeats. */
    internal static void Beat(string line)
    {
      if (Enabled)
      {
        Log.Info(line);
      }
    }

    /* A one-off fact worth keeping - a surface being resized, a mode changing - logged whenever it happens, which is rarely. */
    internal static void Note(string line)
    {
      if (Enabled)
      {
        Log.Info(line);
      }
    }

    /*
     * Named span over SlowPassMs. Throttled per span name. Returns false when disabled, which is also what a suppressed repeat returns — and
     * correctly so: either way, no line was written. Nothing is throttled while disabled, so the first offender after it is turned on is
     * reported at once rather than waiting out a silence.
     */
    internal static bool SlowPass(string name, double ms) => Enabled && Warn($"pass:{name}", $"slow UI pass {name}: {ms:0.#} ms (threshold {SlowPassMs:0} ms)");

    /*
     * The interface stopped. Never throttled: episodes are rate-limited by whoever detects them, and two stalls inside five seconds is
     * exactly the kind of detail a throttle would destroy.
     *
     * Both lines of an episode are written by the watchdog rather than worded here, because their wording needs things that live on the
     * application side of this assembly - which surfaces were open, and the process render mode - and "the UI thread stopped" is the one
     * message in this file where being slightly redundant is cheaper than being short.
     */
    internal static void Stall(string line)
    {
      if (Enabled)
      {
        Log.Warn(line);
      }
    }

    /* Returns false when this warning was suppressed as a repeat, so a caller can count what it did not print. */
    private static bool Warn(string key, string line)
    {
      var now = Stopwatch.GetTimestamp();

      lock (_gate)
      {
        if (_lastWarn.TryGetValue(key, out var last) &&
          (now - last) * 1000.0 / Stopwatch.Frequency < RepeatSeconds * 1000)
        {
          return false;
        }

        _lastWarn[key] = now;

        /* One entry per instrumented span in practice; clearing is enough because a growing table here means a name built per call, which is a bug. */
        if (_lastWarn.Count > 256)
        {
          _lastWarn.Clear();
        }
      }

      Log.Warn(line);
      return true;
    }
  }
}
