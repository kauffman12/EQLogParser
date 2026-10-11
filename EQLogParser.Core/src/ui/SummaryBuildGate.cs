#nullable enable annotations

namespace EQLogParser;

// One materialization at a time; only the newest waiting question survives. A completed key is reusable only while idle:
// another running build will replace its answer. Work must report success, including failures caught inside a board builder.
internal sealed class SummaryBuildGate(Action<Action> schedule)
{
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
    schedule(() => Run(key, work));
    return Outcome.Started;
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
      if (next is { } n) schedule(() => Run(n.Key, n.Work));
    }
  }
}
