using System;
using System.Globalization;

namespace EQLogParser
{
  /*
   * What a session is actually holding, in one line, every so often.
   *
   * The reason it exists is the first Windows field run's unanswered sentence: "~330 MB of a 419 MB heap is not rows." That could only be
   * INFERRED, and inference is how a memory item gets argued about instead of measured. So this prints the parts, and "not rows" becomes
   * "rows are X, working set is Y, and Z grows per million lines".
   *
   * Two things are deliberately split: `Format` is arithmetic and string building over numbers it is handed, while the reading of the
   * collector and the live tables happens in `Collect`. That is not tidiness — it is the same reason PerfGap lives here instead of beside
   * the watchdog that calls it: **the wording is the product, and asserting wording needs a test run.** A line nobody on Linux can execute
   * ships wrong formatting (PerfGap's own history names that failure), and a formatter that reaches into live stores cannot be fed known
   * numbers to assert against.
   *
   * Rows vs slots is the pair this whole class exists for: the fact tables grow by doubling, so slots can be up to 2x rows, and the
   * difference (slack) is reclaimed only once per doubling by `CompactRows`. Printing both makes "the capture is huge" and "the capture
   * reserved more than it wrote" two different, checkable sentences instead of one big number.
   *
   * Nothing here runs unless asked: `PerfJournal.Enabled` is the single gate (set by settings.txt `PerfReport=True`, or `Debug`), so a
   * normal session puts no heap lines in the file that also carries the raid. When it IS on, this line is cheap — one collector snapshot
   * every IntervalSeconds, not per pass.
   */
  internal static class HeapLedger
  {
    /// <summary>How often a line goes out while the journal is on. Slower than a derive pass by design: this is a trend, not a heartbeat.</summary>
    internal const double IntervalSeconds = 30;

    /*
     * The cadence as arithmetic, so it can be asserted without waiting thirty seconds (and without a test suite that sleeps).
     *
     * The first ask is always due: the sizes are worth a line even before any delta exists.
     */
    internal static bool Due(long nowMs, long lastMs, bool havePrevious) =>
      !havePrevious || nowMs - lastMs >= IntervalSeconds * 1000;

    /*
     * Everything Format needs, already turned into deltas and counts. Sizes are raw bytes because formatting a delta after the fact is
     * where sign errors hide; the MB conversion happens once, here, on the way out.
     */
    internal readonly record struct LedgerStats(double Seconds, long WorkingSetBytes, long HeapBytes, double PauseMs,
      int Gen0, int Gen1, int Gen2, long FactRows, long FactSlackBytes, long HealRows, long HealSlackBytes,
      int Names, long EstimatedBytes, long CastEntries = 0, long TimedRecords = 0)
    {
    }

    /*
     * What the parse keeps OUTSIDE the two row arrays, handed in by whoever owns the session.
     *
     * The first Windows field run settled that rows are not the heap (`heap=655.8 MB` against `row arrays est=183.4 MB`)
     * — but "not rows" is still a subtraction rather than an answer. These are the two stores on that side of it which
     * grow with the capture and are reachable for nothing: the cast history (one entry per resolved cast, keyed by spell
     * name — what answers "which rank did this ambiguous name cast") and the timed records (a ReceivedSpell per buff
     * line, plus deaths/resists/loot). Both counts are kept on ADD inside RecordsStore: walking them from a diagnostics
     * line would mean taking the parse lane's own locks across hundreds of thousands of rows.
     */
    internal readonly record struct Extras(long CastEntries, long TimedRecords);

    /*
     * One line, key=value, no prose: it lands in a file a person greps while a raid is running. Bytes print as MB because the numbers are
     * eight-digit otherwise, and a memory question asked in a hurry is a question about magnitudes.
     */
    internal static string Format(in LedgerStats s)
      => string.Create(CultureInfo.InvariantCulture,
        $"heap: ws={Mb(s.WorkingSetBytes):F1} MB heap={Mb(s.HeapBytes):F1} MB pause {Math.Max(0, s.PauseMs):F1} ms"
        + $" | gc {Math.Max(0, s.Gen0)}/{Math.Max(0, s.Gen1)}/{Math.Max(0, s.Gen2)}"
        + $" | facts rows={Math.Max(0, s.FactRows):N0} slack={Mb(Math.Max(0, s.FactSlackBytes)):F1} MB"
        + $" heals rows={Math.Max(0, s.HealRows):N0} slack={Mb(Math.Max(0, s.HealSlackBytes)):F1} MB"
        + $" | names={s.Names:N0} | row arrays est={Mb(s.EstimatedBytes):F1} MB"
        + $" | kept casts={Math.Max(0, s.CastEntries):N0} timed records={Math.Max(0, s.TimedRecords):N0}"
        + $" | over {Math.Max(0, s.Seconds):F0}s");

