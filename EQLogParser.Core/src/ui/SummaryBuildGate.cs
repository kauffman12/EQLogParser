#nullable enable annotations

namespace EQLogParser;

/*
 * One summary-board build at a time, and requests that arrive while one is running COLLAPSE into the newest.
 *
 * The expensive door here is a click, not the parse: materializing a selection allocates one record per selected fact, and a
 * whole-capture select-all measures in seconds (~3.8 s against ~30 ms for one mob). Announcements come from several doors at
 * once — selection command, menu close, settle timer, every derive pass whose content stamp moved — so without this gate two
 * announcements meant two full materializations of the same rows, serialized by the builders' lock only AFTER both had allocated,
 * with the grids showing whichever finished last. (docs/DesignNotes.md → "What makes a board go one pass stale".)
 *
 * Three laws:
 *
 *   1. **Never two at once.** A request arriving mid-build is queued, not started; the runner drains at most one follow-up, so
 *      a burst costs one extra build rather than N.
 *   2. **Only the newest queued request survives — including when the newest one is free.** A request that something already ran
 *      (or running) answers also DROPS whatever waits: keeping it would paint an abandoned selection over the grid afterwards.
 *   3. **A request whose INPUTS match a build that completed (or is running) is dropped.** The key carries everything the builders
 *      read — rows, content stamp, filters, and which capture the question came from — so "same key" means "already computed".
 *      Anything narrower leaves a board a pass stale. The caller builds the key; the gate never guesses at it.
 *
 * A build that THREW answered nothing and is not remembered: the builders catch their own failures and leave the grids showing
 * what they had, so remembering the attempt would drop every later retry of that selection forever. Dedupe on what was computed,
 * not on what was attempted.
 *
 * Like SelectionSettle and DeriveCadence, this is the rule without the dispatcher in it — the scheduler arrives as a delegate, so
 * tests run the whole state machine inline, including "the work throws" (a latched runner reads as "the stats froze").
 */
internal sealed class SummaryBuildGate(Action<Action> schedule)
{
  /// <summary>What a request became. Callers log it; nothing branches on it.</summary>
  internal enum Outcome
  {
    /// <summary>Nothing was running and nothing answered this key: the work started now.</summary>
    Started,

    /// <summary>A build is in flight; this one waits (and replaces any older waiting request).</summary>
    Queued,

    /// <summary>The running or last completed build already had these inputs — no work was done.</summary>
    SkippedSame,
  }

  private readonly object _sync = new();

  private long _runningKey = NoKey;
  private long _doneKey = NoKey;
  private (long Key, Action Work)? _pending;
  private long _collapsed;

  // A key no caller can produce: materialization input keys are built from ids, stamps and settings, and callers use
  // unchecked arithmetic over them — 0 stays unused so "never built" is distinguishable from a real key of 0.
  private const long NoKey = 0;

  /// <summary>How many requests have been dropped as superseded or already-answered since construction.</summary>
  internal long CollapsedCount => Interlocked.Read(ref _collapsed);

  /// <summary>A build is in flight. Tests and diagnostics ask; the gate decides from its own state.</summary>
  internal bool IsRunning { get { lock (_sync) return _runningKey != NoKey; } }

  /*
   * Ask for the boards to be rebuilt from `key`'s inputs. Returns what happened; the work itself runs on whatever
   * scheduler this gate was built with (production: Task.Run; tests: inline).
   */
  internal Outcome Request(long key, Action work)
  {
    Action? start = null;
    Outcome outcome;

    lock (_sync)
    {
      if (key == _runningKey || key == _doneKey)
      {
        /*
         * A free answer drops the queue too (law 2): this IS the newest request, so anything waiting describes a selection the
         * operator walked away from. Without it, select-A → drag-to-B → click-back-on-A ended with B's boards painted over A's grid,
         * and no gesture short of another click removed it.
         *
         * NOT covered here: a DIFFERENT build already in flight (A done → B running → select A). Nothing un-starts B, so the answer is
         * result-side — MainWindow.BuildBoards abandons a build whose capture is no longer the one open.
         */
        if (_pending is { } queued && queued.Key != key)
        {
          _pending = null;
          Interlocked.Increment(ref _collapsed);
        }

        Interlocked.Increment(ref _collapsed);
        outcome = Outcome.SkippedSame;
      }
      else if (_runningKey == NoKey)
      {
        _runningKey = key;
        start = () => schedule(() => Run(key, work));
        outcome = Outcome.Started;
      }
      else
      {
        if (_pending is not null) Interlocked.Increment(ref _collapsed);
        _pending = (key, work);   // newest wins: an older request describes a selection nobody is looking at any more
        outcome = Outcome.Queued;
      }
    }

    // Outside the lock: scheduling must not run work (or take another lock) while holding this one.
    start?.Invoke();
    return outcome;
  }

  private void Run(long key, Action work)
  {
    var answered = false;
    try
    {
      work();
      answered = true;
    }
    finally
    {
      // A throw must return the runner to idle: a latch left here means every later request queues behind a build that
      // will never finish, and the boards stop updating for the rest of the session.
      //
      // But a build that threw answered NOTHING, so it may not be remembered as an answer either. `_doneKey` is what makes an
      // identical later ask free - and the callers' builders swallow their own exceptions (a board build catches, logs
      // "Derived damage summary error", and leaves the panes showing their previous figures), so a failure arrives here as a
      // normal return of the CALL but not of the question. Marking it done meant: one failed select-all, then every later click
      // that returns to that same selection - a second later or ten minutes later, with nothing else having moved - is dropped as
      // already answered and the boards never come back. Law: dedupe on what was COMPUTED, not on what was attempted.
      (long Key, Action Work)? next = null;
      lock (_sync)
      {
        _runningKey = NoKey;
        if (answered) _doneKey = key;
        if (_pending is { } p)
        {
          _pending = null;
          if (p.Key != key)   // what just completed already answers it (same key) — nothing to re-run
          {
            _runningKey = p.Key;
            next = p;
          }
        }
      }

      if (next is { } n) schedule(() => Run(n.Key, n.Work));
    }
  }
}
