using System.Collections.ObjectModel;
using System.Windows.Data;

namespace EQLogParser
{
  internal class TriggerLogStore
  {
    internal object SyncRoot => _lock;
    internal string Name => _name;
    internal ObservableCollection<TriggerLogEntry> Entries => _entries;
    private readonly object _lock = new();
    private readonly ObservableCollection<TriggerLogEntry> _entries = [];
    private readonly string _name;

    internal TriggerLogStore(string name)
    {
      _name = name;
      BindingOperations.EnableCollectionSynchronization(_entries, _lock);
    }

    internal void Add(TriggerLogEntry entry)
    {
      lock (_lock)
      {
        _entries.Insert(0, entry);
        if (_entries.Count > 5000)
        {
          _entries.RemoveAt(_entries.Count - 1);
        }
      }
    }

    internal void Clear()
    {
      lock (_lock) _entries.Clear();
    }
  }
}
