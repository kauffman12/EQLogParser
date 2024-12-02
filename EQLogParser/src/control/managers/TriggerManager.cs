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
    private readonly ConcurrentDictionary<string, TriggerNode> _textOverlayCache = new();
    private readonly ConcurrentDictionary<string, TriggerNode> _timerOverlayCache = new();
    private readonly ConcurrentDictionary<string, List<TriggerNode>> _textOverlayResultCache = new();
    private readonly ConcurrentDictionary<string, List<TriggerNode>> _timerOverlayResultCache = new();
    private readonly DispatcherTimer _configUpdateTimer;
    private readonly DispatcherTimer _triggerUpdateTimer;
    private readonly DispatcherTimer _textOverlayTimer;
    private readonly DispatcherTimer _timerOverlayTimer;
    private readonly Dictionary<string, OverlayWindowData> _textWindows = new();
    private readonly Dictionary<string, OverlayWindowData> _timerWindows = new();
    private readonly List<LogReader> _logReaders = [];
    private readonly SemaphoreSlim _logReadersSemaphore = new(1, 1);
    private TriggerNode _defaultTextOverlay;
    private TriggerNode _defaultTimerOverlay;
    private TriggerProcessor _testProcessor;
    private int _timerIncrement;

    public TriggerManager()
    {
      _configUpdateTimer = CreateTimer(ConfigDoUpdate, 1500);
      _triggerUpdateTimer = CreateTimer(TriggersDoUpdate, 1500);
      _textOverlayTimer = CreateTimer(TextTick, 450);
      _timerOverlayTimer = CreateTimer(TimerTick, 50);
      TriggerStateManager.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
    }

    internal void Select(AlertEntry entry) => EventsSelectTrigger?.Invoke(entry);

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
        return _logReaders.ToList();
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
          _defaultTextOverlay = null;
          if (_textWindows.Count == 0)
          {
            _textOverlayTimer.Stop();
          }

          _textOverlayCache.Clear();
          _textOverlayResultCache.Clear();
        });

        CloseOverlayWindow(id, _timerWindows, () =>
        {
          _defaultTimerOverlay = null;
          if (_timerWindows.Count == 0)
          {
            _timerOverlayTimer.Stop();
          }

          _timerOverlayCache.Clear();
          _timerOverlayResultCache.Clear();
        });
      }
    }

    internal void CloseOverlays()
    {
      CloseAllOverlayWindows(_textWindows, () =>
      {
        _defaultTextOverlay = null;
        _textOverlayResultCache.Clear();
        _textOverlayCache.Clear();
        _textOverlayResultCache.Clear();
        _textOverlayTimer.Stop();
      });

      CloseAllOverlayWindows(_timerWindows, () =>
      {
        _defaultTimerOverlay = null;
        _timerOverlayResultCache.Clear();
        _timerOverlayCache.Clear();
        _timerOverlayResultCache.Clear();
        _timerOverlayTimer.Stop();
      });
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
      CloseOverlays();

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
      var clearReaders = _logReaders.Any(reader => reader.GetProcessor() is TriggerProcessor { CurrentCharacterId: TriggerStateManager.DefaultUser });
      if (clearReaders)
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
          var found = config.Characters.FirstOrDefault(character => character.Id == processor.CurrentCharacterId && character.Name == processor.CurrentProcessorName);
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
          }
        }
      }

      RunningFiles.Clear();
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
      _logReaders.ForEach(reader => reader.Dispose());
      _logReaders.Clear();

      if (config.IsEnabled && MainWindow.CurrentLogFile is { } currentFile)
      {
        var processor = new TriggerProcessor(TriggerStateManager.DefaultUser, TriggerStateManager.DefaultUser, ConfigUtil.PlayerName, config.Voice, config.VoiceRate, null, null, AddTextEvent, AddTimerEvent);
        await processor.Start();
        var reader = new LogReader(processor, currentFile);
        _logReaders.Add(reader);
        RunningFiles[currentFile] = true;
        await Task.Run(() => reader.Start());
        MainActions.ShowTriggersEnabled(true);
      }
      else
      {
        MainActions.ShowTriggersEnabled(false);
        RunningFiles.Clear();
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

    private async void TimerTick(object sender, EventArgs e)
    {
      _timerIncrement++;
      var removeList = new List<string>();
      var processors = await GetProcessorsAsync();
      var activeTimerData = processors.SelectMany(processor => processor.GetActiveTimers()).ToList();

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
                  removeList.Add(kv.Key);
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
                    removeList.Add(kv.Key);
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

        foreach (var id in removeList)
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
      var removeList = new List<string>();

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
              removeList.Add(kv.Key);
            }
          }
          else
          {
            kv.Value.RemoveTicks = -1;
          }
        }

        foreach (var id in removeList)
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

    private async void AddTextEvent(string id, Trigger trigger, string text, string fontColor)
    {
      if (id == null) return;
      fontColor ??= trigger.FontColor;
      var beginTicks = DateTime.UtcNow.Ticks;
      var overlayNodes = await GetOverlayNodes(id, trigger, _textOverlayCache);

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

    private async void AddTimerEvent(string id, Trigger trigger, List<TimerData> data)
    {
      if (id == null) return;
      var overlayNodes = await GetOverlayNodes(id, trigger, _timerOverlayCache);

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
              ((TimerOverlayWindow)windowData.TheWindow).Tick(data);
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

    private async Task<List<TriggerNode>> GetOverlayNodes(string id, Trigger trigger, ConcurrentDictionary<string, TriggerNode> cache)
    {
      var isTextOverlay = cache == _textOverlayCache;
      List<TriggerNode> cachedResult = null;

      UiUtil.InvokeNow(() =>
      {
        if (isTextOverlay)
        {
          _textOverlayResultCache.TryGetValue(id, out cachedResult);
        }

        if (!isTextOverlay)
        {
          _timerOverlayResultCache.TryGetValue(id, out cachedResult);
        }
      });

      if (cachedResult != null)
      {
        return cachedResult;
      }

      var result = new List<TriggerNode>();
      if (trigger.SelectedOverlays != null)
      {
        foreach (var overlayId in trigger.SelectedOverlays)
        {
          if (!string.IsNullOrEmpty(overlayId))
          {
            if (cache.TryGetValue(overlayId, out var existing))
            {
              result.Add(existing);
            }
            else
            {
              var node = await TriggerStateManager.Instance.GetOverlayById(overlayId);
              if (node?.OverlayData.IsTextOverlay == isTextOverlay || node?.OverlayData.IsTimerOverlay == !isTextOverlay)
              {
                result.Add(node);
                cache.TryAdd(node.Id, node);
              }
            }
          }
        }
      }

      if (result.Count == 0)
      {
        if (isTextOverlay)
        {
          if (_defaultTextOverlay == null)
          {
            _defaultTextOverlay = await TriggerStateManager.Instance.GetDefaultTextOverlay();
          }

          if (_defaultTextOverlay != null)
          {
            result.Add(_defaultTextOverlay);
          }
        }
        else
        {
          if (_defaultTimerOverlay == null)
          {
            _defaultTimerOverlay = await TriggerStateManager.Instance.GetDefaultTimerOverlay();
          }

          if (_defaultTimerOverlay != null)
          {
            result.Add(_defaultTimerOverlay);
          }
        }
      }

      if (isTextOverlay)
      {
        _textOverlayResultCache[id] = result;
      }
      else
      {
        _timerOverlayResultCache[id] = result;
      }

      return result;
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
