namespace EQLogParser;

// Dispatcher-owned: one index build, one coalesced debt. Abandon never releases a worker that is still running.
internal sealed class NamesViewBuildLane
{
  private bool _running;
  private bool _owed;

  internal bool TryBegin()
  {
    if (_running)
    {
      _owed = true;
      return false;
    }

    _running = true;
    _owed = false;
    return true;
  }

  internal bool Complete()
  {
    _running = false;
    var owed = _owed;
    _owed = false;
    return owed;
  }

  internal void Abandon() => _owed = false;
}
