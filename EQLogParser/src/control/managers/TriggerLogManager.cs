using System;
using System.Collections.Generic;
using System.Windows.Data;

namespace EQLogParser
{
  internal class TriggerLogManager
  {
    private static readonly Lazy<TriggerLogManager> Lazy = new(() => new TriggerLogManager());
    internal static TriggerLogManager Instance => Lazy.Value;

    private const int MAX_ENTRIES_PER_CHARACTER = 5000;
    private readonly HashSet<string> _activeProcessors = [];
    private readonly Dictionary<string, BulkObservableCollection<TriggerLogEntry>> _logs = [];
    private readonly Dictionary<string, object> _characterLocks = [];
    private readonly object _globalLock = new();

    /// <summary>
    /// Registers the set of active processors so GetLogs() knows which names to show,
    /// even before any triggers fire.
    /// </summary>
    internal void SetActiveProcessors(HashSet<string> processorNames)
    {
      lock (_globalLock)
      {
        _activeProcessors.Clear();
        _activeProcessors.UnionWith(processorNames);
      }
    }

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
          log = new BulkObservableCollection<TriggerLogEntry>(MAX_ENTRIES_PER_CHARACTER);
          BindingOperations.EnableCollectionSynchronization(log, charLock);
          _logs[characterId] = log;
        }

        log.AddRange(entries);
      }
    }

    internal IReadOnlyDictionary<string, BulkObservableCollection<TriggerLogEntry>> GetLogs(out HashSet<string> activeProcessors)
    {
      lock (_globalLock)
      {
        activeProcessors = new HashSet<string>(_activeProcessors);
        return new Dictionary<string, BulkObservableCollection<TriggerLogEntry>>(_logs);
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
