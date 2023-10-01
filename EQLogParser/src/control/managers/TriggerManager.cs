using Syncfusion.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks.Dataflow;
using System.Windows;
using System.Windows.Threading;

namespace EQLogParser
{
  internal class TriggerManager
  {
    internal event Action<Trigger> EventsSelectTrigger;
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly Lazy<TriggerManager> _lazy = new Lazy<TriggerManager>(() => new TriggerManager());
    internal static TriggerManager Instance => _lazy.Value; // instance
    private readonly DispatcherTimer ConfigUpdateTimer;
    private readonly DispatcherTimer TriggerUpdateTimer;
    private readonly DispatcherTimer TextOverlayTimer;
    private readonly DispatcherTimer TimerOverlayTimer;
    private readonly Dictionary<string, OverlayWindowData> TextWindows = new Dictionary<string, OverlayWindowData>();
    private readonly Dictionary<string, OverlayWindowData> TimerWindows = new Dictionary<string, OverlayWindowData>();
    private readonly List<LogReader> LogReaders = new List<LogReader>();
    private readonly TriggerProcessor TestProcessor;
    private readonly BufferBlock<Tuple<string, double, bool>> TestBuffer = new BufferBlock<Tuple<string, double, bool>>();
    private int TimerIncrement = 0;

    public TriggerManager()
    {
      if (!TriggerStateManager.Instance.IsActive())
      {
        new MessageWindow("Trigger Database not available. In use by another EQLogParser?\r\nTrigger Management disabled until restart.",
          EQLogParser.Resource.Warning).Show();
        (Application.Current.MainWindow as MainWindow)?.DisableTriggers();
        return;
      }

      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += TriggerManagerEventsLogLoadingComplete;

      TriggerUtil.LoadOverlayStyles();
      ConfigUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1500) };
      ConfigUpdateTimer.Tick += ConfigDoUpdate;
      TriggerUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1500) };
      TriggerUpdateTimer.Tick += TriggersDoUpdate;
      TextOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = new TimeSpan(0, 0, 0, 0, 450) };
      TimerOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = new TimeSpan(0, 0, 0, 0, 50) };

      // testing
      TestProcessor = new TriggerProcessor(TriggerStateManager.DEFAULT_USER, AddTextEvent, AddTimerEvent);
      TestProcessor.LinkTo(TestBuffer);
    }

    internal bool Init() => true;
    internal BufferBlock<Tuple<string, double, bool>> GetTestBuffer() => TestBuffer;
    internal void Select(Trigger trigger) => EventsSelectTrigger?.Invoke(trigger);
    internal void SetVoice(string voice) => GetProcessors().ForEach(processor => processor.SetVoice(voice));
    internal void SetVoiceRate(int rate) => GetProcessors().ForEach(processor => processor.SetVoiceRate(rate));

    internal void ConfigUpdated()
    {
      ConfigUpdateTimer.Stop();
      ConfigUpdateTimer.Start();
    }

    internal void TriggersUpdated()
    {
      TriggerUpdateTimer.Stop();
      TriggerUpdateTimer.Start();
    }

    internal void CloseOverlay(string id)
    {
      TriggerUtil.CloseOverlay(TextWindows, id);
      TriggerUtil.CloseOverlay(TimerWindows, id);
    }

    internal void CloseOverlays()
    {
      TriggerUtil.CloseOverlays(TextWindows);
      TriggerUtil.CloseOverlays(TimerWindows);
    }

    internal void Stop()
    {
      TextOverlayTimer.Stop();
      TimerOverlayTimer.Stop();
      TextOverlayTimer.Tick -= TextTick;
      TimerOverlayTimer.Tick -= TimerTick;

      lock (LogReaders)
      {
        LogReaders.ForEach(reader => reader.Dispose());
        LogReaders.Clear();
      }
    }

    private void TextTick(object sender, EventArgs e) => WindowTick(TextWindows, TextOverlayTimer);
    private string ModLine(string text, string line) => string.IsNullOrEmpty(text) ? null : text.Replace("{l}", line, StringComparison.OrdinalIgnoreCase);
    private void TriggerManagerEventsLogLoadingComplete(string _) => ConfigDoUpdate(this, null);

    private void ConfigDoUpdate(object sender, EventArgs e)
    {
      ConfigUpdateTimer.Stop();
      UIUtil.InvokeAsync(() => CloseOverlays());

      if (TriggerStateManager.Instance.GetConfig() is TriggerConfig config)
      {
        if (!config.IsAdvanced)
        {
          lock (LogReaders)
          {
            LogReaders.ForEach(reader => reader.Dispose());
            LogReaders.Clear();

            if (config.IsEnabled)
            {
              if (MainWindow.CurrentLogFile != null)
              {
                LogReaders.Add(new LogReader(new TriggerProcessor(TriggerStateManager.DEFAULT_USER, AddTextEvent, AddTimerEvent),
                  MainWindow.CurrentLogFile));
              }

              LOG.Info("Triggers Active");
              TextOverlayTimer.Tick += TextTick;
              TimerOverlayTimer.Tick += TimerTick;
              (Application.Current.MainWindow as MainWindow).ShowTriggersEnabled(true);
            }
            else
            {
              LOG.Info("Triggers Have Been Deactivated");
              TextOverlayTimer.Stop();
              TimerOverlayTimer.Stop();
              TextOverlayTimer.Tick -= TextTick;
              TimerOverlayTimer.Tick -= TimerTick;
              (Application.Current.MainWindow as MainWindow).ShowTriggersEnabled(false);
            }
          }
        }
      }
    }

    private void TriggersDoUpdate(object sender, EventArgs e)
    {
      TriggerUpdateTimer.Stop();
      UIUtil.InvokeAsync(() => CloseOverlays());
      GetProcessors().ForEach(processor => processor.UpdateActiveTriggers());
    }

    private IEnumerable<TriggerProcessor> GetProcessors()
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

        list.Add(TestProcessor);
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

    private void AddTextEvent(string action, string text, Trigger trigger, MatchCollection matches, MatchCollection originalMatches = null)
    {
      if (!string.IsNullOrEmpty(text))
      {
        var beginTicks = DateTime.Now.Ticks;
        UIUtil.InvokeAsync(() =>
        {
          text = TriggerUtil.ProcessText(text, originalMatches);
          text = TriggerUtil.ProcessText(text, matches);
          text = ModLine(text, action);
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
