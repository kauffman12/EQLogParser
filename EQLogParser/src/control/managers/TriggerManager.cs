using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace EQLogParser
{
  internal class TriggerManager
  {
    internal event Action<bool> EventsProcessorsUpdated;
    internal event Action<AlertEntry> EventsSelectTrigger;
    internal static TriggerManager Instance => Lazy.Value;

    private static readonly Lazy<TriggerManager> Lazy = new(() => new TriggerManager());
    private readonly DispatcherTimer _configUpdateTimer;
    private readonly DispatcherTimer _triggerUpdateTimer;
    private readonly List<LogReader> _logReaders = [];
    private readonly SemaphoreSlim _logReadersSemaphore = new(1, 1);
    private TriggerProcessor _testProcessor;

    public TriggerManager()
    {
      _configUpdateTimer = UiUtil.CreateTimer(ConfigDoUpdate, 500);
      _triggerUpdateTimer = UiUtil.CreateTimer(TriggersDoUpdate, 1000);
      TriggerStateManager.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
    }

    internal void Select(AlertEntry entry) => EventsSelectTrigger?.Invoke(entry);

    internal void TriggersUpdated()
    {
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
        await TriggerOverlayManager.Instance.StopAsync();
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

      TriggerOverlayManager.Instance.HideOverlays();
    }

    internal async Task<List<LogReader>> GetLogReadersAsync()
    {
      await _logReadersSemaphore.WaitAsync();

      try
      {
        return [.. _logReaders];
      }
      finally
      {
        _logReadersSemaphore.Release();
      }
    }

    internal async Task SetTestProcessor(TriggerConfig config, BlockingCollection<Tuple<string, double, bool>> collection)
    {
      await InitTestProcessor(TriggerStateManager.DefaultUser, $"Trigger Tester ({TriggerStateManager.DefaultUser})", ConfigUtil.PlayerName,
        config.Voice, config.VoiceRate, null, null, collection);
    }

    internal async Task SetTestProcessor(TriggerCharacter character, BlockingCollection<Tuple<string, double, bool>> collection)
    {
      var playerName = !FileUtil.ParseFileName(character.FilePath, out var parsedPlayerName, out _) ? character.Name : parsedPlayerName;
      await InitTestProcessor(character.Id, $"Trigger Tester ({character.Name})", playerName, character.Voice,
        character.VoiceRate, character.ActiveColor, character.FontColor, collection);
    }

    private async Task InitTestProcessor(string id, string name, string playerName, string voice, int voiceRate,
      string activeColor, string fontColor, BlockingCollection<Tuple<string, double, bool>> collection)
    {
      _testProcessor?.Dispose();
      _testProcessor =
        new TriggerProcessor(id, name, playerName, voice, voiceRate, activeColor, fontColor);
      _testProcessor.SetTesting(true);
      await _testProcessor.StartAsync();
      _testProcessor.LinkTo(collection);
      await FireEventsProcessorsUpdatedAsync();
    }

    internal Task StopTestProcessor()
    {
      _testProcessor?.Dispose();
      _testProcessor = null;
      return Task.CompletedTask;
    }

    internal async Task<List<Tuple<string, ObservableCollection<AlertEntry>>>> GetAlertLogs()
    {
      var processors = await GetProcessorsAsync();
      return processors.Select(p => Tuple.Create(p.CurrentProcessorName, p.AlertLog)).ToList();
    }

    private async void TriggerManagerEventsLogLoadingComplete(string _)
    {
      if (await TriggerStateManager.Instance.GetConfig() is { IsAdvanced: false })
      {
        ConfigDoUpdate(this, null);
      }
    }

    private void TriggerConfigUpdateEvent(TriggerConfig _)
    {
      _configUpdateTimer.Stop();
      _configUpdateTimer.Start();
    }

    private async void ConfigDoUpdate(object sender, EventArgs e)
    {
      _configUpdateTimer.Stop();

      if (await TriggerStateManager.Instance.GetConfig() is { } config)
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
      if (_logReaders.Any(reader => reader.GetProcessor() is TriggerProcessor { CurrentCharacterId: TriggerStateManager.DefaultUser }))
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
          }
          else
          {
            processor.SetActiveColor(found.ActiveColor);
            processor.SetFontColor(found.FontColor);
            processor.SetVoice(found.Voice);
            processor.SetVoiceRate(found.VoiceRate);
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
              character.ActiveColor, character.FontColor);
            await processor.StartAsync();
            var reader = new LogReader(processor, character.FilePath);
            _logReaders.Add(reader);
            await Task.Run(() => reader.Start());
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
        if (config.IsEnabled && _logReaders[0].GetProcessor() is TriggerProcessor { CurrentCharacterId: TriggerStateManager.DefaultUser } p
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
          var processor = new TriggerProcessor(TriggerStateManager.DefaultUser, TriggerStateManager.DefaultUser, ConfigUtil.PlayerName, config.Voice,
            config.VoiceRate, null, null);
          await processor.StartAsync();
          var reader = new LogReader(processor, currentFile);
          _logReaders.Add(reader);
          await Task.Run(() => reader.Start());
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
      var processors = await GetProcessorsAsync();
      foreach (var processor in processors)
      {
        await processor.UpdateActiveTriggers();
      }

      await UpdateOverlayInfo();
    }

    private async Task FireEventsProcessorsUpdatedAsync()
    {
      await UpdateOverlayInfo();
      await UiUtil.InvokeAsync(() => EventsProcessorsUpdated?.Invoke(true));
    }

    private async Task UpdateOverlayInfo()
    {
      var idSet = new HashSet<string>();
      var triggerSet = new HashSet<string>();
      foreach (var processor in await GetProcessorsAsync())
      {
        foreach (var id in processor.GetRequiredOverlayIds())
        {
          idSet.Add(id);
        }

        foreach (var id in processor.GetEnabledTriggers())
        {
          triggerSet.Add(id);
        }
      }

      await TriggerOverlayManager.Instance.UpdateOverlayInfoAsync(idSet, triggerSet);
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
