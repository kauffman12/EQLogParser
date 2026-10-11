using log4net;
using System.Reflection;

#nullable enable annotations

namespace EQLogParser;

// One materialization at a time; only the newest waiting question survives. A completed key is reusable only while idle:
// another running build will replace its answer. Work must report success, including failures caught inside a board builder.
internal sealed class SummaryBuildGate(Action<Action> schedule)
{
  // The same per-class log4net field every Core class carries — there is no global logger type in this application.
  private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

  internal enum Outcome { Started, Queued, SkippedSame }

  private readonly object _sync = new();
  private long? _runningKey;
  private long? _doneKey;
  private (long Key, Func<bool> Work)? _pending;
  private long _collapsed;

  internal long CollapsedCount => Interlocked.Read(ref _collapsed);
  internal bool IsRunning { get { lock (_sync) return _runningKey.HasValue; } }

  internal Outcome Request(long key, Func<bool> work)
  {
    lock (_sync)
    {
      if (key == _runningKey || (!_runningKey.HasValue && key == _doneKey))
      {
        if (_pending is not null) Interlocked.Increment(ref _collapsed);
        _pending = null;
        Interlocked.Increment(ref _collapsed);
        return Outcome.SkippedSame;
      }
      if (_runningKey.HasValue)
      {
        if (_pending is not null) Interlocked.Increment(ref _collapsed);
        _pending = (key, work);
        return Outcome.Queued;
      }
      _runningKey = key;
    }
    HandOff(key, work, rethrowToCaller: true);
    return Outcome.Started;
  }

  /*
   * The one place this class gives its work to somebody else's thread, and the only way it can latch.
   *
   * Everything that happens INSIDE a build is already covered — Run's finally clears the running key whether the work answered true,
   * false or a throw. But if the hand-off itself fails (a scheduler refusing work during shutdown, an TaskScheduler whose queue is gone),
   * no Run will ever arrive to run that finally: _runningKey stays set forever, every later announcement queues behind a build that does
   * not exist, and at the far end that is indistinguishable from "the stats froze" — the same latched-flag failure the timer overlay's
   * _isRendering rule is about. So the state goes back the way it came. A completed answer is dropped with it: nothing was built, and the
   * gate must not keep calling a stale grid reusable because an attempt never left the caller.
   *
   * `rethrowToCaller` differs by caller on purpose. From Request there is no other exception in flight, so the caller hears about it. From
   * Run's finally a throw here would REPLACE whatever the build was already throwing, and the reason a board failed outranks the reason its
   * follow-up could not start — that path reports on the log and lets the build's own exception travel.
   *
   * `started` is what makes this method safe with a scheduler that runs inline (the tests do; production hands work to Task.Run). Without it,
   * an inline build that throws arrives through this same catch, and treating that as "the scheduler refused" would both reset state the build
   * already settled correctly and — worse — swallow the exception, breaking the standing law that a throwing build reaches whoever pumped it.
   */
  private void HandOff(long key, Func<bool> work, bool rethrowToCaller)
  {
    var started = false;
    try
    {
      schedule(() => { started = true; Run(key, work); });
    }
    catch (Exception ex)
    {
      // Only a hand-off that never reached Run can latch the gate.
      if (!started) RefuseHandOff(key, ex);
      else if (!rethrowToCaller)
        Log.Warn($"summary build: follow-up build #{key} threw on a scheduler that runs work inline", ex);

      if (rethrowToCaller) throw;
    }
  }

  /*
   * Put the state back the way it was and say so. The dropped ask is named rather than resurrected: keeping it in _pending would be worse
   * than losing it, because a later Request of the SAME key collapses against a pending item (SkippedSame) and nothing builds — an ask
   * silently eaten is the exact failure this class exists to prevent. With `_doneKey` cleared no answer is marked reusable, so any gesture
   * that asks again rebuilds.
   */
  private void RefuseHandOff(long key, Exception ex)
  {
    lock (_sync)
    {
      if (_runningKey == key) { _runningKey = null; _doneKey = null; _pending = null; }
    }

    Log.Warn($"summary build: the scheduler refused build #{key}; the gate is idle again and nothing is marked answered", ex);
  }

  private void Run(long key, Func<bool> work)
  {
    var answered = false;
    try
    {
      answered = work();
    }
    finally
    {
      (long Key, Func<bool> Work)? next;
      lock (_sync)
      {
        // A failed build may have painted some boards, so even the previous successful answer is no longer reusable.
        _doneKey = answered ? key : null;
        next = _pending;
        _pending = null;
        _runningKey = next?.Key;
      }
      // Never rethrow here: a throw inside this finally would REPLACE whatever the build was already carrying, and the reason a board
      // failed is the thing a reader needs. Refusals and an inline successor's own throw are reported on the log instead.
      if (next is { } n) HandOff(n.Key, n.Work, rethrowToCaller: false);
    }
  }
}
