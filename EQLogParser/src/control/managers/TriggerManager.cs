using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal class TriggerManager
  {
    internal event Action EventsProcessorsUpdated;
    internal event Action<bool> EventsUpdatingTriggers;
    internal event Action<TriggerLogEntry> EventsSelectTrigger;
    internal static TriggerManager Instance => Lazy.Value;

    private static readonly Lazy<TriggerManager> Lazy = new(() => new TriggerManager());

    private static readonly object _timerLock = new();
    private Timer _configUpdateTimer;
    private Timer _triggerUpdateTimer;
    private readonly List<LogReader> _logReaders = [];
    private readonly SemaphoreSlim _logReadersSemaphore = new(1, 1);
    private TriggerProcessor _testProcessor;

    public TriggerManager()
    {
      _configUpdateTimer = new Timer(ConfigDoUpdate, null, Timeout.Infinite, Timeout.Infinite);
      _triggerUpdateTimer = new Timer(TriggersDoUpdate, null, Timeout.Infinite, Timeout.Infinite);
      StartTimers();

      TriggerStateDB.Instance.OverlayImportEvent += OverlayImportEvent;
      TriggerStateDB.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
      TriggerStateDB.Instance.TriggerUpdateEvent += TriggerUpdateEvent;
      TriggerStateDB.Instance.TriggerImportEvent += TriggerImportEvent;
    }

    private void StartTimers()
    {
      lock (_timerLock)
      {
        _configUpdateTimer?.Change(500, 500);
        _triggerUpdateTimer?.Change(1000, 1000);
      }
    }

    private void StopTimers()
    {
      lock (_timerLock)
      {
        _configUpdateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _triggerUpdateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
      }
    }

    internal void Select(TriggerLogEntry entry) => EventsSelectTrigger?.Invoke(entry);

    internal void TriggersUpdated()
    {
      EventsUpdatingTriggers?.Invoke(true);
      StopTimers();
      StartTimers();
    }

    internal async Task StartAsync()
    {
      await TriggerUtil.LoadOverlayStyles();
      MainActions.EventsLogLoadingComplete += TriggerManagerEventsLogLoadingComplete;
      StartTimers();
      TriggerConfigUpdateEvent(null);
    }

    internal async Task StopAsync()
    {
      MainActions.EventsLogLoadingComplete -= TriggerManagerEventsLogLoadingComplete;

      // Stop and dispose timers atomically so no callback can fire between stop and dispose
      lock (_timerLock)
      {
        _configUpdateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _triggerUpdateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _configUpdateTimer?.Dispose();
        _triggerUpdateTimer?.Dispose();
        _configUpdateTimer = null;
        _triggerUpdateTimer = null;
      }

      await _logReadersSemaphore.WaitAsync();

      try
      {
        _logReaders.ForEach(reader => reader.Dispose());
        _logReaders.Clear();
        await TriggerOverlayManager.Instance.RemoveAllAsync();
      }
      finally
      {
        _logReadersSemaphore.Release();
      }
    }

    internal async Task StopTriggersAsync()
    {
      var processors = await GetProcessorsAsync();
      foreach (var processor in processors)
      {
        await processor.StopTriggersAsync();
      }

      TriggerOverlayManager.Instance.StopOverlays();
    }

    internal async Task<List<LogReader>> GetLogReadersAsync()
    {
      await _logReadersSemaphore.WaitAsync().ConfigureAwait(false);

      try
      {
        return [.. _logReaders];
      }
      finally
      {
        _logReadersSemaphore.Release();
      }
    }

    internal async Task SetTestProcessor(TriggerConfig config, BlockingCollection<LogReaderItem> collection)
    {
      await InitTestProcessor(TriggerStateDB.DefaultUser, $"Trigger Tester ({TriggerStateDB.DefaultUser})", ConfigUtil.PlayerName,
        config.Voice, config.VoiceRate, -1, null, null, null, null, collection);
    }

    internal async Task SetTestProcessor(TriggerCharacter character, BlockingCollection<LogReaderItem> collection)
    {
      var playerName = !FileUtil.ParseFileName(character.FilePath, out var parsedPlayerName, out _) ? character.Name : parsedPlayerName;
      await InitTestProcessor(character.Id, $"Trigger Tester ({character.Name})", playerName, character.Voice,
        character.VoiceRate, character.CustomVolume, character.ActiveColor, character.IdleColor, character.ResetColor, character.FontColor, collection);
    }

    internal async Task StopTestProcessor()
    {
      // i think this was causing deadlocks on UI thread
      await Task.Run(async () =>
      {
        if (_testProcessor == null) return;
        await _testProcessor.DisposeAsync();
        _testProcessor = null;
      });
    }

    // refresh styles
    private async void OverlayImportEvent(bool obj) => await TriggerUtil.LoadOverlayStyles();
    // in case of merge
    private void TriggerImportEvent(bool _) => TriggersUpdated();

    private async void TriggerUpdateEvent(TriggerNode node)
    {
      // reload triggers if current one is enabled by anyone
      if (await TriggerStateDB.Instance.IsAnyEnabled(node.Id))
      {
        TriggersUpdated();
      }
    }

    private async void TriggerManagerEventsLogLoadingComplete(string file, bool open)
    {
      if (await TriggerStateDB.Instance.GetConfig() is { IsAdvanced: false })
      {
        _ = ConfigDoUpdateWorkAsync();
      }
    }

    private void TriggerConfigUpdateEvent(TriggerConfig _)
    {
      StopTimers();
      StartTimers();
    }

    private async Task InitTestProcessor(string id, string name, string playerName, string voice, int voiceRate,
      int customVolume, string activeColor, string idleColor, string resetColor, string fontColor, BlockingCollection<LogReaderItem> collection)
    {
      if (_testProcessor != null)
      {
        await _testProcessor.DisposeAsync();
      }

      _testProcessor = new TriggerProcessor(id, name, playerName, voice, voiceRate, customVolume, activeColor, idleColor, resetColor, fontColor);
      _testProcessor.SetTesting(true);
      await _testProcessor.StartAsync();
      _testProcessor.LinkTo(collection);
      await FireEventsProcessorsUpdatedAsync();
    }

    private async void ConfigDoUpdate(object state)
    {
      lock (_timerLock)
      {
        // Stop timer before async work
        _configUpdateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
      }

      try
      {
        await ConfigDoUpdateWorkAsync();
      }
      finally
      {
        lock (_timerLock)
        {
          // Restart timer after work completes
          _configUpdateTimer?.Change(500, 500);
        }
      }
    }

    private async Task ConfigDoUpdateWorkAsync()
    {
      if (await TriggerStateDB.Instance.GetConfig() is { } config)
      {
        await _logReadersSemaphore.WaitAsync();

        try
        {
          if (config.IsAdvanced)
          {
            await HandleAdvancedConfig(config);
          }
          else
          {
            await HandleBasicConfig(config);
          }
        }
        finally
        {
          _logReadersSemaphore.Release();
        }

        await FireEventsProcessorsUpdatedAsync();
      }
    }

    private async Task HandleAdvancedConfig(TriggerConfig config)
    {
      // if Default User is being used then we switched from basic so clear all
      if (_logReaders.Any(reader => reader.GetProcessor() is TriggerProcessor { CurrentCharacterId: TriggerStateDB.DefaultUser }))
      {
        _logReaders.ForEach(item => item.Dispose());
        _logReaders.Clear();
      }

      var toRemove = new List<LogReader>();
      var alreadyRunning = new List<string>();
      foreach (var reader in _logReaders)
      {
        if (reader.GetProcessor() is TriggerProcessor processor)
        {
          var found = config.Characters.FirstOrDefault(character => character.Id == processor.CurrentCharacterId &&
            character.Name == processor.CurrentProcessorName);

          if (found is not { IsEnabled: true } || reader.FileName != found.FilePath)
          {
            reader.Dispose();
            toRemove.Add(reader);
            await processor.DisposeAsync();
          }
          else
          {
            processor.SetActiveColor(found.ActiveColor);
            processor.SetIdleColor(found.IdleColor);
            processor.SetResetColor(found.ResetColor);
            processor.SetFontColor(found.FontColor);
            processor.SetVoice(found.Voice);
            processor.SetVoiceRate(found.VoiceRate);
            processor.SetPlayerVolume(found.CustomVolume);
            alreadyRunning.Add(found.Id);
          }
        }
      }

      toRemove.ForEach(remove => _logReaders.Remove(remove));

      // If all readers removed, clear trigger logs
      if (_logReaders.Count == 0)
      {
        TriggerLogManager.Instance.ClearAllOnDisable();
      }

      var startTasks = config.Characters
        .Where(character => character.IsEnabled && !alreadyRunning.Contains(character.Id))
        .Select(async character =>
        {
          if (!string.IsNullOrEmpty(character.FilePath))
          {
            var playerName = !FileUtil.ParseFileName(character.FilePath, out var parsedPlayerName, out _) ? character.Name : parsedPlayerName;
            var processor = new TriggerProcessor(character.Id, character.Name, playerName, character.Voice, character.VoiceRate,
              character.CustomVolume, character.ActiveColor, character.IdleColor, character.ResetColor, character.FontColor);
            await processor.StartAsync();
            var reader = new LogReader(processor, character.FilePath);
            _logReaders.Add(reader);
            _ = reader.StartAsync();
          }
        }).ToList();

      await Task.WhenAll(startTasks);
      if (_logReaders.Count == 0)
      {
        TriggerLogManager.Instance.ClearAllOnDisable();
      }
      MainActions.ShowTriggersEnabled(_logReaders.Count > 0);
    }

    private async Task HandleBasicConfig(TriggerConfig config)
    {
      var currentFile = AppSettings.CurrentLogFile;
      LogReader defReader = null;
      TriggerProcessor defProcessor = null;

      if (_logReaders.Count > 0)
      {
        if (config.IsEnabled && _logReaders[0].GetProcessor() is TriggerProcessor { CurrentCharacterId: TriggerStateDB.DefaultUser } p
          && !string.IsNullOrEmpty(currentFile) && _logReaders[0].FileName == currentFile)
        {
          defReader = _logReaders[0];
          defProcessor = p;
        }
        else
        {
          _logReaders.ForEach(item => item.Dispose());
          _logReaders.Clear();
        }
      }

      if (config.IsEnabled && !string.IsNullOrEmpty(currentFile))
      {
        if (defReader == null || defProcessor == null)
        {
          var processor = new TriggerProcessor(TriggerStateDB.DefaultUser, TriggerStateDB.DefaultUser, ConfigUtil.PlayerName, config.Voice,
            config.VoiceRate, -1, null, null, null, null);
          await processor.StartAsync();
          var reader = new LogReader(processor, currentFile);
          _logReaders.Add(reader);
          _ = reader.StartAsync();
          MainActions.ShowTriggersEnabled(true);
        }
        else
        {
          defProcessor.SetVoice(config.Voice);
          defProcessor.SetVoiceRate(config.VoiceRate);
        }
      }
      else
      {
        MainActions.ShowTriggersEnabled(false);
        TriggerLogManager.Instance.ClearAllOnDisable();
      }
    }

    private async void TriggersDoUpdate(object state)
    {
      lock (_timerLock)
      {
        // Stop timer before async work
        _triggerUpdateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
      }

      try
      {
        await TriggersDoUpdateWorkAsync();
      }
      finally
      {
        lock (_timerLock)
        {
          // Restart timer after work completes
          _triggerUpdateTimer?.Change(1000, 1000);
        }
      }
    }

    private async Task TriggersDoUpdateWorkAsync()
    {
      await Task.Run(async () =>
      {
        var idSet = new HashSet<string>();
        var triggerSet = new HashSet<string>();
        foreach (var processor in await GetProcessorsAsync())
        {
          await processor.UpdateActiveTriggers();
          foreach (var id in processor.GetRequiredOverlayIds())
          {
            idSet.Add(id);
          }

          foreach (var id in await processor.GetEnabledTriggersAsync())
          {
            triggerSet.Add(id);
          }
        }

        await TriggerOverlayManager.Instance.UpdateOverlayInfoAsync(idSet, triggerSet);
        EventsUpdatingTriggers?.Invoke(false);
      });
    }

    private async Task FireEventsProcessorsUpdatedAsync()
    {
      await Task.Run(async () =>
      {
        var idSet = new HashSet<string>();
        var triggerSet = new HashSet<string>();
        foreach (var processor in await GetProcessorsAsync())
        {
          foreach (var id in processor.GetRequiredOverlayIds())
          {
            idSet.Add(id);
          }

          foreach (var id in await processor.GetEnabledTriggersAsync())
          {
            triggerSet.Add(id);
          }
        }

        await TriggerOverlayManager.Instance.UpdateOverlayInfoAsync(idSet, triggerSet);
        EventsProcessorsUpdated?.Invoke();
      });
    }

    private async Task<List<TriggerProcessor>> GetProcessorsAsync()
    {
      await _logReadersSemaphore.WaitAsync();

      try
      {
        var list = _logReaders.Select(reader => reader.GetProcessor()).OfType<TriggerProcessor>().ToList();
        if (_testProcessor != null) list.Add(_testProcessor);
        return list;
      }
      finally
      {
        _logReadersSemaphore.Release();
      }
    }
  }
}
