using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace EQLogParser
{
  internal class TriggerManager
  {
    internal event Action<bool> EventsProcessorsUpdated;
    internal event Action<string> EventsSelectTrigger;
    internal static TriggerManager Instance => Lazy.Value; // instance
    public readonly Dictionary<string, bool> RunningFiles = [];
    private static readonly Lazy<TriggerManager> Lazy = new(() => new TriggerManager());
    private readonly DispatcherTimer _configUpdateTimer;
    private readonly DispatcherTimer _triggerUpdateTimer;
    private readonly DispatcherTimer _textOverlayTimer;
    private readonly DispatcherTimer _timerOverlayTimer;
    private readonly Dictionary<string, OverlayWindowData> _textWindows = [];
    private readonly Dictionary<string, OverlayWindowData> _timerWindows = [];
    private readonly List<LogReader> _logReaders = [];
    private readonly TriggerNode _noDefaultOverlay = new();
    private TriggerNode _defaultTextOverlay; // cache to avoid excess queries
    private TriggerNode _defaultTimerOverlay; // cache to avoid excess queries
    private TriggerProcessor _testProcessor;
    private int _timerIncrement;

    public TriggerManager()
    {
      if (!TriggerStateManager.Instance.IsActive())
      {
        // delay so window owner can be set correctly
        Task.Delay(1000).ContinueWith(_ =>
        {
          UiUtil.InvokeAsync(() =>
          {
            new MessageWindow("Trigger Database not available. In use by another EQLogParser?\r\nTrigger Management disabled until restart.",
              Resource.Warning).Show();
          });
        });

        (Application.Current?.MainWindow as MainWindow)?.DisableTriggers();
        return;
      }

      TriggerUtil.LoadOverlayStyles();
      _configUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1500) };
      _triggerUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1500) };
      _textOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = new TimeSpan(0, 0, 0, 0, 450) };
      _timerOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = new TimeSpan(0, 0, 0, 0, 50) };
      TriggerStateManager.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
      _configUpdateTimer.Tick += ConfigDoUpdate;
      _triggerUpdateTimer.Tick += TriggersDoUpdate;
      _textOverlayTimer.Tick += TextTick;
      _timerOverlayTimer.Tick += TimerTick;
    }

    internal void CloseOverlay(string id) => CloseOverlay(id, _textWindows, _timerWindows);
    internal void CloseOverlays() => CloseOverlays(_textWindows, _timerWindows);
    internal void Select(string id) => EventsSelectTrigger?.Invoke(id);

    internal void TriggersUpdated()
    {
      _triggerUpdateTimer.Stop();
      _triggerUpdateTimer.Start();
    }

    internal void Start()
    {
      TriggerUtil.LoadOverlayStyles();
      MainActions.EventsLogLoadingComplete += TriggerManagerEventsLogLoadingComplete;
      TriggerConfigUpdateEvent(null);
    }

    internal void Stop()
    {
      MainActions.EventsLogLoadingComplete -= TriggerManagerEventsLogLoadingComplete;

      lock (_logReaders)
      {
        _logReaders?.ForEach(reader => reader.Dispose());
        _logReaders?.Clear();
      }

      _textOverlayTimer?.Stop();
      _timerOverlayTimer?.Stop();
    }

    internal void SetTestProcessor(TriggerConfig config, BlockingCollection<Tuple<string, double, bool>> collection)
    {
      _testProcessor?.Dispose();
      const string name = TriggerStateManager.DefaultUser;
      _testProcessor = new TriggerProcessor(name, $"Trigger Tester ({name})", ConfigUtil.PlayerName, config.Voice,
        config.VoiceRate, null, null, AddTextEvent, AddTimerEvent);
      _testProcessor.SetTesting(true);
      _testProcessor.LinkTo(collection);
      UiUtil.InvokeAsync(() => EventsProcessorsUpdated?.Invoke(true));
    }

    internal void SetTestProcessor(TriggerCharacter character, BlockingCollection<Tuple<string, double, bool>> collection)
    {
      _testProcessor?.Dispose();
      string server = null;
      var playerName = character.Name;
      FileUtil.ParseFileName(character.FilePath, ref playerName, ref server);
      _testProcessor = new TriggerProcessor(character.Id, $"Trigger Tester ({character.Name})", playerName, character.Voice,
        character.VoiceRate, character.ActiveColor, character.FontColor, AddTextEvent, AddTimerEvent);
      _testProcessor.SetTesting(true);
      _testProcessor.LinkTo(collection);
      UiUtil.InvokeAsync(() => EventsProcessorsUpdated?.Invoke(true));
    }

    internal void StopTestProcessor()
    {
      _testProcessor?.Dispose();
      _testProcessor = null;
    }

    internal List<Tuple<string, ObservableCollection<AlertEntry>>> GetAlertLogs()
    {
      return GetProcessors().Select(p => Tuple.Create(p.CurrentProcessorName, p.AlertLog)).ToList();
    }

    private void TextTick(object sender, EventArgs e) => WindowTick(_textWindows, _textOverlayTimer);

    private void TriggerManagerEventsLogLoadingComplete(string _)
    {
      // ignore event if in advanced mode
      if (TriggerStateManager.Instance.GetConfig() is { IsAdvanced: false })
      {
        ConfigDoUpdate(this, null);
      }
    }

    private void TriggerConfigUpdateEvent(TriggerConfig _)
    {
      _configUpdateTimer?.Stop();
      _configUpdateTimer?.Start();
    }

    private void ConfigDoUpdate(object sender, EventArgs e)
    {
      _configUpdateTimer.Stop();
      UiUtil.InvokeAsync(CloseOverlays);
      _textOverlayTimer?.Stop();
      _timerOverlayTimer?.Stop();
      _defaultTextOverlay = null;
      _defaultTimerOverlay = null;

      if (TriggerStateManager.Instance.GetConfig() is { } config)
      {
        lock (_logReaders)
        {
          if (config.IsAdvanced)
          {
            // Only clear out everything if switched from basic
            if (GetProcessors().FirstOrDefault(p => p.CurrentCharacterId == TriggerStateManager.DefaultUser) != null)
            {
              _logReaders.ForEach(reader => reader.Dispose());
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
                if (found is not { IsEnabled: true })
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
                string server = null;
                var playerName = character.Name;
                FileUtil.ParseFileName(character.FilePath, ref playerName, ref server);
                var reader = new LogReader(new TriggerProcessor(character.Id, character.Name, playerName,
                  character.Voice, character.VoiceRate, character.ActiveColor, character.FontColor, AddTextEvent, AddTimerEvent), character.FilePath);
                _logReaders.Add(reader);
                RunningFiles[character.FilePath] = true;
              }
            }

            (Application.Current?.MainWindow as MainWindow)?.ShowTriggersEnabled(_logReaders.Count > 0);
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
                _logReaders.Add(new LogReader(new TriggerProcessor(TriggerStateManager.DefaultUser, TriggerStateManager.DefaultUser,
                  ConfigUtil.PlayerName, config.Voice, config.VoiceRate, null, null, AddTextEvent, AddTimerEvent), currentFile));
                ((MainWindow)Application.Current?.MainWindow)?.ShowTriggersEnabled(true);

                // only 1 running file in basic mode
                RunningFiles.Clear();
                RunningFiles[currentFile] = true;
              }
            }
            else
            {
              ((MainWindow)Application.Current?.MainWindow)?.ShowTriggersEnabled(false);
              RunningFiles.Clear();
            }
          }
        }

        EventsProcessorsUpdated?.Invoke(true);
      }
    }

    private void TriggersDoUpdate(object sender, EventArgs e)
    {
      _triggerUpdateTimer.Stop();
      CloseOverlays();
      GetProcessors().ForEach(p => p.UpdateActiveTriggers());
    }

    private void CloseOverlay(string id, params Dictionary<string, OverlayWindowData>[] windowList)
    {
      if (id != null)
      {
        UiUtil.InvokeAsync(() =>
        {
          foreach (var windows in windowList)
          {
            windows.Remove(id, out var windowData);
            windowData?.TheWindow.Close();
          }

          _defaultTextOverlay = null;
          _defaultTimerOverlay = null;
        });
      }
    }

    private void CloseOverlays(params Dictionary<string, OverlayWindowData>[] windowList)
    {
      UiUtil.InvokeAsync(() =>
      {
        foreach (var windows in windowList)
        {
          foreach (var windowData in windows.Values)
          {
            windowData?.TheWindow?.Close();
          }

          windows.Clear();
        }

        _defaultTextOverlay = null;
        _defaultTimerOverlay = null;
      });
    }

    private List<TriggerProcessor> GetProcessors()
    {
      var list = new List<TriggerProcessor>();
      lock (_logReaders)
      {
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
      }
      return list;
    }

    private void TimerTick(object sender, EventArgs e)
    {
      _timerIncrement++;
      WindowTick(_timerWindows, _timerOverlayTimer, _timerIncrement);

      if (_timerIncrement == 10)
      {
        _timerIncrement = 0;
      }
    }

    private void WindowTick(Dictionary<string, OverlayWindowData> windows, DispatcherTimer dispatchTimer, int increment = 10)
    {
      var removeList = new List<string>();
      var data = GetProcessors().SelectMany(processor => processor.GetActiveTimers()).ToList();

      foreach (var kv in windows)
      {
        var done = false;
        var shortTick = false;
        if (kv.Value is { } windowData)
        {
          if (windowData.TheWindow is TextOverlayWindow textWindow)
          {
            done = textWindow.Tick();
          }
          else if (windowData.TheWindow is TimerOverlayWindow timerWindow)
          {
            // full tick every 500ms
            if (increment == 10)
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
        if (windows.Remove(id, out var windowData))
        {
          windowData.TheWindow?.Close();
        }
      }

      if (windows.Count == 0)
      {
        dispatchTimer.Stop();
      }
    }

    private void AddTextEvent(string text, Trigger trigger, string fontColor)
    {
      var beginTicks = DateTime.UtcNow.Ticks;
      fontColor ??= trigger.FontColor;
      UiUtil.InvokeAsync(() =>
      {
        var textOverlayFound = false;
        if (trigger.SelectedOverlays != null)
        {
          foreach (var overlayId in trigger.SelectedOverlays)
          {
            if (!_textWindows.TryGetValue(overlayId, out var windowData))
            {
              if (TriggerStateManager.Instance.GetOverlayById(overlayId) is { OverlayData.IsTextOverlay: true } node)
              {
                windowData = GetTextWindowData(node);
              }
            }

            if (windowData != null)
            {
              var brush = UiUtil.GetBrush(fontColor);
              (windowData.TheWindow as TextOverlayWindow)?.AddTriggerText(text, beginTicks, brush);
              textOverlayFound = true;
            }
          }
        }

        if (!textOverlayFound && GetDefaultTextOverlay() is { } node2)
        {
          if (!_textWindows.TryGetValue(node2.Id, out var windowData))
          {
            windowData = GetTextWindowData(node2);
          }

          // using default
          var brush = UiUtil.GetBrush(fontColor);
          (windowData?.TheWindow as TextOverlayWindow)?.AddTriggerText(text, beginTicks, brush);
          textOverlayFound = true;
        }

        if (textOverlayFound && !_textOverlayTimer.IsEnabled)
        {
          _textOverlayTimer.Start();
        }
      }, DispatcherPriority.Render);
      return;

      OverlayWindowData GetTextWindowData(TriggerNode node)
      {
        var windowData = new OverlayWindowData { TheWindow = new TextOverlayWindow(node) };
        _textWindows[node.Id] = windowData;
        windowData.TheWindow.Show();
        return windowData;
      }
    }

    private void AddTimerEvent(Trigger trigger, List<TimerData> data)
    {
      UiUtil.InvokeAsync(() =>
      {
        var timerOverlayFound = false;
        if (trigger.SelectedOverlays != null)
        {
          foreach (var overlayId in trigger.SelectedOverlays)
          {
            if (!_timerWindows.TryGetValue(overlayId, out var windowData))
            {
              if (TriggerStateManager.Instance.GetOverlayById(overlayId) is { OverlayData.IsTimerOverlay: true } node)
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

        if (!timerOverlayFound && GetDefaultTimerOverlay() is { } node2)
        {
          if (!_timerWindows.TryGetValue(node2.Id, out _))
          {
            GetTimerWindowData(node2, data);
          }

          // using default
          timerOverlayFound = true;
        }

        if (timerOverlayFound && !_timerOverlayTimer.IsEnabled)
        {
          _timerOverlayTimer.Start();
        }
      }, DispatcherPriority.Render);
      return;

      OverlayWindowData GetTimerWindowData(TriggerNode node, List<TimerData> timerData)
      {
        var windowData = new OverlayWindowData { TheWindow = new TimerOverlayWindow(node) };
        _timerWindows[node.Id] = windowData;
        windowData.TheWindow.Show();
        ((TimerOverlayWindow)windowData.TheWindow).Tick(timerData);
        return windowData;
      }
    }

    private TriggerNode GetDefaultTextOverlay()
    {
      if (_defaultTextOverlay == null)
      {
        if (TriggerStateManager.Instance.GetDefaultTextOverlay() is { } overlay)
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

    private TriggerNode GetDefaultTimerOverlay()
    {
      if (_defaultTimerOverlay == null)
      {
        if (TriggerStateManager.Instance.GetDefaultTimerOverlay() is { } overlay)
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
