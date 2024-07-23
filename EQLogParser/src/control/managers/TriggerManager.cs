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
    internal static TriggerManager Instance => Lazy.Value; // instance
    public readonly Dictionary<string, bool> RunningFiles = new();
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Lazy<TriggerManager> Lazy = new(() => new TriggerManager());
    private readonly DispatcherTimer _configUpdateTimer;
    private readonly DispatcherTimer _triggerUpdateTimer;
    private readonly DispatcherTimer _textOverlayTimer;
    private readonly DispatcherTimer _timerOverlayTimer;
    private readonly Dictionary<string, OverlayWindowData> _textWindows = new();
    private readonly Dictionary<string, OverlayWindowData> _timerWindows = new();
    private readonly List<LogReader> _logReaders = [];
    private readonly TriggerNode _noDefaultOverlay = new();
    private TriggerNode _defaultTextOverlay; // cache to avoid excess queries
    private TriggerNode _defaultTimerOverlay; // cache to avoid excess queries
    private TriggerProcessor _testProcessor;
    private int _timerIncrement;
    private static readonly SemaphoreSlim LogReadersSemaphore = new(1, 1);
    private static readonly SemaphoreSlim TextOverlaySemaphore = new(1, 1);
    private static readonly SemaphoreSlim TimerOverlaySemaphore = new(1, 1);

    public TriggerManager()
    {
      _configUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1500) };
      _triggerUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1500) };
      _textOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = new TimeSpan(0, 0, 0, 0, 450) };
      _timerOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = new TimeSpan(0, 0, 0, 0, 50) };
      _configUpdateTimer.Tick += ConfigDoUpdate;
      _triggerUpdateTimer.Tick += TriggersDoUpdate;
      _textOverlayTimer.Tick += TextTick;
      _timerOverlayTimer.Tick += TimerTick;
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
      await LogReadersSemaphore.WaitAsync();

      try
      {
        _logReaders?.ForEach(reader => reader.Dispose());
        _logReaders?.Clear();
      }
      finally
      {
        LogReadersSemaphore.Release();
      }

      _textOverlayTimer?.Stop();
      _timerOverlayTimer?.Stop();
    }

    internal async Task<List<LogReader>> GetLogReadersAsync()
    {
      await LogReadersSemaphore.WaitAsync();

      try
      {
        return _logReaders.ToList();
      }
      finally
      {
        LogReadersSemaphore.Release();
      }
    }

    internal async Task CloseOverlay(string id)
    {
      if (!string.IsNullOrEmpty(id))
      {
        await TextOverlaySemaphore.WaitAsync();

        try
        {
          _textWindows.Remove(id, out var textWindowData);

          UiUtil.InvokeNow(() =>
          {
            textWindowData?.TheWindow?.Close();
            _defaultTextOverlay = null;
          }, DispatcherPriority.Render);
        }
        finally
        {
          TextOverlaySemaphore.Release();
        }

        await TimerOverlaySemaphore.WaitAsync();

        try
        {
          _timerWindows.Remove(id, out var timerWindowData);

          UiUtil.InvokeNow(() =>
          {
            timerWindowData?.TheWindow?.Close();
            _defaultTimerOverlay = null;
          }, DispatcherPriority.Render);
        }
        finally
        {
          TimerOverlaySemaphore.Release();
        }
      }
    }

    internal async Task CloseOverlays()
    {
      await TextOverlaySemaphore.WaitAsync();

      try
      {
        var textList = _textWindows.Values.ToList();
        _textWindows.Clear();

        UiUtil.InvokeNow(() =>
        {
          textList.ForEach(window => window.TheWindow.Close());
          _defaultTextOverlay = null;
        }, DispatcherPriority.Render);
      }
      finally
      {
        TextOverlaySemaphore.Release();
      }

      await TimerOverlaySemaphore.WaitAsync();

      try
      {
        var timerList = _timerWindows.Values.ToList();
        _timerWindows.Clear();

        UiUtil.InvokeNow(() =>
        {
          timerList.ForEach(window => window.TheWindow.Close());
          _defaultTimerOverlay = null;
        }, DispatcherPriority.Render);
      }
      finally
      {
        TimerOverlaySemaphore.Release();
      }
    }

    internal async Task SetTestProcessor(TriggerConfig config, BlockingCollection<Tuple<string, double, bool>> collection)
    {
      _testProcessor?.Dispose();
      const string name = TriggerStateManager.DefaultUser;
      _testProcessor = new TriggerProcessor(name, $"Trigger Tester ({name})", ConfigUtil.PlayerName, config.Voice,
        config.VoiceRate, null, null, AddTextEvent, AddTimerEvent);
      _testProcessor.SetTesting(true);
      await _testProcessor.Start();
      _testProcessor.LinkTo(collection);
      UiUtil.InvokeNow(() => EventsProcessorsUpdated?.Invoke(true));
    }

    internal async Task SetTestProcessor(TriggerCharacter character, BlockingCollection<Tuple<string, double, bool>> collection)
    {
      _testProcessor?.Dispose();
      // if cant parse then use character name
      if (!FileUtil.ParseFileName(character.FilePath, out var playerName, out _))
      {
        playerName = character.Name;
      }

      _testProcessor = new TriggerProcessor(character.Id, $"Trigger Tester ({character.Name})", playerName, character.Voice,
        character.VoiceRate, character.ActiveColor, character.FontColor, AddTextEvent, AddTimerEvent);
      _testProcessor.SetTesting(true);
      await _testProcessor.Start();
      _testProcessor.LinkTo(collection);
      UiUtil.InvokeNow(() => EventsProcessorsUpdated?.Invoke(true));
    }

    internal void StopTestProcessor()
    {
      _testProcessor?.Dispose();
      _testProcessor = null;
    }

    internal async Task<List<Tuple<string, ObservableCollection<AlertEntry>>>> GetAlertLogs()
    {
      var processors = await GetProcessorsAsync();
      return processors.Select(p => Tuple.Create(p.CurrentProcessorName, p.AlertLog)).ToList();
    }

    private async void TriggerManagerEventsLogLoadingComplete(string _)
    {
      // ignore event if in advanced mode
      if (await TriggerStateManager.Instance.GetConfig() is { IsAdvanced: false })
      {
        ConfigDoUpdate(this, null);
      }
    }

    private void TriggerConfigUpdateEvent(TriggerConfig _)
    {
      _configUpdateTimer?.Stop();
      _configUpdateTimer?.Start();
    }

    private async void ConfigDoUpdate(object sender, EventArgs e)
    {
      _configUpdateTimer.Stop();
      _textOverlayTimer?.Stop();
      _timerOverlayTimer?.Stop();
      await CloseOverlays();

      if (await TriggerStateManager.Instance.GetConfig() is { } config)
      {
        await LogReadersSemaphore.WaitAsync();

        try
        {
          var startTasks = new List<Task>();
          if (config.IsAdvanced)
          {
            // Only clear out everything if switched from basic
            var clearReaders = false;
            foreach (var reader in _logReaders)
            {
              if (reader.GetProcessor() is TriggerProcessor { CurrentCharacterId: TriggerStateManager.DefaultUser })
              {
                clearReaders = true;
                break;
              }
            }

            if (clearReaders)
            {
              _logReaders.ForEach(item => item.Dispose());
              _logReaders.Clear();
            }

            // remove stales readers first
            var toRemove = new List<LogReader>();
            var alreadyRunning = new List<string>();
            foreach (var reader in _logReaders)
            {
              // remove readers if the character no longer exists
              if (reader.GetProcessor() is TriggerProcessor processor)
              {
                // use processor name as well in-case it's a rename
                var found = config.Characters.FirstOrDefault(character =>
                  character.Id == processor.CurrentCharacterId && character.Name == processor.CurrentProcessorName);
                // if file path changed or no longer enabled remove it
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

            // add characters that aren't enabled yet
            foreach (var character in config.Characters)
            {
              if (character.IsEnabled && !alreadyRunning.Contains(character.Id))
              {
                // if cant parse then use character name
                if (!FileUtil.ParseFileName(character.FilePath, out var playerName, out _))
                {
                  playerName = character.Name;
                }

                var processor = new TriggerProcessor(character.Id, character.Name, playerName,
                  character.Voice, character.VoiceRate, character.ActiveColor, character.FontColor, AddTextEvent,
                  AddTimerEvent);
                await processor.Start();
                var reader = new LogReader(processor, character.FilePath);
                _logReaders.Add(reader);

                startTasks.Add(Task.Run(() => reader.Start()));
                RunningFiles[character.FilePath] = true;
              }
            }

            MainActions.ShowTriggersEnabled(_logReaders.Count > 0);
          }
          else
          {
            // Basic always clear out everything
            _logReaders.ForEach(reader => reader.Dispose());
            _logReaders.Clear();

            if (config.IsEnabled)
            {
              if (MainWindow.CurrentLogFile is { } currentFile)
              {
                var processor = new TriggerProcessor(TriggerStateManager.DefaultUser, TriggerStateManager.DefaultUser,
                  ConfigUtil.PlayerName, config.Voice, config.VoiceRate, null, null, AddTextEvent, AddTimerEvent);
                await processor.Start();
                var reader = new LogReader(processor, currentFile);
                _logReaders.Add(reader);
                startTasks.Add(Task.Run(() => reader.Start()));
                MainActions.ShowTriggersEnabled(true);

                // only 1 running file in basic mode
                RunningFiles.Clear();
                RunningFiles[currentFile] = true;
              }
            }
            else
            {
              MainActions.ShowTriggersEnabled(false);
              RunningFiles.Clear();
            }
          }

          await Task.WhenAll(startTasks);
        }
        finally
        {
          LogReadersSemaphore.Release();
        }

        EventsProcessorsUpdated?.Invoke(true);
      }
    }

    private async void TriggersDoUpdate(object sender, EventArgs e)
    {
      _triggerUpdateTimer.Stop();
      await CloseOverlays();
      foreach (var processor in await GetProcessorsAsync())
      {
        await processor.UpdateActiveTriggers();
      }
    }

    private async Task<List<TriggerProcessor>> GetProcessorsAsync()
    {
      await LogReadersSemaphore.WaitAsync();

      try
      {
        var list = new List<TriggerProcessor>();
        foreach (var reader in _logReaders)
        {
          if (reader.GetProcessor() is TriggerProcessor processor)
          {
            list.Add(processor);
          }
        }

        if (_testProcessor != null)
        {
          list.Add(_testProcessor);
        }
        return list;
      }
      finally
      {
        LogReadersSemaphore.Release();
      }
    }

    private async void TimerTick(object sender, EventArgs e)
    {
      _timerIncrement++;
      var removeList = new List<string>();
      var data = GetProcessorsAsync().Result.SelectMany(processor => processor.GetActiveTimers()).ToList();

      await TimerOverlaySemaphore.WaitAsync();

      try
      {
        foreach (var kv in _timerWindows)
        {
          var done = false;
          var shortTick = false;
          if (kv.Value is { } windowData)
          {
            if (windowData.TheWindow is TimerOverlayWindow timerWindow)
            {
              // full tick every 500ms
              if (_timerIncrement == 10)
              {
                done = timerWindow.Tick(data);
              }
              else
              {
                timerWindow.ShortTick(data);
                shortTick = true;
              }
            }

            if (!shortTick)
            {
              if (done)
              {
                var nowTicks = DateTime.UtcNow.Ticks;
                if (windowData.RemoveTicks == -1)
                {
                  windowData.RemoveTicks = nowTicks + (TimeSpan.TicksPerMinute * 2);
                }
                else if (nowTicks > windowData.RemoveTicks)
                {
                  removeList.Add(kv.Key);
                }
              }
              else
              {
                windowData.RemoveTicks = -1;
              }
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
        TimerOverlaySemaphore.Release();
      }

      if (_timerIncrement == 10)
      {
        _timerIncrement = 0;
      }
    }

    private async void TextTick(object sender, EventArgs e)
    {
      var removeList = new List<string>();

      await TextOverlaySemaphore.WaitAsync();

      try
      {
        foreach (var kv in _textWindows)
        {
          var done = false;
          if (kv.Value is { } windowData)
          {
            if (windowData.TheWindow is TextOverlayWindow textWindow)
            {
              done = textWindow.Tick();
            }

            if (done)
            {
              var nowTicks = DateTime.UtcNow.Ticks;
              if (windowData.RemoveTicks == -1)
              {
                windowData.RemoveTicks = nowTicks + (TimeSpan.TicksPerMinute * 2);
              }
              else if (nowTicks > windowData.RemoveTicks)
              {
                removeList.Add(kv.Key);
              }
            }
            else
            {
              windowData.RemoveTicks = -1;
            }
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
        TextOverlaySemaphore.Release();
      }
    }

    private async void AddTextEvent(string text, Trigger trigger, string fontColor)
    {
      var beginTicks = DateTime.UtcNow.Ticks;
      fontColor ??= trigger.FontColor;
      var textOverlayFound = false;

      await Task.Run(async () =>
      {
        await TextOverlaySemaphore.WaitAsync();

        try
        {
          if (trigger.SelectedOverlays != null)
          {
            foreach (var overlayId in trigger.SelectedOverlays)
            {
              if (!_textWindows.TryGetValue(overlayId, out var windowData))
              {
                if (await TriggerStateManager.Instance.GetOverlayById(overlayId) is { OverlayData.IsTextOverlay: true } node)
                {
                  windowData = GetTextWindowData(node);
                }
              }

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

          if (!textOverlayFound && await GetDefaultTextOverlay() is { } node2)
          {
            if (!_textWindows.TryGetValue(node2.Id, out var windowData))
            {
              windowData = GetTextWindowData(node2);
            }

            // using default
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
          TextOverlaySemaphore.Release();
        }
      });
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

      await Task.Run(async () =>
      {
        await TimerOverlaySemaphore.WaitAsync();

        try
        {
          if (trigger.SelectedOverlays != null)
          {
            foreach (var overlayId in trigger.SelectedOverlays)
            {
              if (!_timerWindows.TryGetValue(overlayId, out var windowData))
              {
                if (await TriggerStateManager.Instance.GetOverlayById(overlayId) is { OverlayData.IsTimerOverlay: true } node)
                {
                  windowData = GetTimerWindowData(node, data);
                }
              }

              // may not have found a timer overlay
              if (windowData != null)
              {
                timerOverlayFound = true;
              }
            }
          }

          if (!timerOverlayFound && await GetDefaultTimerOverlay() is { } node2)
          {
            if (!_timerWindows.TryGetValue(node2.Id, out _))
            {
              GetTimerWindowData(node2, data);
            }

            // using default
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
          TimerOverlaySemaphore.Release();
        }
      });
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
        if (await TriggerStateManager.Instance.GetDefaultTextOverlay() is { } overlay)
        {
          _defaultTextOverlay = overlay;
        }
        else
        {
          _defaultTextOverlay = _noDefaultOverlay;
        }
      }

      if (_defaultTextOverlay == _noDefaultOverlay)
      {
        return null;
      }

      return _defaultTextOverlay;
    }

    private async Task<TriggerNode> GetDefaultTimerOverlay()
    {
      if (_defaultTimerOverlay == null)
      {
        if (await TriggerStateManager.Instance.GetDefaultTimerOverlay() is { } overlay)
        {
          _defaultTimerOverlay = overlay;
        }
        else
        {
          _defaultTimerOverlay = _noDefaultOverlay;
        }
      }

      if (_defaultTimerOverlay == _noDefaultOverlay)
      {
        return null;
      }

      return _defaultTimerOverlay;
    }
  }
}
