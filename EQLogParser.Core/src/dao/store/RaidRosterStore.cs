using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  internal class RaidRosterStore : ILifecycle, IDisposable
  {
    internal static event Action EventsRosterUpdated;

    // singleton
    internal static RaidRosterStore Instance { get; } = new();

    private static readonly TimeSpan DebounceTime = TimeSpan.FromSeconds(2);
    private readonly Dictionary<double, WhoRosterRecord> _rosterRecords = [];
    private readonly object _lock = new();
    private DelayedAction _update = new(DebounceTime, () => EventsRosterUpdated?.Invoke());
    private WhoRosterRecord _currentRecord;
    private long _lastCaptureTime;

    private RaidRosterStore()
    {
      LifecycleManager.Register(this);
    }

    internal void CapturePlayer(string name, int group, double beginTime)
    {
      var dateTime = DateUtil.FromDotNetSeconds(beginTime);
      var ticks = dateTime.Ticks;

      lock (_lock)
      {
        if (_currentRecord == null || (ticks - _lastCaptureTime) > DebounceTime.Ticks)
        {
          var record = new WhoRosterRecord
          {
            BeginTicks = ticks,
            Players = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
              [name] = group
            }
          };

          _rosterRecords[record.BeginTicks] = record;
          _currentRecord = record;
          _update?.Invoke();
        }
        else
        {
          _currentRecord.Players[name] = group;
        }

        _lastCaptureTime = ticks;
      }
    }

    internal void FlushCurrentGroup()
    {
      lock (_lock)
      {
        _currentRecord = null;
        _lastCaptureTime = 0;
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
      if (serverChanged)
      {
        lock (_lock)
        {
          _rosterRecords.Clear();
          _currentRecord = null;
          _lastCaptureTime = 0;
        }
      }
    }

    public void Shutdown()
    {
      lock (_lock)
      {
        _rosterRecords.Clear();
        _currentRecord = null;
        _lastCaptureTime = 0;

        _update.Dispose();
        _update = null;
      }
    }

    public void Dispose() => Shutdown();
  }
}
