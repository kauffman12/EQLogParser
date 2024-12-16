using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace EQLogParser
{
  internal class TriggerManager
  {
    internal event Action<bool> EventsProcessorsUpdated;
    internal event Action<AlertEntry> EventsSelectTrigger;
    internal readonly ConcurrentDictionary<string, bool> RunningFiles = new();
    internal static TriggerManager Instance => Lazy.Value;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Lazy<TriggerManager> Lazy = new(() => new TriggerManager());
    private readonly DispatcherTimer _configUpdateTimer;
    private readonly DispatcherTimer _overlayUpdateTimer;
    private readonly DispatcherTimer _triggerUpdateTimer;
    private readonly DispatcherTimer _textOverlayTimer;
    private readonly DispatcherTimer _timerOverlayTimer;
    private readonly Dictionary<string, OverlayWindowData> _textWindows = [];
    private readonly Dictionary<string, OverlayWindowData> _timerWindows = [];
    private readonly List<LogReader> _logReaders = [];
    private readonly SemaphoreSlim _logReadersSemaphore = new(1, 1);
    private readonly List<string> _removeTextList = [];
    private readonly List<string> _removeTimerList = [];
    private TriggerProcessor _testProcessor;
    private int _timerIncrement;

    public TriggerManager()
    {
      _configUpdateTimer = CreateTimer(ConfigDoUpdate, 1000);
      _overlayUpdateTimer = CreateTimer(OverlaysDoUpdate, 1000);
      _triggerUpdateTimer = CreateTimer(TriggersDoUpdate, 1000);
      _textOverlayTimer = CreateTimer(TextTick, 450);
      _timerOverlayTimer = CreateTimer(TimerTick, 50);
      TriggerStateManager.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
    }

    internal void Select(AlertEntry entry) => EventsSelectTrigger?.Invoke(entry);

    internal void OverlaysUpdated()
    {
      _overlayUpdateTimer.Stop();
      _overlayUpdateTimer.Start();
    }

    internal void TriggersUpdated()
    {
      _triggerUpdateTimer.Stop();
      _triggerUpdateTimer.Start();
    }

    internal async Task Start()
    {
      await TriggerUtil.LoadOverlayStyles();
      MainActions.EventsLogLoadingComplete += TriggerManagerEventsLogLoadingComplete;
      TriggerConfigUpdateEvent(null);
    }

    internal async Task Stop()
    {
      MainActions.EventsLogLoadingComplete -= TriggerManagerEventsLogLoadingComplete;
      await _logReadersSemaphore.WaitAsync();

      try
      {
        _logReaders.ForEach(reader => reader.Dispose());
        _logReaders.Clear();
      }
      finally
      {
        _logReadersSemaphore.Release();
      }

      CloseOverlays();
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

    internal void CloseOverlay(string id)
    {
      if (!string.IsNullOrEmpty(id))
      {
        CloseOverlayWindow(id, _textWindows, () =>
        {
          if (_textWindows.Count == 0)
          {
            _textOverlayTimer.Stop();
          }
        });

        CloseOverlayWindow(id, _timerWindows, () =>
        {
          if (_timerWindows.Count == 0)
          {
            _timerOverlayTimer.Stop();
          }
        });
      }
    }

    internal void CloseOverlays()
    {
      CloseAllOverlayWindows(_textWindows, _textOverlayTimer.Stop);
      CloseAllOverlayWindows(_timerWindows, _timerOverlayTimer.Stop);
    }

    internal async Task SetTestProcessor(TriggerConfig config, BlockingCollection<Tuple<string, double, bool>> collection)
    {
      await InitTestProcessor(TriggerStateManager.DefaultUser, $"Trigger Tester ({TriggerStateManager.DefaultUser})", ConfigUtil.PlayerName,
        config.Voice, config.VoiceRate, collection);
    }

    internal async Task SetTestProcessor(TriggerCharacter character, BlockingCollection<Tuple<string, double, bool>> collection)
    {
      var playerName = !FileUtil.ParseFileName(character.FilePath, out var parsedPlayerName, out _) ? character.Name : parsedPlayerName;
      await InitTestProcessor(character.Id, $"Trigger Tester ({character.Name})", playerName, character.Voice, character.VoiceRate, collection);
    }

    private async Task InitTestProcessor(string id, string name, string playerName, string voice, int voiceRate, BlockingCollection<Tuple<string, double, bool>> collection)
    {
      _testProcessor?.Dispose();
      _testProcessor =
        new TriggerProcessor(id, name, playerName, voice, voiceRate, null, null, AddTextEvent, AddTimerEvent);
      _testProcessor.SetTesting(true);
      await _testProcessor.Start();
      _testProcessor.LinkTo(collection);
      UiUtil.InvokeNow(() => EventsProcessorsUpdated?.Invoke(true));
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

        EventsProcessorsUpdated?.Invoke(true);
      }
    }

    private async Task HandleAdvancedConfig(TriggerConfig config)
    {
      RunningFiles.Clear();

      // if Default User is being used then we switched from basic so clear all
      if (_logReaders.Any(reader => reader.GetProcessor() is TriggerProcessor { CurrentCharacterId: TriggerStateManager.DefaultUser }))
      {
        _logReaders.ForEach(item => item.Dispose());
        _logReaders.Clear();
        CloseOverlays();
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
            processor.SetVoice(found.Voice);
            processor.SetVoiceRate(found.VoiceRate);
            alreadyRunning.Add(found.Id);
            RunningFiles[found.FilePath] = true;
          }
        }
      }

      toRemove.ForEach(remove => _logReaders.Remove(remove));

      var startTasks = config.Characters
        .Where(character => character.IsEnabled && !alreadyRunning.Contains(character.Id))
        .Select(async character =>
        {
          var playerName = !FileUtil.ParseFileName(character.FilePath, out var parsedPlayerName, out _) ? character.Name : parsedPlayerName;
          var processor = new TriggerProcessor(character.Id, character.Name, playerName, character.Voice, character.VoiceRate,
            character.ActiveColor, character.FontColor, AddTextEvent, AddTimerEvent);
          await processor.Start();
          var reader = new LogReader(processor, character.FilePath);
          _logReaders.Add(reader);
          RunningFiles[character.FilePath] = true;
          await Task.Run(() => reader.Start());
        }).ToList();

      await Task.WhenAll(startTasks);
      MainActions.ShowTriggersEnabled(_logReaders.Count > 0);
    }

    private async Task HandleBasicConfig(TriggerConfig config)
    {
      RunningFiles.Clear();

      var currentFile = MainWindow.CurrentLogFile;
      LogReader defReader = null;
      TriggerProcessor defProcessor = null;

      if (_logReaders.Count > 0)
      {
        if (config.IsEnabled && _logReaders[0].GetProcessor() is TriggerProcessor { CurrentCharacterId: TriggerStateManager.DefaultUser } p
          && _logReaders[0].FileName == currentFile)
        {
          defReader = _logReaders[0];
          defProcessor = p;
        }
        else
        {
          _logReaders.ForEach(item => item.Dispose());
          _logReaders.Clear();
          CloseOverlays();
        }
      }

      if (config.IsEnabled)
      {
        if (defReader == null || defProcessor == null)
        {
          var processor = new TriggerProcessor(TriggerStateManager.DefaultUser, TriggerStateManager.DefaultUser, ConfigUtil.PlayerName, config.Voice,
            config.VoiceRate, null, null, AddTextEvent, AddTimerEvent);
          await processor.Start();
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

        RunningFiles[currentFile] = true;
      }
      else
      {
        MainActions.ShowTriggersEnabled(false);
      }
    }

    private async void OverlaysDoUpdate(object sender, EventArgs e)
    {
      _overlayUpdateTimer.Stop();
      var processors = await GetProcessorsAsync();
      foreach (var processor in processors)
      {
        await processor.UpdateOverlaysAsync();
      }
    }

    private async void TriggersDoUpdate(object sender, EventArgs e)
    {
      _triggerUpdateTimer.Stop();
      CloseOverlays();
      var processors = await GetProcessorsAsync();
      foreach (var processor in processors)
      {
        await processor.UpdateActiveTriggers();
      }
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

    private void TimerTick(object sender, EventArgs e)
    {
      _timerIncrement++;
      var activeTimerData = TriggerProcessor.ActiveTimers.ToList();
      _removeTimerList.Clear();

      try
      {
        foreach (var kv in _timerWindows)
        {
          if (kv.Value.TheWindow is TimerOverlayWindow timerWindow)
          {
            if (_timerIncrement == 10)
            {
              // if done
              if (timerWindow.Tick(activeTimerData))
              {
                if (kv.Value.IsCooldown)
                {
                  _removeTimerList.Add(kv.Key);
                }
                else
                {
                  var nowTicks = DateTime.UtcNow.Ticks;
                  if (kv.Value.RemoveTicks == -1)
                  {
                    kv.Value.RemoveTicks = nowTicks + (TimeSpan.TicksPerMinute * 2);
                  }
                  else if (nowTicks > kv.Value.RemoveTicks)
                  {
                    _removeTimerList.Add(kv.Key);
                  }
                }
              }
              else
              {
                kv.Value.RemoveTicks = -1;
              }
            }
            else
            {
              timerWindow.ShortTick(activeTimerData);
            }
          }
        }

        foreach (var id in _removeTimerList)
        {
          if (_timerWindows.Remove(id, out var windowData))
          {
            windowData.TheWindow?.Close();
          }
        }
      }
      catch (Exception ex)
      {
        Log.Error("Error in TimerTick", ex);
      }
      finally
      {
        if (_timerWindows.Count == 0)
        {
          _timerOverlayTimer.Stop();
        }
      }

      if (_timerIncrement == 10)
      {
        _timerIncrement = 0;
      }
    }

    private void TextTick(object sender, EventArgs e)
    {
      _removeTextList.Clear();

      try
      {
        foreach (var kv in _textWindows)
        {
          if (kv.Value.TheWindow is TextOverlayWindow textWindow && textWindow.Tick())
          {
            var nowTicks = DateTime.UtcNow.Ticks;
            if (kv.Value.RemoveTicks == -1)
            {
              kv.Value.RemoveTicks = nowTicks + (TimeSpan.TicksPerMinute * 2);
            }
            else if (nowTicks > kv.Value.RemoveTicks)
            {
              _removeTextList.Add(kv.Key);
            }
          }
          else
          {
            kv.Value.RemoveTicks = -1;
          }
        }

        foreach (var id in _removeTextList)
        {
          if (_textWindows.Remove(id, out var windowData))
          {
            windowData.TheWindow?.Close();
          }
        }
      }
      catch (Exception ex)
      {
        Log.Error("Error in TextTick", ex);
      }
      finally
      {
        if (_textWindows.Count == 0)
        {
          _textOverlayTimer.Stop();
        }
      }
    }

    private void AddTextEvent(string id, Trigger trigger, List<TriggerNode> overlayNodes, string text, string fontColor)
    {
      if (id == null) return;
      fontColor ??= trigger.FontColor;
      var beginTicks = DateTime.UtcNow.Ticks;

      UiUtil.InvokeNow(() =>
      {
        try
        {
          foreach (var node in overlayNodes)
          {
            if (node.Id == null) continue;
            if (!_textWindows.TryGetValue(node.Id, out var windowData))
            {
              windowData = new OverlayWindowData { TheWindow = new TextOverlayWindow(node) };
              _textWindows[node.Id] = windowData;
              windowData.TheWindow.Show();
            }

            var brush = UiUtil.GetBrush(fontColor);
            (windowData.TheWindow as TextOverlayWindow)?.AddTriggerText(text, beginTicks, brush);
          }

          if (!_textOverlayTimer.IsEnabled)
          {
            _textOverlayTimer.Start();
          }
        }
        catch (Exception ex)
        {
          Log.Warn("Error during AddTextEvent", ex);
        }
      });
    }

    private void AddTimerEvent(string id, Trigger trigger, List<TriggerNode> overlayNodes)
    {
      if (id == null) return;
      var activeTimerData = TriggerProcessor.ActiveTimers.ToList();

      UiUtil.InvokeNow(() =>
      {
        try
        {
          foreach (var node in overlayNodes)
          {
            if (node.Id == null) continue;
            if (!_timerWindows.TryGetValue(node.Id, out var windowData))
            {
              windowData = new OverlayWindowData
              {
                TheWindow = new TimerOverlayWindow(node),
                IsCooldown = node.OverlayData?.TimerMode == 1
              };

              _timerWindows[node.Id] = windowData;
              windowData.TheWindow.Show();
              ((TimerOverlayWindow)windowData.TheWindow).Tick(activeTimerData);
            }

            if (!_timerOverlayTimer.IsEnabled)
            {
              _timerOverlayTimer.Start();
            }
          }
        }
        catch (Exception ex)
        {
          Log.Error("Error during AddTimerEvent", ex);
        }
      });
    }

    private static void CloseOverlayWindow(string id, Dictionary<string, OverlayWindowData> windows, Action resetDefault)
    {
      UiUtil.InvokeNow(() =>
      {
        try
        {
          if (windows.Remove(id, out var windowData))
          {
            windowData?.TheWindow?.Close();
            resetDefault();
          }
        }
        catch (Exception ex)
        {
          Log.Error("Error closing OverlayWindow", ex);
        }
      }, DispatcherPriority.Send);
    }

    private static void CloseAllOverlayWindows(Dictionary<string, OverlayWindowData> windows, Action resetDefault)
    {
      UiUtil.InvokeNow(() =>
      {
        try
        {
          var windowList = windows.Values.ToList();
          windows.Clear();
          windowList.ForEach(window => window.TheWindow.Close());
          resetDefault();
        }
        catch (Exception ex)
        {
          Log.Error("Error closing all OverlayWindows", ex);
        }
      }, DispatcherPriority.Send);
    }

    private static DispatcherTimer CreateTimer(EventHandler tickHandler, int interval, DispatcherPriority priority = DispatcherPriority.Normal)
    {
      var timer = new DispatcherTimer(priority) { Interval = TimeSpan.FromMilliseconds(interval) };
      timer.Tick += tickHandler;
      return timer;
    }
  }
}
