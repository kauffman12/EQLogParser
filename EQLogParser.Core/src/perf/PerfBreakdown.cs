using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace EQLogParser
{
  /*
   * Times the inside of a slow pass by phase, so one log line says WHERE the time went instead of only that something took long.
   *
   * The heartbeat already names whole passes - "chart.update n=9 avg 410 max 1693 ms" - but a whole pass is the wrong granularity for the
   * next question, which is "what comes off the UI thread". Redrawing a chart walks every record, aggregates per player, rolls a window over
   * them, sorts, then hands the result to the chart control, and usually exactly one of those steps is expensive. A breakdown registers each
   * phase as a span of its own (so the heartbeat keeps a row per phase with averages and maxima across the session) and additionally builds
   * one line, for a pass that goes over budget, listing every phase in order next to the sizes that explain it:
   *
   *   chart.update 1694 ms | DamageChart UPDATE | walked 41233 records -> 7 lines, 84000 points | plots 2 | chart.walk 310 ms … chart.refresh 1289 ms
   *
   * Phases are expected to run one after another: opening a phase closes the one before it, so an exception between open and close cannot
   * leave a span naming itself in "in progress" for the rest of the session (the same hazard PerfCounters documents for a mark nobody ends).
   * A phase opened twice inside one pass - a redraw that plots twice - adds up rather than overwriting, so the phases still account for the
   * total instead of showing only the last of them.
   *
   * The line is throttled: a pass slow three times a second is one solved problem, and three hundred lines would push out the next thing
   * worth reading. What gets swallowed is counted and then said on the next line, the way GcTidyUp reports the requests it turned down - and
   * Write hands back the line it built rather than only logging it, because a rule nobody can assert is a rule that quietly changes.
   */
  internal sealed class PerfBreakdown
  {
    /* How soon the same breakdown may be written again while passes keep going over budget. A test may shorten it. */
    internal const double DefaultNoteIntervalMs = 5_000;

    private readonly object _gate = new();
    private readonly string[] _phaseNames;
    private readonly int[] _spanIds;
    private long _lastNoteTicks = long.MinValue;
    private long _written;
    private long _suppressed;

    internal PerfBreakdown(string name, params string[] phaseNames)
    {
      Name = name;
      _phaseNames = [.. phaseNames];
      _spanIds = new int[phaseNames.Length];

      for (var i = 0; i < phaseNames.Length; i++)
      {
        _spanIds[i] = PerfCounters.Register(phaseNames[i]);
      }
    }

    /*
     * Name of the whole pass; it leads the line so the phases read as belonging to it.
     */
    internal string Name { get; }

    internal int PhaseCount => _phaseNames.Length;

    /*
     * How long a pass has to be slow before it is worth a line.
     */
    internal double NoteIntervalMs { get; set; } = DefaultNoteIntervalMs;

    /*
     * Breakdown lines written since this breakdown was created.
     */
    internal long NoteCount => Volatile.Read(ref _written);

    /*
     * Breakdown lines refused because one went out inside the interval. Counted, never silently dropped.
     */
    internal long SuppressedCount => Volatile.Read(ref _suppressed);

    /*
     * Starts timing one pass. A pass belongs to the thread that opened it - these are UI-thread passes.
     */
    internal Pass NewPass()
    {
      return new Pass(this);
    }

    /*
     * Builds the line and writes it, unless one went out inside the interval, in which case the refusal is counted. Returns what was written,
     * or null when nothing was: the caller in the application ignores it and a test does not.
     */
    private string Write(double totalMs, double slowMs, string details, double[] ms)
    {
      var now = Stopwatch.GetTimestamp();

      lock (_gate)
      {
        if (_lastNoteTicks != long.MinValue &&
          (now - _lastNoteTicks) * 1000.0 / Stopwatch.Frequency < NoteIntervalMs)
        {
          Interlocked.Increment(ref _suppressed);
          return null;
        }

        _lastNoteTicks = now;
        Interlocked.Increment(ref _written);

        var suppressed = Interlocked.Exchange(ref _suppressed, 0);
        StringBuilder line = new();

        line.Append(Name).Append(' ').Append(Ms(totalMs)).Append(" ms");

        if (!string.IsNullOrEmpty(details))
        {
          line.Append(" | ").Append(details);
        }

        /* Oldest phase first, so a reader sees the pipeline in the order it ran. */
        line.Append(" | ");

        for (var i = 0; i < _phaseNames.Length; i++)
        {
          if (i > 0)
          {
            line.Append(' ');
          }

          line.Append(_phaseNames[i]).Append(' ').Append(Ms(ms[i])).Append(" ms");
        }

        /* The budget belongs on the line: these phase names are ours rather than the framework's, so a reader needs to know what counted as slow. */
        line.Append(" | budget ").Append(Ms(slowMs)).Append(" ms");

        if (suppressed > 0)
        {
          line.Append(" | ").Append(suppressed).Append(" suppressed within ").Append(Ms(NoteIntervalMs)).Append(" ms");
        }

        PerfJournal.Note(line.ToString());
        return line.ToString();
      }
    }

    /* One decimal under a hundred milliseconds, whole above it; invariant culture so a log reads the same in every locale. */
    private static string Ms(double value)
    {
      var culture = CultureInfo.InvariantCulture;
      return value >= 100d ? value.ToString("0", culture) : value.ToString("0.#", culture);
    }

    internal sealed class Pass
    {
      private readonly PerfBreakdown _owner;
      private readonly double[] _ms;
      private readonly PerfCounters.PerfMark[] _marks;
      private readonly long _startTicks;
      private int _open = -1;

      internal Pass(PerfBreakdown owner)
      {
        _owner = owner;
        _ms = new double[owner._phaseNames.Length];
        _marks = new PerfCounters.PerfMark[owner._phaseNames.Length];
        _startTicks = Stopwatch.GetTimestamp();
      }

      /*
       * Opens a phase by index, closing whichever phase was open before it.
       */
      internal void Open(int phase)
      {
        Close();

        if ((uint)phase >= (uint)_owner._spanIds.Length)
        {
          return;
        }

        _open = phase;
        _marks[phase] = PerfCounters.Begin(_owner._spanIds[phase]);
      }

      /*
       * Closes whichever phase is open. Safe to call when none is.
       */
      internal void Close()
      {
        if (_open < 0)
        {
          return;
        }

        var phase = _open;
        _open = -1;
        _ms[phase] += PerfCounters.End(_marks[phase]);
      }

      /*
       * Milliseconds measured in one phase so far, for a caller that wants to branch on them.
       */
      internal double Ms(int phase)
      {
        return (uint)phase >= (uint)_ms.Length ? 0d : _ms[phase];
      }

      /*
       * Wall time from the pass opening to now, phases or no phases.
       */
      internal double TotalMs => (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;

      /*
       * Closes the pass and writes the line when it went over budget. Returns what was written (null when the pass was quick enough to leave
       * no trace), and the total is available from TotalMs either way.
       */
      internal string Complete(double slowMs, string details = null)
      {
        Close();

        var total = TotalMs;

        return total >= slowMs ? _owner.Write(total, slowMs, details, _ms) : null;
      }

      /*
       * Closes the pass without judging it: for a pass abandoned on the way out (a window closing under it, or one nested inside a pass another
       * chart owns), where the spans still have to be shut but the numbers describe nothing worth logging.
       */
      internal void Abort()
      {
        Close();
      }
    }
  }
}
