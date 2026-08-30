using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  /* Thread-safe quick-share history rules shared by every producer (GINA chat detection, legacy
   * share flow) and the WPF window, which keeps its bound ObservableCollection in sync. The
   * dedup/ownership logic lives here so it is testable without WPF; only view updates are
   * marshaled to the UI thread (by the adapter). */
  internal class QuickShareState
  {
    internal static QuickShareState Instance { get; } = new();

    private readonly List<QuickShareRecord> _records = [];
    private readonly object _lock = new();

    // Raised on the accepting thread after a record is inserted, so view owners (e.g. the WPF
    // window's bound collection) can mirror it. Fires at most once per unique record.
    internal event Action<QuickShareRecord> Accepted;

    // Applies the insert-once-at-top rule. Returns true when the record was added, so view owners
    // can mirror it into their own collections.
    internal bool Add(QuickShareRecord record)
    {
      var added = false;
      lock (_lock)
      {
        if (_records.Count == 0 || _records[0].Key != record.Key || _records[0].BeginTime != record.BeginTime)
        {
          _records.Insert(0, record);
          added = true;
        }
      }

      // Raised outside the lock so view-side handlers can re-enter the state (IsMine, Add).
      if (added)
      {
        Accepted?.Invoke(record);
      }

      return added;
    }

    internal bool IsMine(string key)
    {
      lock (_lock)
      {
        return _records.Any(r => r.IsMine && r.Key == key);
      }
    }

    // Point-in-time copy for any-thread readers that don't bind the WPF collection (tests, the
    // share-status lookup in TriggerUtil).
    internal List<QuickShareRecord> Snapshot()
    {
      lock (_lock)
      {
        return [.. _records];
      }
    }
  }
}
