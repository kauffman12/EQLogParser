// Annotations only; a pending cleanup can legitimately be absent.
#nullable enable annotations

namespace EQLogParser;

/// <summary>A cleanup belongs to one capture and one arm, even after a worker has queued it for the dispatcher.</summary>
internal sealed class CaptureTidyGate
{
  internal sealed record Ticket(int SessionId, long Generation);

  private readonly object _gate = new();
  private Ticket? _current;
  private long _generation;
  private bool _pending;

  internal Ticket Arm(int sessionId)
  {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessionId);
    lock (_gate)
    {
      _current = new Ticket(sessionId, ++_generation);
      _pending = true;
      return _current;
    }
  }

  internal bool TryTake(int sessionId, out Ticket? ticket)
  {
    lock (_gate)
    {
      ticket = null;
      if (!_pending || _current?.SessionId != sessionId) return false;
      _pending = false;
      ticket = _current;
      return true;
    }
  }

  internal bool IsCurrent(Ticket ticket, int sessionId)
  {
    lock (_gate)
    {
      return ticket == _current && ticket.SessionId == sessionId;
    }
  }

  internal void Invalidate()
  {
    lock (_gate)
    {
      _current = null;
      _pending = false;
    }
  }
}
