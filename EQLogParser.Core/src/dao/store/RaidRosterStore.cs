namespace EQLogParser
{
  internal class RaidRosterStore : ILifecycle, IDisposable
  {
    internal static event Action<long> EventsRosterUpdated;

    // singleton
    internal static RaidRosterStore Instance { get; } = new();

    private static readonly TimeSpan ListingGap = TimeSpan.FromSeconds(2);
    private readonly Dictionary<double, WhoRosterRecord> _rosterRecords = [];
    private readonly object _lock = new();
    private WhoRosterRecord _currentRecord;
    private long _lastCaptureTime;
    private long _captureVersion = 1;

    private RaidRosterStore()
    {
      LifecycleManager.Register(this);
    }

    internal long CaptureVersion
    {
      get { lock (_lock) return _captureVersion; }
    }

    internal void CapturePlayer(long captureVersion, string name, int group, double beginTime)
    {
      var dateTime = DateUtil.FromDotNetSeconds(beginTime);
      var ticks = dateTime.Ticks;

      lock (_lock)
      {
        if (captureVersion != _captureVersion) return;
        if (_currentRecord == null || (ticks - _lastCaptureTime) > ListingGap.Ticks)
        {
          var record = new WhoRosterRecord
          {
            BeginTicks = ticks,
            Players = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
              [name] = group
            }
          };

          _currentRecord = record;
        }
        else
        {
          _currentRecord.Players[name] = group;
        }

        _lastCaptureTime = ticks;
      }
    }

    internal void FlushCurrentGroup(long captureVersion, uint playerCount)
    {
      lock (_lock)
      {
        if (captureVersion != _captureVersion) return;
        var record = _currentRecord;
        _currentRecord = null;
        _lastCaptureTime = 0;

        // A read can start inside a listing; the footer alone does not prove we saw all its rows.
        if (record == null || record.Players.Count != playerCount) return;
        _rosterRecords[record.BeginTicks] = record;
      }

      foreach (Action<long> subscriber in EventsRosterUpdated?.GetInvocationList() ?? [])
      {
        ThreadBoundary.Guard("raid roster subscriber", () => subscriber(captureVersion));
      }
    }

    internal List<WhoRosterRecord> GetRosterRecords()
    {
      lock (_lock)
      {
        return [.. _rosterRecords.Values.OrderByDescending(r => r.BeginTicks)];
      }
    }

    public void Clear(bool serverChanged = true)
    {
      lock (_lock)
      {
        // The old reader may still be parsing when the lifecycle clears these stores.
        _captureVersion++;
        if (serverChanged)
        {
          _rosterRecords.Clear();
        }

        _currentRecord = null;
        _lastCaptureTime = 0;
      }
    }

    public void Shutdown() => Clear();

    public void Dispose() => Shutdown();
  }
}
