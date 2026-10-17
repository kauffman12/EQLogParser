using System;
using System.Threading;

#nullable enable annotations

namespace EQLogParser;

// Mutations belong to the dispatcher. A queued request carries an instance, never "whatever is current later".
internal sealed class WindowInstanceSlot<T>(Action<T> close) where T : class
{
  private T? _current;
  internal T? Current => Volatile.Read(ref _current);

  internal void Replace(T? next)
  {
    var previous = _current;
    if (ReferenceEquals(previous, next)) return;
    _current = next;
    if (previous is not null) close(previous);
  }

  internal bool Close(T? expected)
  {
    if (!Forget(expected)) return false;
    close(expected);
    return true;
  }

  internal bool Forget(T? expected)
  {
    if (expected is null || !ReferenceEquals(_current, expected)) return false;
    _current = null;
    return true;
  }
}
