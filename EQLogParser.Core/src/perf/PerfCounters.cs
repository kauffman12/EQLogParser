using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace EQLogParser
{
  /*
   * Named accounting for work this application does, so a stall can be attributed without attaching a profiler.
   *
   * The problem this exists for is a report of the shape "the numbers stopped for a second or two, then were fine". That is a
   * description of a blocked thread, and the one thing such a report cannot say is *whose* work blocked it: the FCT overlay paints
   * on the application's UI thread, so does the damage meter, the trigger text overlay, every chart and every grid, and any one of
   * them holding that thread for a second stops all the others. A profiler answers the question exactly once, on the machine it was
   * run on, and not at all for the player whose laptop it happened to. So the accounting lives in the product: each suspect span is
   * registered by name, every pass records count/average/worst into a window, and the watchdog (UiBeatMonitor) prints that table once
   * a heartbeat alongside the gap between beats. The worst frame and the beat gap in the same line is the whole diagnosis - "our
   * paint took 900 ms" and "our paint took 1.2 ms while someone else's meter rebuild took 890 ms" are different bugs.
   *
   * Three shapes of entry, all reported in one table:
   *   timed   - Begin/End around a span; count, average and worst milliseconds (also what a stall names as "in progress")
   *   counted - Note(id); how many times something happened (halo bakes, dropped numbers)
   *   gauge   - Gauge(id, value); a level read at the last heartbeat (live hits, queued records)
   *
   * Numbers are best-effort. Fields are plain reads and writes rather than interlocked: entries for one span are written by one
   * thread in practice, two threads colliding on a counter would lose a sample of a diagnostic, and no decision anywhere depends on
   * an exact count. What matters is that a pass costs tens of nanoseconds, so instrumenting the frame path does not create the
   * hitch it is looking for - which is also why spans are addressed by integer handle instead of by name at every call.
   */
  internal static class PerfCounters
  {
    /*
     * How many entries FormatWindow names; the slow ones first, because a heartbeat nobody reads answers nothing. Ten rather than eight:
     * the trigger paths added a span and three counters each, and a window that displaces fct.dropConveyor from the line has traded away the
     * one number we were already reading for one we are only starting to read. The tiers below keep durations ahead of counts either way.
     */
    private const int DefaultLimit = 10;

    /*
     * How many counters share the line with those durations. They need a budget of their own rather than leftover room because with every
     * surface instrumented the timed spans alone fill ten rows, and the first measured run did exactly that: eight windows printed spans
     * while trig.logBatch and fct.dropConveyor - the two numbers that say what arrived and what was thrown away during them - never
     * appeared at all. Four is enough for the counted names in use and keeps a burst of one from hiding the rest.
     */
    private const int CountedLimit = 4;

    /* Stopwatch.Frequency is a property, so this cannot be a constant; it is computed once at type init all the same. */
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private enum Kind
    {
      Timed,
      Counted,
      Gauge
    }

    /*
     * One named span, counter or level. Public fields on purpose: this is a mutable record updated from whichever thread runs the
     * work, and the only reader is the heartbeat formatter. See the class comment for why that is acceptable here.
     */
    private sealed class Entry
    {
      public string Name;
      public Kind Kind;

      /* False for work that does not run on the UI thread, which therefore must never be named as the occupant of a stall. */
      public bool UiThread;

      /* Since the last FormatWindow. */
      public long WindowCount;
      public double WindowMs;
      public double WindowMaxMs;
      public bool WindowSeen;

      /* The last value published for a level (live hits on the canvas, records waiting); meaningless unless Kind is Gauge. */
      public double Gauge;

      /*
       * Attribution for a stall seen from another thread: 0 when this span is not running, else when it started. Read and written through
       * Volatile rather than declared volatile, which C# does not allow for a 64-bit field - and torn reads are exactly the failure that
       * would print a nonsense duration in a stall line.
       */
      public long RunningSinceMs;
    }

    /* A running timed span, handed back by Begin and returned to End. */
    internal readonly struct PerfMark
    {
      internal readonly int Id;
      internal readonly long StartTicks;

      internal PerfMark(int id, long startTicks)
      {
        Id = id;
        StartTicks = startTicks;
      }
    }

    private static readonly List<Entry> _entries = [];
    private static readonly object _gate = new();

    /*
     * A snapshot array the hot path indexes without locking: Register copies under the lock, so Begin/End/Note never take it and a
     * registration during a frame cannot block one. Handles are stable - entries are only ever appended.
     */
    private static volatile Entry[] _table = [];

    /*
     * Registers a span name and returns its handle. Idempotent by name, so two windows that both care about the same span share one
     * row in the table rather than printing twice; call it once, in a field initializer, and hold the handle.
     *
     * `uiThread: false` is for measured work that runs somewhere else - the damage meter's stats rebuild, for instance, which happens on
     * a pool thread. Its cost still belongs on the heartbeat (it is where a raid's allocation comes from), but it must not appear in
     * "in progress", because that phrase means "the UI thread was inside this when it stopped", and naming a background pass there would
     * send a reader to the wrong window while the real occupant went unnamed. The first registration of a name decides, since a name used
     * by both kinds of thread has no honest answer.
     */
    internal static int Register(string name, bool uiThread = true)
    {
      lock (_gate)
      {
        for (var i = 0; i < _entries.Count; i++)
        {
          if (_entries[i].Name == name)
          {
            return i;
          }
        }

        _entries.Add(new Entry { Name = name, UiThread = uiThread });
        _table = [.. _entries];
        return _entries.Count - 1;
      }
    }

    /* Starts a timed span. Nesting is fine: each mark closes itself, and the inner one stops naming itself first. */
    internal static PerfMark Begin(int id)
    {
      var entry = Resolve(id);
      if (entry is not null)
      {
        entry.Kind = Kind.Timed;
        entry.WindowSeen = true;

        if (entry.UiThread)
        {
          Volatile.Write(ref entry.RunningSinceMs, Environment.TickCount64);
        }
      }

      return new PerfMark(id, Stopwatch.GetTimestamp());
    }

    /* Closes a timed span, records it, and returns its duration so the caller can keep its own copy (the FCT header shows these). */
    internal static double End(PerfMark mark)
    {
      var ms = (Stopwatch.GetTimestamp() - mark.StartTicks) * TicksToMs;
      var entry = Resolve(mark.Id);

      if (entry is not null)
      {
        Volatile.Write(ref entry.RunningSinceMs, 0L);
        entry.WindowCount++;
        entry.WindowMs += ms;
        entry.WindowSeen = true;

        if (ms > entry.WindowMaxMs)
        {
          entry.WindowMaxMs = ms;
        }

        /*
         * The slow-pass warning rides here rather than at each call site, so "a named span on the UI thread took longer than a
         * frame budget times ten" is one rule with one threshold. PerfJournal throttles repeats: a pass that is slow every second
         * is one line in the log and a solved problem, not three hundred lines hiding the next stall.
         */
        if (ms >= PerfJournal.SlowPassMs)
        {
          PerfJournal.SlowPass(entry.Name, ms);
        }
      }

      return ms;
    }

    /* Runs work inside a timed span. Convenient where a try/finally would swallow a line; allocates one delegate, so use Begin/End in a frame path. */
    internal static void Run(int id, Action work)
    {
      var mark = Begin(id);

      try
      {
        work();
      }
      finally
      {
        End(mark);
      }
    }

    /* Counts occurrences of something that has no useful duration of its own. */
    internal static void Note(int id, long count = 1)
    {
      var entry = Resolve(id);

      if (entry is null)
      {
        return;
      }

      entry.Kind = Kind.Counted;

      /* Added atomically: a counted name may be written from the parsing thread while the heartbeat reads it on the UI thread. */
      Interlocked.Add(ref entry.WindowCount, count);
      entry.WindowSeen = true;
    }

    /* Publishes a level - live hits on screen, records waiting to be drawn - as of the last write. */
    internal static void Gauge(int id, double value)
    {
      var entry = Resolve(id);

      if (entry is null)
      {
        return;
      }

      entry.Kind = Kind.Gauge;
      entry.Gauge = value;
      entry.WindowSeen = true;
    }

    /* What is inside a timed span right now, with how long it has been in there: "in progress: meter.loadstats 812 ms". */
    internal static string RunningReport(long nowMs)
    {
      var table = _table;
      StringBuilder builder = null;

      for (var i = 0; i < table.Length; i++)
      {
        var since = Volatile.Read(ref table[i].RunningSinceMs);

        if (since <= 0)
        {
          continue;
        }

        builder ??= new StringBuilder();

        if (builder.Length > 0)
        {
          builder.Append(", ");
        }

        builder.Append(table[i].Name).Append(' ').Append(nowMs - since).Append(" ms");
      }

      return builder is null ? "nothing" : builder.ToString();
    }

    /*
     * The window as one line, worst spans first, then counters, then levels; calling it closes the window.
     *
     * Sorted by milliseconds spent rather than by count because the question a heartbeat answers is "who has been holding the UI
     * thread", and a span that ran 900 times for 0.2 ms is not the answer while one run of 40 ms is. The three kinds are collected
     * separately and printed in a fixed order — spans, counters, levels — each kind by how much of the window it took, because without
     * that separation a raid's halo bakes (a counter in the thousands) would crowd every duration off the line, which is backwards: the
     * durations are why the line exists.
     *
     * Each kind gets its own budget rather than sharing one, because with eight surfaces instrumented the spans alone fill ten rows and
     * a shared cap printed them while dropping every counter — including the drop counters that explain a queue. What has to be readable
     * together is the duration of a pass and what arrived during it. A span that never ran prints nothing, so an idle application's
     * heartbeat stays short even with everything registered.
     */
    internal static string FormatWindow(int limit = DefaultLimit)
    {
      var table = _table;
      var spans = new List<(string Text, double Weight)>();
      var counts = new List<(string Text, double Weight)>();
      var levels = new List<string>();

      for (var i = 0; i < table.Length; i++)
      {
        var entry = table[i];

        if (!entry.WindowSeen)
        {
          continue;
        }

        switch (entry.Kind)
        {
          case Kind.Timed when entry.WindowCount > 0:
            spans.Add(($"{entry.Name} n={entry.WindowCount} avg {entry.WindowMs / entry.WindowCount:0.#} max {entry.WindowMaxMs:0.#} ms", entry.WindowMs));
            break;

          case Kind.Counted when entry.WindowCount > 0:
            counts.Add(($"{entry.Name}×{entry.WindowCount}", entry.WindowCount));
            break;

          case Kind.Gauge:
            levels.Add($"{entry.Name}={entry.Gauge:0.##}");
            break;
        }
      }

      spans.Sort((a, b) => b.Weight.CompareTo(a.Weight));
      counts.Sort((a, b) => b.Weight.CompareTo(a.Weight));

      var builder = new StringBuilder();

      AppendRows(builder, spans, limit);
      AppendRows(builder, counts, CountedLimit);

      /* Levels are one token each and there are half a dozen of them, so all of them fit and none of them is worth dropping. */
      foreach (var level in levels)
      {
        if (builder.Length > 0)
        {
          builder.Append(", ");
        }

        builder.Append(level);
      }

      foreach (var entry in table)
      {
        entry.WindowCount = 0;
        entry.WindowMs = 0;
        entry.WindowMaxMs = 0;
        entry.WindowSeen = false;
      }

      return builder.Length == 0 ? "quiet" : builder.ToString();
    }

    /* Appends up to `budget` of the heaviest rows, comma separated, onto the line built so far. */
    private static void AppendRows(StringBuilder builder, List<(string Text, double Weight)> rows, int budget)
    {
      for (var i = 0; i < rows.Count && i < budget; i++)
      {
        if (builder.Length > 0)
        {
          builder.Append(", ");
        }

        builder.Append(rows[i].Text);
      }
    }

    private static Entry Resolve(int id)
    {
      var table = _table;
      return id >= 0 && id < table.Length ? table[id] : null;
    }
  }
}
