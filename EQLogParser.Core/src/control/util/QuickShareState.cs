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

    /* Attaches a view owner (the WPF window's bound collection) without losing history: the records
     * accepted so far are handed to the handler, then it receives every later one. Both steps happen
     * under one lock so nothing can slip through between the replay and the subscription — a view
     * that is constructed lazily (a GINA share seen long before the Quick Share window is opened)
     * used to miss everything accepted before its construction. Handlers run on the accepting
     * thread, oldest-replay-first, so a handler that inserts at index 0 ends up newest-first; it is
     * the subscriber's job to marshal to the UI thread. */
    internal void Subscribe(Action<QuickShareRecord> handler)
    {
      if (handler == null) return;

      List<QuickShareRecord> replay;
      lock (_lock)
      {
        replay = [.. _records];
        Accepted += handler;
      }

      // outside the lock, like Accepted itself, so a handler may re-enter (IsMine, Add)
      for (var i = replay.Count - 1; i >= 0; i--)
      {
        handler(replay[i]);
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
