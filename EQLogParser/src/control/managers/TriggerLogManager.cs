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

    /*
     * Trigger log traffic, on the heartbeat alongside the window that displays it (TriggersLogView). One AddRange is one whole-collection
     * Reset to anything bound, so the batch count is how often a visible grid must rebuild and re-sort itself, and the entry count is what
     * it has to sort — read together they separate "triggers fired a lot" from "the grid reloads too often for what fired". Counted off the
     * UI thread, because that is where the trigger processor appends.
     */
    private static readonly int LogBatchId = PerfCounters.Register("trig.logBatch", uiThread: false);
    private static readonly int LogEntryId = PerfCounters.Register("trig.logEntry", uiThread: false);
    private readonly HashSet<string> _activeProcessors = [];
    private readonly Dictionary<string, BulkObservableCollection<TriggerLogEntry>> _logs = [];
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

      // Collection should already exist via EnsureCollection called in TriggerProcessor.StartAsync
      // This check is defensive in case of future code changes
      if (!_logs.TryGetValue(characterId, out var log))
      {
        return;
      }

      PerfCounters.Note(LogBatchId);
      PerfCounters.Note(LogEntryId, entries.Count);

      lock (log)
      {
        log.AddRange(entries);
      }
    }

    /// <summary>
    /// Ensures a log collection exists for the given processor/character.
    /// Called by TriggerProcessor when it starts.
    /// </summary>
    internal void EnsureCollection(string processorName)
    {
      lock (_globalLock)
      {
        if (!_logs.ContainsKey(processorName))
        {
          var log = new BulkObservableCollection<TriggerLogEntry>(MAX_ENTRIES_PER_CHARACTER);
          BindingOperations.EnableCollectionSynchronization(log, _globalLock);
          _logs[processorName] = log;
        }
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

    /// <summary>
    /// Clears all trigger logs. Should only be called from user-initiated clear action.
    /// </summary>
    internal void ClearAll()
    {
      // Snapshot entries under global lock, then clear each collection under its own lock
      List<KeyValuePair<string, BulkObservableCollection<TriggerLogEntry>>> entries;
      lock (_globalLock)
      {
        entries = new List<KeyValuePair<string, BulkObservableCollection<TriggerLogEntry>>>(_logs);
      }

      foreach (var kvp in entries)
      {
        lock (kvp.Value)
        {
          kvp.Value.Clear();
        }
      }
    }

    /// <summary>
    /// Clears all trigger logs and removes character entries.
    /// Called when triggers are disabled to fully clean up.
    /// </summary>
    internal void ClearAllOnDisable()
    {
      // Snapshot entries under global lock, clear each collection under its own lock,
      // then wipe the dictionary so any new AddRange creates fresh collections.
      List<KeyValuePair<string, BulkObservableCollection<TriggerLogEntry>>> entries;
      lock (_globalLock)
      {
        entries = new List<KeyValuePair<string, BulkObservableCollection<TriggerLogEntry>>>(_logs);
      }

      foreach (var kvp in entries)
      {
        lock (kvp.Value)
        {
          kvp.Value.Clear();
        }
      }

      lock (_globalLock)
      {
        _logs.Clear();
      }
    }
  }
}
