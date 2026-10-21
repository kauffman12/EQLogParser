namespace EQLogParser;

// Process-lived scalar checkpoint only. A window calculates on a copy, never while holding the command lock.
internal sealed class MeterLifecycleCheckpoint
{
  internal sealed record Work(long Owner, long Revision, DamageOverlayLifecycle State);

  private readonly object _gate = new();
  private DamageOverlayLifecycle _state = new();
  private long _owner;
  private long _revision;

  internal long Open()
  {
    lock (_gate) return ++_owner;
  }

  internal Work Read(long owner)
  {
    lock (_gate) return new Work(owner, _revision, _state.Copy());
  }

  internal bool TryCommit(Work work)
  {
    lock (_gate)
    {
      if (work.Owner != _owner || work.Revision != _revision) return false;
      _state = work.State;
      _revision++;
      return true;
    }
  }

  internal void Reset(int sessionId, int capturedCount)
  {
    lock (_gate)
    {
      _revision++;
      _state.Reset(sessionId, capturedCount);
    }
  }
}
