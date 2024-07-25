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
    internal readonly ConcurrentDictionary<string, bool> RunningFiles = new();
    internal static TriggerManager Instance => Lazy.Value;

    private static readonly Lazy<TriggerManager> Lazy = new(() => new TriggerManager());
    private readonly DispatcherTimer _configUpdateTimer;
    private readonly DispatcherTimer _triggerUpdateTimer;
    private readonly DispatcherTimer _textOverlayTimer;
    private readonly DispatcherTimer _timerOverlayTimer;
    private readonly Dictionary<string, OverlayWindowData> _textWindows = new();
    private readonly Dictionary<string, OverlayWindowData> _timerWindows = new();
    private readonly List<LogReader> _logReaders = [];
    private readonly TriggerNode _noDefaultOverlay = new();
    private readonly SemaphoreSlim _logReadersSemaphore = new(1, 1);
    private readonly SemaphoreSlim _textOverlaySemaphore = new(1, 1);
    private readonly SemaphoreSlim _timerOverlaySemaphore = new(1, 1);
    private TriggerNode _defaultTextOverlay;
    private TriggerNode _defaultTimerOverlay;
    private TriggerProcessor _testProcessor;
    private int _timerIncrement;

    public TriggerManager()
    {
      _configUpdateTimer = CreateTimer(ConfigDoUpdate, 1500);
      _triggerUpdateTimer = CreateTimer(TriggersDoUpdate, 1500);
      _textOverlayTimer = CreateTimer(TextTick, 450, DispatcherPriority.Render);
      _timerOverlayTimer = CreateTimer(TimerTick, 50, DispatcherPriority.Render);
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

      _textOverlayTimer.Stop();
      _timerOverlayTimer.Stop();
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

    internal async Task CloseOverlay(string id)
    {
      if (!string.IsNullOrEmpty(id))
      {
        await CloseOverlayWindow(id, _textWindows, _textOverlaySemaphore, () => _defaultTextOverlay = null);
        await CloseOverlayWindow(id, _timerWindows, _timerOverlaySemaphore, () => _defaultTimerOverlay = null);
      }
    }

    internal async Task CloseOverlays()
    {
      await CloseAllOverlayWindows(_textWindows, _textOverlaySemaphore, () => _defaultTextOverlay = null);
      await CloseAllOverlayWindows(_timerWindows, _timerOverlaySemaphore, () => _defaultTimerOverlay = null);
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
      _textOverlayTimer.Stop();
      _timerOverlayTimer.Stop();
      await CloseOverlays();

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
          var processor = new TriggerProcessor(character.Id, character.Name, playerName, character.Voice, character.VoiceRate, character.ActiveColor, character.FontColor, AddTextEvent, AddTimerEvent);
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
      await CloseOverlays();
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

      await _timerOverlaySemaphore.WaitAsync();

      try
      {
        foreach (var kv in _timerWindows)
        {
          if (kv.Value.TheWindow is TimerOverlayWindow timerWindow)
          {
            var done = _timerIncrement == 10 && timerWindow.Tick(activeTimerData);
            if (!done) timerWindow.ShortTick(activeTimerData);

            if (done)
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
        }

        foreach (var id in removeList)
        {
          if (_timerWindows.Remove(id, out var windowData))
          {
            windowData.TheWindow?.Close();
          }
        }

        if (_timerWindows.Count == 0)
        {
          _timerOverlayTimer.Stop();
        }
      }
      finally
      {
        _timerOverlaySemaphore.Release();
      }

      if (_timerIncrement == 10)
      {
        _timerIncrement = 0;
      }
    }

    private async void TextTick(object sender, EventArgs e)
    {
      var removeList = new List<string>();
      await _textOverlaySemaphore.WaitAsync();

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

        if (_textWindows.Count == 0)
        {
          _textOverlayTimer.Stop();
        }
      }
      finally
      {
        _textOverlaySemaphore.Release();
      }
    }

    private async void AddTextEvent(string text, Trigger trigger, string fontColor)
    {
      fontColor ??= trigger.FontColor;
      var beginTicks = DateTime.UtcNow.Ticks;
      var textOverlayFound = false;

      await _textOverlaySemaphore.WaitAsync();

      try
      {
        if (trigger.SelectedOverlays != null)
        {
          foreach (var overlayId in trigger.SelectedOverlays)
          {
            var windowData = await GetOrCreateTextOverlayWindow(overlayId);
            if (windowData != null)
            {
              UiUtil.InvokeNow(() =>
              {
                var brush = UiUtil.GetBrush(fontColor);
                (windowData.TheWindow as TextOverlayWindow)?.AddTriggerText(text, beginTicks, brush);
              }, DispatcherPriority.Render);

              textOverlayFound = true;
            }
          }
        }

        if (!textOverlayFound && await GetDefaultTextOverlay() is { } defaultNode)
        {
          var windowData = await GetOrCreateTextOverlayWindow(defaultNode.Id);
          UiUtil.InvokeNow(() =>
          {
            var brush = UiUtil.GetBrush(fontColor);
            (windowData?.TheWindow as TextOverlayWindow)?.AddTriggerText(text, beginTicks, brush);
          }, DispatcherPriority.Render);

          textOverlayFound = true;
        }

        UiUtil.InvokeNow(() =>
        {
          if (textOverlayFound && !_textOverlayTimer.IsEnabled)
          {
            _textOverlayTimer.Start();
          }
        }, DispatcherPriority.Render);
      }
      finally
      {
        _textOverlaySemaphore.Release();
      }
    }

    private async Task<OverlayWindowData> GetOrCreateTextOverlayWindow(string overlayId)
    {
      if (!_textWindows.TryGetValue(overlayId, out var windowData))
      {
        var node = await TriggerStateManager.Instance.GetOverlayById(overlayId);
        if (node?.OverlayData.IsTextOverlay == true)
        {
          windowData = GetTextWindowData(node);
        }
      }
      return windowData;
    }

    private OverlayWindowData GetTextWindowData(TriggerNode node)
    {
      OverlayWindowData result = null;
      UiUtil.InvokeNow(() =>
      {
        result = new OverlayWindowData { TheWindow = new TextOverlayWindow(node) };
        _textWindows[node.Id] = result;
        result.TheWindow.Show();
      }, DispatcherPriority.Render);

      return result;
    }

    private async void AddTimerEvent(Trigger trigger, List<TimerData> data)
    {
      var timerOverlayFound = false;

      await _timerOverlaySemaphore.WaitAsync();

      try
      {
        if (trigger.SelectedOverlays != null)
        {
          foreach (var overlayId in trigger.SelectedOverlays)
          {
            var windowData = await GetOrCreateTimerOverlayWindow(overlayId, data);
            if (windowData != null) timerOverlayFound = true;
          }
        }

        if (!timerOverlayFound && await GetDefaultTimerOverlay() is { } defaultNode)
        {
          await GetOrCreateTimerOverlayWindow(defaultNode.Id, data);
          timerOverlayFound = true;
        }

        UiUtil.InvokeNow(() =>
        {
          if (timerOverlayFound && !_timerOverlayTimer.IsEnabled)
          {
            _timerOverlayTimer.Start();
          }
        }, DispatcherPriority.Render);
      }
      finally
      {
        _timerOverlaySemaphore.Release();
      }
    }

    private async Task<OverlayWindowData> GetOrCreateTimerOverlayWindow(string overlayId, List<TimerData> timerData)
    {
      if (!_timerWindows.TryGetValue(overlayId, out var windowData))
      {
        var node = await TriggerStateManager.Instance.GetOverlayById(overlayId);
        if (node?.OverlayData.IsTimerOverlay == true)
        {
          windowData = GetTimerWindowData(node, timerData);
        }
      }
      return windowData;
    }

    private OverlayWindowData GetTimerWindowData(TriggerNode node, List<TimerData> timerData)
    {
      OverlayWindowData result = null;

      UiUtil.InvokeNow(() =>
      {
        result = new OverlayWindowData { TheWindow = new TimerOverlayWindow(node) };
        _timerWindows[node.Id] = result;
        result.TheWindow.Show();
        ((TimerOverlayWindow)result.TheWindow).Tick(timerData);
      }, DispatcherPriority.Render);

      return result;
    }

    private async Task<TriggerNode> GetDefaultTextOverlay()
    {
      if (_defaultTextOverlay == null)
      {
        _defaultTextOverlay = await TriggerStateManager.Instance.GetDefaultTextOverlay() ?? _noDefaultOverlay;
      }

      return _defaultTextOverlay == _noDefaultOverlay ? null : _defaultTextOverlay;
    }

    private async Task<TriggerNode> GetDefaultTimerOverlay()
    {
      if (_defaultTimerOverlay == null)
      {
        _defaultTimerOverlay = await TriggerStateManager.Instance.GetDefaultTimerOverlay() ?? _noDefaultOverlay;
      }

      return _defaultTimerOverlay == _noDefaultOverlay ? null : _defaultTimerOverlay;
    }

    private static async Task CloseOverlayWindow(string id, Dictionary<string, OverlayWindowData> windows, SemaphoreSlim semaphore, Action resetDefault)
    {
      await semaphore.WaitAsync();

      try
      {
        if (windows.Remove(id, out var windowData))
        {
          UiUtil.InvokeNow(() => windowData?.TheWindow?.Close(), DispatcherPriority.Render);
          resetDefault();
        }
      }
      finally
      {
        semaphore.Release();
      }
    }

    private static async Task CloseAllOverlayWindows(Dictionary<string, OverlayWindowData> windows, SemaphoreSlim semaphore, Action resetDefault)
    {
      await semaphore.WaitAsync();

      try
      {
        var windowList = windows.Values.ToList();
        windows.Clear();

        UiUtil.InvokeNow(() =>
        {
          windowList.ForEach(window => window.TheWindow.Close());
          resetDefault();
        }, DispatcherPriority.Render);
      }
      finally
      {
        semaphore.Release();
      }
    }

    private static DispatcherTimer CreateTimer(EventHandler tickHandler, int interval, DispatcherPriority priority = DispatcherPriority.Normal)
    {
      var timer = new DispatcherTimer(priority) { Interval = TimeSpan.FromMilliseconds(interval) };
      timer.Tick += tickHandler;
      return timer;
    }
  }
}