    /*
     * Read the live state. `capture` may be null (a session that has not handed over a table yet) and every part then reads zero rather
     * than skipping the line: a line of zeros during a bulk load is information ("nothing captured yet"), while no line at all cannot be
     * told apart from "the ledger never ran".
     */
    internal static LedgerStats Collect(CombatCapture capture, in PerfGc.Reading from, in PerfGc.Reading to, double seconds,
      in Extras extras = default)
    {
      var factRows = 0L;
      var factSlack = 0L;
      var estimated = 0L;
      var healRows = 0L;
      var healSlack = 0L;
      var names = 0;

      if (capture?.Facts is { } facts)
      {
        factRows = facts.FactCount;
        factSlack = facts.SlackBytes;
        estimated += facts.EstimatedBytes;
        names = facts.InternedNames.Count;
      }

      if (capture?.HealFacts is { } heals)
      {
        healRows = heals.HealCount;
        healSlack = heals.SlackBytes;
        estimated += heals.EstimatedBytes;
      }

      return new LedgerStats(seconds, to.WorkingSetBytes, to.HeapBytes, PerfGc.WindowPauseMs(from, to),
        Math.Max(0, to.Gen0 - from.Gen0), Math.Max(0, to.Gen1 - from.Gen1), Math.Max(0, to.Gen2 - from.Gen2),
        factRows, factSlack, healRows, healSlack, names, estimated,
        Math.Max(0, extras.CastEntries), Math.Max(0, extras.TimedRecords));
    }

    private static long _lastMs;
    private static PerfGc.Reading _previous;
    private static bool _havePrevious;
    private static int _lineCount;

    /*
     * The cadence owner's call: cheap when off (one volatile read), and it never throws — a diagnostics line that can take the parse or a
     * derive pass down with it is worse than no line, so anything unexpected just means "no line this time".
     */
    internal static void MaybeLog(CombatCapture capture, in Extras extras = default)
    {
      if (!PerfJournal.Enabled)
      {
        return;
      }

      var nowMs = Environment.TickCount64;
      if (!Due(nowMs, Volatile.Read(ref _lastMs), _havePrevious))
      {
        return;
      }

      try
      {
        var now = PerfGc.Sample();

        /*
         * The first sample of a capture prints with zero deltas rather than waiting out the interval: its value is the SIZES (working set,
         * rows, slack, kept records), and a person who turned PerfReport on wants to see them as soon as the load settles. A delta needs two readings, so
         * "from" is "now" the first time and every counter in that line reads zero — which is honest, not a missing number.
         */
        var from = _havePrevious ? _previous : now;
        var seconds = _havePrevious ? (nowMs - _lastMs) / 1000.0 : 0;

        var line = Format(Collect(capture, from, now, seconds, extras));
        _previous = now;
        Volatile.Write(ref _lastMs, nowMs);

        /*
         * Last, on purpose. The cadence reads "have we sampled before" and "when", so publishing the timestamp first means a concurrent
         * tick either sees the old state (no line, correct) or the new one with a real timestamp — never "sampled, at second zero", which is
         * how a 100 ms pump would print a heap line every tick. That is exactly what the first version of this did: the flag existed only in
         * the code it replaced, and the test that counts lines is what found it.
         */
        _havePrevious = true;
        Interlocked.Increment(ref _lineCount);
        PerfJournal.Note(line);
      }
      catch (Exception)
      {
        // Deliberately silent, and deliberately not counted: the ledger is the thing that reports on other code, never the thing that
        // breaks it. A missing line is visible in exactly one way (it is missing), which is also how its absence is diagnosed.
      }
    }

    /// <summary>Number of lines this process has written. A cadence that silently stopped logging is otherwise indistinguishable from
    /// "the load was quiet", so the counter is what a test — and a reader comparing two log files — can ask.</summary>
    internal static int LineCount => Volatile.Read(ref _lineCount);

    /// <summary>Forget the baseline — a new capture starts its own trend rather than inheriting the last log's deltas.</summary>
    internal static void Reset()
    {
      _havePrevious = false;
      Volatile.Write(ref _lastMs, 0);
    }

    private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);
  }
}
