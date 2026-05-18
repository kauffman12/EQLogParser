using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Data;

namespace EQLogParser
{
  internal class TriggerLogManager
  {
    internal static TriggerLogManager Instance { get; set; } = new();

    private const int MAX_ENTRIES_PER_CHARACTER = 5000;

    private readonly Dictionary<string, ObservableCollection<TriggerLogEntry>> _logs = new();
    private readonly Dictionary<string, object> _characterLocks = new();
    private readonly object _globalLock = new();

    internal void Add(string characterId, TriggerLogEntry entry)
    {
      AddRange(characterId, new List<TriggerLogEntry> { entry });
    }

    internal void AddRange(string characterId, List<TriggerLogEntry> entries)
    {
      if (entries == null || entries.Count == 0)
      {
        return;
      }

      object charLock;
      lock (_globalLock)
      {
        if (!_characterLocks.TryGetValue(characterId, out charLock))
        {
          charLock = new object();
          _characterLocks[characterId] = charLock;
        }
      }

      lock (charLock)
      {
        if (!_logs.TryGetValue(characterId, out var log))
        {
          log = new ObservableCollection<TriggerLogEntry>();
          BindingOperations.EnableCollectionSynchronization(log, charLock);
          _logs[characterId] = log;
        }

        // Enforce max size before adding new entries
        int excess = log.Count + entries.Count - MAX_ENTRIES_PER_CHARACTER;
        if (excess > 0)
        {
          // Remove oldest entries (at the end since newest are inserted at 0)
          for (int i = 0; i < excess; i++)
          {
            log.RemoveAt(log.Count - 1);
          }
        }

        // Insert new entries at the beginning (newest first)
        foreach (var entry in entries)
        {
          log.Insert(0, entry);
        }
      }
    }

    internal IReadOnlyDictionary<string, ObservableCollection<TriggerLogEntry>> GetLogs()
    {
      lock (_globalLock)
      {
        return new Dictionary<string, ObservableCollection<TriggerLogEntry>>(_logs);
      }
    }

    internal void ClearCharacter(string characterId)
    {
      object charLock;
      lock (_globalLock)
      {
        if (!_characterLocks.TryGetValue(characterId, out charLock))
        {
          return;
        }
      }

      lock (charLock)
      {
        if (_logs.TryGetValue(characterId, out var log))
        {
          log.Clear();
        }
      }
    }

    /// <summary>
    /// Clears all trigger logs. Should only be called from user-initiated clear action.
    /// </summary>
    internal void ClearAll()
    {
      // Snapshot lock entries under global lock, then clear each collection under its own lock
      List<KeyValuePair<string, object>> entries;
      lock (_globalLock)
      {
        entries = new List<KeyValuePair<string, object>>(_characterLocks);
      }

      foreach (var kvp in entries)
      {
        lock (kvp.Value)
        {
          if (_logs.TryGetValue(kvp.Key, out var log))
          {
            log.Clear();
          }
        }
      }
    }

    /// <summary>
    /// Clears all trigger logs and removes character entries.
    /// Called when triggers are disabled to fully clean up.
    /// </summary>
    internal void ClearAllOnDisable()
    {
      // Snapshot lock entries under global lock, clear each collection under its own lock,
      // then wipe the dictionaries so any new AddRange creates fresh collections.
      List<KeyValuePair<string, object>> entries;
      lock (_globalLock)
      {
        entries = new List<KeyValuePair<string, object>>(_characterLocks);
      }

      foreach (var kvp in entries)
      {
        lock (kvp.Value)
        {
          if (_logs.TryGetValue(kvp.Key, out var log))
          {
            log.Clear();
          }
        }
      }

      lock (_globalLock)
      {
        _logs.Clear();
        _characterLocks.Clear();
      }
    }
  }
}
