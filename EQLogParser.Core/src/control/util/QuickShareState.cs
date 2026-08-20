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

    /// <summary>Applies the insert-once-at-top rule. Returns true when the record was added, so
    /// view owners can mirror it into their own collections.</summary>
    internal bool Add(QuickShareRecord record)
    {
      lock (_lock)
      {
        if (_records.Count == 0 || _records[0].Key != record.Key || _records[0].BeginTime != record.BeginTime)
        {
          _records.Insert(0, record);
          return true;
        }

        return false;
      }
    }

    internal bool IsMine(string key)
    {
      lock (_lock)
      {
        return _records.FirstOrDefault(r => r.IsMine && r.Key == key) != null;
      }
    }
  }
}
