using System;
using System.Threading;

namespace EQLogParser
{
  internal sealed class DelayedAction : IDisposable
  {
    private readonly DelayedAction<object> _inner;

    internal DelayedAction(TimeSpan interval, Action action)
    {
      _inner = new DelayedAction<object>(interval, _ => action());
    }

    internal void Invoke()
    {
      _inner.Invoke(null);
    }

    public void Dispose()
    {
      _inner.Dispose();
    }
  }

  internal sealed class DelayedAction<T>(TimeSpan interval, Action<T> action) : IDisposable
  {
    private readonly object _lock = new();
    private readonly TimeSpan _interval = interval;
    private readonly Action<T> _action = action;

    private Timer _timer;
    private bool _scheduled;
    private bool _disposed;
    private T _pendingValue;

    public void Invoke(T value)
    {
      lock (_lock)
      {
        if (_disposed || _scheduled)
          return;

        _scheduled = true;
        _pendingValue = value;
        _timer = new Timer(OnTimer, null, _interval, Timeout.InfiniteTimeSpan);
      }
    }

    private void OnTimer(object state)
    {
      T value;

      lock (_lock)
      {
        if (_disposed)
          return;

        value = _pendingValue;
        _scheduled = false;
        _timer?.Dispose();
        _timer = null;
      }

      _action(value);
    }

    public void Dispose()
    {
      lock (_lock)
      {
        _disposed = true;
        _scheduled = false;
        _timer?.Dispose();
        _timer = null;
      }
    }
  }
}
