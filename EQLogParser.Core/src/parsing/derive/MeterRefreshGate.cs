namespace EQLogParser;

// Dispatcher-owned state, with a caller-supplied monotonic clock for deterministic tests.
internal sealed class MeterRefreshGate
{
  internal const double IntervalSeconds = 1;
  private bool _running;
  private bool _pending;
  private bool _forced;
  private double _lastStart = double.NegativeInfinity;

  internal bool Request(double now, bool force = false)
  {
    _pending = true;
    _forced |= force;
    return TryStart(now);
  }

  internal bool Complete(double now)
  {
    _running = false;
    return TryStart(now);
  }

  private bool TryStart(double now)
  {
    if (_running || !_pending || !_forced && now - _lastStart < IntervalSeconds) return false;
    _running = true;
    _pending = false;
    _forced = false;
    _lastStart = now;
    return true;
  }
}
