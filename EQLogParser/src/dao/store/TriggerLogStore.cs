using System.Collections.Generic;
using System.Windows.Data;

namespace EQLogParser
{
  internal class TriggerLogStore
  {
    internal string Name => _name;
    internal BulkObservableCollection<TriggerLogEntry> Entries => _entries;
    private readonly object _lock = new();
    private readonly BulkObservableCollection<TriggerLogEntry> _entries = new(5000);
    private readonly string _name;

    internal TriggerLogStore(string name)
    {
      _name = name;
      BindingOperations.EnableCollectionSynchronization(_entries, _lock);
    }

    internal void AddRange(List<TriggerLogEntry> newEntries)
    {
      lock (_lock)
      {
        _entries.AddRange(newEntries);
      }
    }

    internal void Clear()
    {
      lock (_lock) _entries.Clear();
    }
  }
}
