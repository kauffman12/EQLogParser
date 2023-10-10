using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks.Dataflow;
using System.Windows;
using System.Windows.Threading;

namespace EQLogParser
{
  internal class TriggerManager
  {
    internal event Action<bool> EventsProcessorsUpdated;
    internal event Action<string> EventsSelectTrigger;
    internal static TriggerManager Instance => _lazy.Value; // instance
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly Lazy<TriggerManager> _lazy = new Lazy<TriggerManager>(() => new TriggerManager());
    private readonly DispatcherTimer ConfigUpdateTimer;
    private readonly DispatcherTimer TriggerUpdateTimer;
    private readonly DispatcherTimer TextOverlayTimer;
    private readonly DispatcherTimer TimerOverlayTimer;
    private readonly Dictionary<string, OverlayWindowData> TextWindows = new Dictionary<string, OverlayWindowData>();
    private readonly Dictionary<string, OverlayWindowData> TimerWindows = new Dictionary<string, OverlayWindowData>();
    private readonly List<LogReader> LogReaders = new List<LogReader>();
    private TriggerProcessor TestProcessor = null;
    private int TimerIncrement = 0;

    public TriggerManager()
    {
      if (!TriggerStateManager.Instance.IsActive())
      {
        new MessageWindow("Trigger Database not available. In use by another EQLogParser?\r\nTrigger Management disabled until restart.",
          Resource.Warning).Show();
        (Application.Current?.MainWindow as MainWindow)?.DisableTriggers();
        return;
      }

      TriggerUtil.LoadOverlayStyles();
      ConfigUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1500) };
      TriggerUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1500) };
      TextOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = new TimeSpan(0, 0, 0, 0, 450) };
      TimerOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = new TimeSpan(0, 0, 0, 0, 50) };
      TriggerStateManager.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
      ConfigUpdateTimer.Tick += ConfigDoUpdate;
      TriggerUpdateTimer.Tick += TriggersDoUpdate;
      TextOverlayTimer.Tick += TextTick;
      TimerOverlayTimer.Tick += TimerTick;
    }

    internal void CloseOverlay(string id) => CloseOverlay(id, TextWindows, TimerWindows);
    internal void CloseOverlays() => CloseOverlays(TextWindows, TimerWindows);
    internal void Select(string id) => EventsSelectTrigger?.Invoke(id);
    internal void SetVoice(string voice) => GetProcessors().ForEach(p => p.SetVoice(voice));
    internal void SetVoiceRate(int rate) => GetProcessors().ForEach(p => p.SetVoiceRate(rate));

    internal void TriggersUpdated()
    {
      TriggerUpdateTimer.Stop();
      TriggerUpdateTimer.Start();
    }

    internal void Start()
    {
      TriggerUtil.LoadOverlayStyles();
      (Application.Current?.MainWindow as MainWindow).EventsLogLoadingComplete += TriggerManagerEventsLogLoadingComplete;
      TriggerConfigUpdateEvent(null);
    }

    internal void Stop()
    {
      (Application.Current?.MainWindow as MainWindow).EventsLogLoadingComplete -= TriggerManagerEventsLogLoadingComplete;

      lock (LogReaders)
      {
        LogReaders?.ForEach(reader => reader.Dispose());
        LogReaders?.Clear();
      }

      TextOverlayTimer?.Stop();
      TimerOverlayTimer?.Stop();
    }

    internal void SetTestProcessor(string playerId, string name, ISourceBlock<Tuple<string, double, bool>> source)
    {
      TestProcessor?.Dispose();
      TestProcessor = new TriggerProcessor(playerId, $"Trigger Tester ({name})", AddTextEvent, AddTimerEvent);
      TestProcessor.LinkTo(source);
      UIUtil.InvokeAsync(() => EventsProcessorsUpdated?.Invoke(true));
    }

    internal List<Tuple<string, ObservableCollection<AlertEntry>>> GetAlertLogs()
    {
      var list = new List<Tuple<string, ObservableCollection<AlertEntry>>>();
      foreach (var p in GetProcessors())
      {
        list.Add(Tuple.Create(p.CurrentCharacterName, p.AlertLog));
      }
      return list;
    }

    private void TextTick(object sender, EventArgs e) => WindowTick(TextWindows, TextOverlayTimer);

    private void TriggerManagerEventsLogLoadingComplete(string _)
    {
      // ignore event if in advanced mode
      if (TriggerStateManager.Instance.GetConfig() is TriggerConfig config && !config.IsAdvanced)
      {
        ConfigDoUpdate(this, null);
      }
    }

    private void TriggerConfigUpdateEvent(TriggerConfig _)
    {
      ConfigUpdateTimer?.Stop();
      ConfigUpdateTimer?.Start();
    }

    private void ConfigDoUpdate(object sender, EventArgs e)
    {
      ConfigUpdateTimer.Stop();
      UIUtil.InvokeNow(CloseOverlays);
      TextOverlayTimer?.Stop();
      TimerOverlayTimer?.Stop();

      if (TriggerStateManager.Instance.GetConfig() is TriggerConfig config)
      {
        lock (LogReaders)
        {
          if (config.IsAdvanced)
          {
            // Only clear out everything if switcehd from basic
            if (GetProcessors().FirstOrDefault(p => p.CurrentCharacterId == TriggerStateManager.DEFAULT_USER) != null)
            {
              LogReaders.ForEach(reader => reader.Dispose());
              LogReaders.Clear();
            }

            // remove stales readers first
            var toRemove = new List<LogReader>();
            var alreadyRunning = new List<string>();
            foreach (var reader in LogReaders)
            {
              // remove readers if the character no longer exists
              if (reader.GetProcessor() is TriggerProcessor processor)
              {
                var found = config.Characters.FirstOrDefault(character => character.Id == processor.CurrentCharacterId);
                if (found == null || !found.IsEnabled)
                {
                  reader.Dispose();
                  toRemove.Add(reader);
                }
                else
                {
                  alreadyRunning.Add(found.Id);
                }
              }
            }

            toRemove.ForEach(remove => LogReaders.Remove(remove));

            // add characters that aren't enabled yet
            foreach (var character in config.Characters)
            {
              if (character.IsEnabled && !alreadyRunning.Contains(character.Id))
              {
                LogReaders.Add(new LogReader(new TriggerProcessor(character.Id, character.Name, AddTextEvent, AddTimerEvent),
                  character.FilePath));
              }
            }

            if (LogReaders.Count > 0)
            {
              (Application.Current?.MainWindow as MainWindow).ShowTriggersEnabled(true);
            }
            else
            {
              (Application.Current?.MainWindow as MainWindow).ShowTriggersEnabled(false);
            }
          }
          else
          {
            // Basic always clear out everything
            LogReaders.ForEach(reader => reader.Dispose());
            LogReaders.Clear();

            if (config.IsEnabled)
            {
              if (MainWindow.CurrentLogFile != null)
              {
                LogReaders.Add(new LogReader(new TriggerProcessor(TriggerStateManager.DEFAULT_USER,
                  TriggerStateManager.DEFAULT_USER, AddTextEvent, AddTimerEvent), MainWindow.CurrentLogFile));
                (Application.Current?.MainWindow as MainWindow).ShowTriggersEnabled(true);
              }
            }
            else
            {
              (Application.Current?.MainWindow as MainWindow).ShowTriggersEnabled(false);
            }
          }
        }

        EventsProcessorsUpdated?.Invoke(true);
      }
    }

    private void TriggersDoUpdate(object sender, EventArgs e)
    {
      TriggerUpdateTimer.Stop();
      UIUtil.InvokeNow(CloseOverlays);
      GetProcessors().ForEach(p => p.UpdateActiveTriggers());
    }

    private static void CloseOverlay(string id, params Dictionary<string, OverlayWindowData>[] windowList)
    {
      if (id != null)
      {
        UIUtil.InvokeNow(() =>
        {
          foreach (var windows in windowList)
          {
            lock (windows)
            {
              windows.Remove(id, out var windowData);
              windowData?.TheWindow.Close();
            }
          }
        });
      }
    }

    private static void CloseOverlays(params Dictionary<string, OverlayWindowData>[] windowList)
    {
      UIUtil.InvokeNow(() =>
      {
        foreach (var windows in windowList)
        {
          lock (windows)
          {
            foreach (var windowData in windows.Values)
            {
              windowData?.TheWindow?.Close();
            }

            windows.Clear();
          }
        }
      });
    }

    private List<TriggerProcessor> GetProcessors()
    {
      var list = new List<TriggerProcessor>();
      lock (LogReaders)
      {
        foreach (var reader in LogReaders)
        {
          if (reader.GetProcessor() is TriggerProcessor processor)
          {
            list.Add(processor);
          }
        }

        if (TestProcessor != null)
        {
          list.Add(TestProcessor);
        }
      }
      return list;
    }

    private void TimerTick(object sender, EventArgs e)
    {
      TimerIncrement++;
      WindowTick(TimerWindows, TimerOverlayTimer, TimerIncrement);

      if (TimerIncrement == 10)
      {
        TimerIncrement = 0;
      }
    }

    private void WindowTick(Dictionary<string, OverlayWindowData> windows, DispatcherTimer dispatchTimer, int increment = 10)
    {
      var removeList = new List<string>();
      var data = GetProcessors().SelectMany(processor => processor.GetActiveTimers()).ToList();

      lock (windows)
      {
        foreach (var keypair in windows)
        {
          var done = false;
          var shortTick = false;
          if (keypair.Value is OverlayWindowData windowData)
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
                var nowTicks = DateTime.Now.Ticks;
                if (windowData.RemoveTicks == -1)
                {
                  windowData.RemoveTicks = nowTicks + (TimeSpan.TicksPerMinute * 2);
                }
                else if (nowTicks > windowData.RemoveTicks)
                {
                  removeList.Add(keypair.Key);
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
    }

    private void AddTextEvent(string text, Trigger trigger)
    {
      var beginTicks = DateTime.Now.Ticks;
      UIUtil.InvokeAsync(() =>
      {
        var textOverlayFound = false;

        trigger.SelectedOverlays?.ForEach(overlayId =>
        {
          OverlayWindowData windowData = null;
          lock (TextWindows)
          {
            if (!TextWindows.TryGetValue(overlayId, out windowData))
            {
              if (TriggerStateManager.Instance.GetOverlayById(overlayId) is TriggerNode node
                && node?.OverlayData?.IsTextOverlay == true)
              {
                windowData = GetWindowData(node);
              }
            }

            if (windowData != null)
            {
              var brush = TriggerUtil.GetBrush(trigger.FontColor);
              (windowData?.TheWindow as TextOverlayWindow).AddTriggerText(text, beginTicks, brush);
              textOverlayFound = true;
            }
          }
        });

        if (!textOverlayFound && TriggerStateManager.Instance.GetDefaultTextOverlay() is TriggerNode node)
        {
          lock (TextWindows)
          {
            if (!TextWindows.TryGetValue(node.Id, out var windowData))
            {
              windowData = GetWindowData(node);
            }

            // using default
            var brush = TriggerUtil.GetBrush(trigger.FontColor);
            (windowData?.TheWindow as TextOverlayWindow).AddTriggerText(text, beginTicks, brush);
            textOverlayFound = true;
          }
        }

        if (textOverlayFound && !TextOverlayTimer.IsEnabled)
        {
          TextOverlayTimer.Start();
        }
      }, DispatcherPriority.Render);

      OverlayWindowData GetWindowData(TriggerNode node)
      {
        var windowData = new OverlayWindowData { TheWindow = new TextOverlayWindow(node) };
        TextWindows[node.Id] = windowData;
        windowData.TheWindow.Show();
        return windowData;
      }
    }

    private void AddTimerEvent(Trigger trigger, List<TimerData> data)
    {
      UIUtil.InvokeAsync(() =>
      {
        var timerOverlayFound = false;
        trigger.SelectedOverlays?.ForEach(overlayId =>
        {
          OverlayWindowData windowData = null;
          lock (TimerWindows)
          {
            if (!TimerWindows.TryGetValue(overlayId, out windowData))
            {
              if (TriggerStateManager.Instance.GetOverlayById(overlayId) is TriggerNode node
                && node?.OverlayData?.IsTimerOverlay == true)
              {
                windowData = GetWindowData(node, data);
              }
            }

            // may not have found a timer overlay
            if (windowData != null)
            {
              timerOverlayFound = true;
            }
          }
        });

        if (!timerOverlayFound && TriggerStateManager.Instance.GetDefaultTimerOverlay() is TriggerNode node)
        {
          lock (TimerWindows)
          {
            if (!TimerWindows.TryGetValue(node.Id, out var windowData))
            {
              windowData = GetWindowData(node, data);
            }

            // using default
            timerOverlayFound = true;
          }
        }

        if (timerOverlayFound && !TimerOverlayTimer.IsEnabled)
        {
          TimerOverlayTimer.Start();
        }
      }, DispatcherPriority.Render);

      OverlayWindowData GetWindowData(TriggerNode node, List<TimerData> timerData)
      {
        var windowData = new OverlayWindowData { TheWindow = new TimerOverlayWindow(node) };
        TimerWindows[node.Id] = windowData;
        windowData.TheWindow.Show();
        ((TimerOverlayWindow)windowData?.TheWindow).Tick(timerData);
        return windowData;
      }
    }
  }
}
