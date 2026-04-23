using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace EQLogParser
{
  internal class TriggerManager
  {
    internal event Action<List<TriggerLogStore>> EventsProcessorsUpdated;
    internal event Action<bool> EventsUpdatingTriggers;
    internal event Action<TriggerLogEntry> EventsSelectTrigger;
    internal static TriggerManager Instance => Lazy.Value;

    private static readonly Lazy<TriggerManager> Lazy = new(() => new TriggerManager());
    private readonly DispatcherTimer _configUpdateTimer;
    private readonly DispatcherTimer _triggerUpdateTimer;
    private readonly List<LogReader> _logReaders = [];
    private readonly SemaphoreSlim _logReadersSemaphore = new(1, 1);
    private TriggerProcessor _testProcessor;

    public TriggerManager()
    {
      _configUpdateTimer = UiUtil.CreateTimer(ConfigDoUpdate, 500, false);
      _triggerUpdateTimer = UiUtil.CreateTimer(TriggersDoUpdate, 1000, false);
      TriggerStateDB.Instance.OverlayImportEvent += OverlayImportEvent;
      TriggerStateDB.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
      TriggerStateDB.Instance.TriggerUpdateEvent += TriggerUpdateEvent;
      TriggerStateDB.Instance.TriggerImportEvent += TriggerImportEvent;
    }

    internal void Select(TriggerLogEntry entry) => EventsSelectTrigger?.Invoke(entry);

    internal void TriggersUpdated()
    {
      EventsUpdatingTriggers?.Invoke(true);
      _triggerUpdateTimer.Stop();
      _triggerUpdateTimer.Start();
    }

    internal async Task StartAsync()
    {
      await TriggerUtil.LoadOverlayStyles();
      MainActions.EventsLogLoadingComplete += TriggerManagerEventsLogLoadingComplete;
      TriggerConfigUpdateEvent(null);
    }

    internal async Task StopAsync()
    {
      MainActions.EventsLogLoadingComplete -= TriggerManagerEventsLogLoadingComplete;
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
        ConfigDoUpdate(this, null);
      }
    }

    private void TriggerConfigUpdateEvent(TriggerConfig _)
    {
      _configUpdateTimer.Stop();
      _configUpdateTimer.Start();
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

    private async void ConfigDoUpdate(object sender, EventArgs e)
    {
      _configUpdateTimer.Stop();

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
      MainActions.ShowTriggersEnabled(_logReaders.Count > 0);
    }

    private async Task HandleBasicConfig(TriggerConfig config)
    {
      var currentFile = MainWindow.CurrentLogFile;
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
      }
    }

    private async void TriggersDoUpdate(object sender, EventArgs e)
    {
      _triggerUpdateTimer.Stop();

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
        var triggerLogs = new List<TriggerLogStore>();
        foreach (var processor in await GetProcessorsAsync())
        {
          triggerLogs.Add(processor.TriggerLog);
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
        EventsProcessorsUpdated?.Invoke(triggerLogs);
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
