#nullable enable annotations

namespace EQLogParser;

/*
 * One summary-board build at a time, and requests that arrive while one is running COLLAPSE into the newest.
 *
 * The expensive door in this application is not the parse, it is a click: materializing a selection allocates one record
 * per selected fact, and a whole-capture select-all measures in seconds (~3.8 s against ~30 ms for one mob; docs →
 * "What makes a board go one pass stale"). Announcements come from several places at once — the command that changed the
 * selection, the menu closing, the settle timer, every derive pass whose content stamp moved — and until now each one
 * spawned its own worker task. Two announcements therefore meant TWO materializations of the same rows: both allocated
 * their full record sets before the builders' own lock serialised them, which is a memory spike on top of double the work,
 * with the grids ending up showing whichever build happened to finish last. "Select all builds the stats twice" is that,
 * and on a live raid with a whole-capture selection it was worse than twice — one per pass, forever.
 *
 * Three laws, in order of who benefits:
 *
 *   1. **Never two at once.** A request that arrives while one runs is queued, not started; the runner drains at most one
 *      follow-up and re-asks, so a burst of announcements costs one extra build rather than N.
 *   2. **Only the newest queued request survives.** An older selection has already been superseded by a newer click; running
 *      it first would only delay the answer that matters (and its records are the ones holding memory down).
 *   3. **A request whose INPUTS match a build that ran to completion (or is running) is dropped.** A build that threw answered
      nothing and is not remembered, because the builders catch their own failures and leave the grids showing last night's
      numbers: remembering the attempt would drop every later retry of that same selection forever. The key carries everything the builders
 *      read — which rows, what the capture has produced (the pane's content stamp: facts + identity verdicts), and which
 *      filters/validation settings the app is on — so "same key" means "the answer is already computed". This is what turns a
 *      duplicated announcement into zero work rather than one second copy. Skipping on anything narrower (ids alone, say)
 *      would leave a board a pass stale, which is the failure mode this pane has been corrected for repeatedly; the key is
 *      therefore handed in by the caller and the gate never guesses at it.
 *
 * Like SelectionSettle and DeriveCadence, this is the rule without the dispatcher in it: the scheduler arrives as a delegate,
 * so a test can run the whole state machine inline (including "the work throws", which must not wedge the gate shut — a latched
 * runner means no board ever updates again, which reads as "the stats froze").
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
