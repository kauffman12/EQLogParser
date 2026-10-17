using System;
using System.Threading;

namespace EQLogParser;

// Plain expiry data only: timer ticks can retire a presentation without waiting for its worker.
internal sealed class MeterPresentationState
{
  private sealed record Lease(int Session, int Version, double ExpiresAt);
  private Lease _lease;

  internal void Publish(int session, int version, double expiresAt) =>
    Volatile.Write(ref _lease, new Lease(session, version, expiresAt));

  internal bool IsFresh(int session, int version, double now)
  {
    var lease = Volatile.Read(ref _lease);
    return lease is not null && lease.Session == session && lease.Version == version
      && double.IsFinite(lease.ExpiresAt) && now <= lease.ExpiresAt;
  }

  internal void Paint(Action paint, Action hide)
  {
    try
    {
      paint();
    }
    catch
    {
      Clear();
      hide();
      throw;
    }
  }

  internal void Clear() => Volatile.Write(ref _lease, null);
}
