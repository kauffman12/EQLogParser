using Syncfusion.Data.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace EQLogParser
{
  class TriggerOverlayManager
  {
    internal static TriggerOverlayManager Instance = new TriggerOverlayManager();

    internal const string NO_OVERLAY = "No Overlay";
    private readonly string OVERLAY_FILE = "triggerOverlays.json";
    private readonly TriggerNode OverlayNodes;
    private readonly DispatcherTimer TextOverlayTimer;
    private readonly DispatcherTimer TimerOverlayTimer;
    private readonly DispatcherTimer OverlayUpdateTimer;
    private readonly ConcurrentDictionary<string, Window> TextWindows = new ConcurrentDictionary<string, Window>();
    private readonly ConcurrentDictionary<string, Window> TimerWindows = new ConcurrentDictionary<string, Window>();
    private readonly ConcurrentDictionary<string, TextOverlayWindow> PreviewTextWindows = new ConcurrentDictionary<string, TextOverlayWindow>();
    private readonly ConcurrentDictionary<string, TimerOverlayWindow> PreviewTimerWindows = new ConcurrentDictionary<string, TimerOverlayWindow>();

    public TriggerOverlayManager()
    {
      var json = ConfigUtil.ReadConfigFile(OVERLAY_FILE);

      OverlayNodes = (json != null) ? JsonSerializer.Deserialize<TriggerNode>(json, new JsonSerializerOptions { IncludeFields = true }) : new TriggerNode();
      OverlayNodes.Nodes?.ForEach(node =>
      {
        Application.Current.Resources["OverlayText-" + node.OverlayData.Id] = node.OverlayData.Name;
        // copy initializes other resources
        if (node.OverlayData.IsTextOverlay)
        {
          TriggerUtil.Copy(new TextOverlayPropertyModel(), node.OverlayData);
        }
        else
        {
          TriggerUtil.Copy(new TimerOverlayPropertyModel(), node.OverlayData);
        }
      });

      TextOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = new TimeSpan(0, 0, 0, 0, 500) };
      TimerOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = new TimeSpan(0, 0, 0, 0, 500) };
      OverlayUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 1) };
      OverlayUpdateTimer.Tick += OverlayDataUpdated;
    }

    internal void Start()
    {
      TriggerManager.Instance.EventsNewTimer += EventsNewTimer;
      TriggerManager.Instance.EventsUpdateTimer += EventsUpdateTimer;
      TriggerManager.Instance.EventsAddText += EventsAddText;
      TextOverlayTimer.Tick += TextTick;
      TimerOverlayTimer.Tick += TimerTick;
    }

    internal void Stop()
    {
      TriggerManager.Instance.EventsNewTimer -= EventsNewTimer;
      TriggerManager.Instance.EventsUpdateTimer -= EventsUpdateTimer;
      TriggerManager.Instance.EventsAddText -= EventsAddText;
      TextOverlayTimer.Stop();
      TimerOverlayTimer.Stop();
      TextOverlayTimer.Tick -= TextTick;
      TimerOverlayTimer.Tick -= TimerTick;
      TextWindows.ForEach(keypair => keypair.Value.Close());
      TextWindows.Clear();
      TimerWindows.ForEach(keypair => keypair.Value.Close());
      TimerWindows.Clear();
      SaveOverlays();
    }


    internal TriggerTreeViewNode GetOverlayTreeView() => TriggerUtil.GetTreeView(OverlayNodes, "Overlays");
    private void EventsNewTimer(object sender, dynamic e) => StartTimer(e, false);
    private void EventsUpdateTimer(object sender, dynamic e) => StartTimer(e, true);

    internal void PreviewTextOverlay(string id)
    {
      if (!PreviewTextWindows.ContainsKey(id))
      {
        var beginTime = DateUtil.ToDouble(DateTime.Now);
        PreviewTextWindows[id] = new TextOverlayWindow(id, true);
        PreviewTextWindows[id].AddTriggerText("Example Message", beginTime);
        PreviewTextWindows[id].AddTriggerText("Example Message #2", beginTime);
        PreviewTextWindows[id].Show();
      }
      else
      {
        PreviewTextWindows[id].Close();
        PreviewTextWindows.TryRemove(id, out _);
      }
    }

    internal void PreviewTimerOverlay(string id)
    {
      if (!PreviewTimerWindows.ContainsKey(id))
      {
        var beginTime = DateUtil.ToDouble(DateTime.Now);
        PreviewTimerWindows[id] = new TimerOverlayWindow(id, true);
        PreviewTimerWindows[id].CreateTimer("Example Trigger Name", beginTime + 200, true);
        PreviewTimerWindows[id].CreateTimer("Example Trigger Name #2", beginTime + 100, true);
        PreviewTimerWindows[id].CreateTimer("Example Trigger Name #3", beginTime + 250, true);
        PreviewTimerWindows[id].Show();
      }
      else
      {
        PreviewTimerWindows[id].Close();
        PreviewTimerWindows.TryRemove(id, out _);
      }
    }

    internal void ClosePreviewTextOverlay(string id)
    {
      if (PreviewTextWindows.TryGetValue(id, out TextOverlayWindow window))
      {
        window.Close();
        PreviewTextWindows.TryRemove(id, out _);
      }
    }

    internal void ClosePreviewTimerOverlay(string id)
    {
      if (PreviewTimerWindows.TryGetValue(id, out TimerOverlayWindow window))
      {
        window.Close();
        PreviewTimerWindows.TryRemove(id, out _);
      }
    }

    internal List<string> GetTextOverlayItems()
    {
      var list = new List<string> { NO_OVERLAY };
      lock (OverlayNodes)
      {
        if (OverlayNodes.Nodes != null)
        {
          OverlayNodes.Nodes?.Where(node => node.OverlayData?.IsTextOverlay == true)
            .ForEach(node => list.Add(node.OverlayData.Name + " (" + node.OverlayData.Id + ")"));
        }
      }
      return list;
    }

    internal List<string> GetTimerOverlayItems()
    {
      var list = new List<string> { NO_OVERLAY };
      lock (OverlayNodes)
      {
        if (OverlayNodes.Nodes != null)
        {
          OverlayNodes.Nodes?.Where(node => node.OverlayData?.IsTimerOverlay == true)
            .ForEach(node => list.Add(node.OverlayData.Name + " (" + node.OverlayData.Id + ")"));
        }
      }
      return list;
    }

    internal List<Overlay> GetTextOverlays()
    {
      var list = new List<Overlay>();
      lock (OverlayNodes)
      {
        if (OverlayNodes.Nodes != null)
        {
          list.AddRange(OverlayNodes.Nodes?.ToList().Where(node => node.OverlayData?.IsTextOverlay == true)
            .Select(node => node.OverlayData).OrderBy(overlay => overlay.Name));
        }
      }
      return list;
    }

    internal List<Overlay> GetTimerOverlays()
    {
      var list = new List<Overlay>();
      lock (OverlayNodes)
      {
        if (OverlayNodes.Nodes != null)
        {
          list.AddRange(OverlayNodes.Nodes?.ToList().Where(node => node.OverlayData?.IsTimerOverlay == true)
            .Select(node => node.OverlayData).OrderBy(overlay => overlay.Name));
        }
      }
      return list;
    }

    internal Overlay GetTextOverlayById(string id, out bool isEnabled)
    {
      isEnabled = false;
      Overlay data = null;
      lock (OverlayNodes)
      {
        if (OverlayNodes.Nodes != null)
        {
          if (OverlayNodes.Nodes?.Find(node => node.OverlayData.Id == id && node.OverlayData.IsTextOverlay) is TriggerNode node)
          {
            data = node.OverlayData;
            isEnabled = node.IsEnabled == true;
          }
        }
      }
      return data;
    }

    internal Overlay GetTimerOverlayById(string id, out bool isEnabled)
    {
      isEnabled = false;
      Overlay data = null;
      lock (OverlayNodes)
      {
        if (OverlayNodes.Nodes != null)
        {
          if (OverlayNodes.Nodes?.Find(node => node.OverlayData.Id == id && node.OverlayData.IsTimerOverlay) is TriggerNode node)
          {
            data = node.OverlayData;
            isEnabled = node.IsEnabled == true;
          }
        }
      }
      return data;
    }
    
    internal void UpdateOverlays()
    {
      OverlayUpdateTimer.Stop();
      OverlayUpdateTimer.Start();
    }

    internal void SaveOverlays()
    {
      lock (OverlayNodes)
      {
        var json = JsonSerializer.Serialize(OverlayNodes, new JsonSerializerOptions { IncludeFields = true });
        ConfigUtil.WriteConfigFile(OVERLAY_FILE, json);
      }
    }

    private void TextTick(object sender, EventArgs e) => WindowTick(TextWindows, TextOverlayTimer);
    private void TimerTick(object sender, EventArgs e) => WindowTick(TimerWindows, TimerOverlayTimer);

    private void WindowTick(ConcurrentDictionary<string, Window> windows, DispatcherTimer dispatchTimer)
    {
      var removed = new List<string>();
      foreach (var keypair in windows)
      {
        var done = false;
        if (keypair.Value is TextOverlayWindow textWindow)
        {
          done = textWindow.Tick();
        }
        else if (keypair.Value is TimerOverlayWindow timerWindow)
        {
          done = timerWindow.Tick();
        }

        if (done)
        {
          removed.Add(keypair.Key);
        }
      }

      foreach (var id in removed)
      {
        if (windows.TryRemove(id, out var window))
        {
          window.Close();
        }
      }

      if (windows.Count == 0)
      {
        dispatchTimer.Stop();
      }
    }

    private void OverlayDataUpdated(object sender, EventArgs e)
    {
      OverlayUpdateTimer.Stop();
      SaveOverlays();
    }

    private void EventsAddText(object sender, dynamic e)
    {
      var beginTime = DateUtil.ToDouble(DateTime.Now);
      Application.Current.Dispatcher.InvokeAsync(() =>
      {
        if (!string.IsNullOrEmpty(e.Trigger.SelectedTextOverlay) && !NO_OVERLAY.Equals(e.Trigger.SelectedTextOverlay))
        {
          // check if it's even enabled
          if (GetTextOverlayById(e.Trigger.SelectedTextOverlay, out bool isEnabled) != null)
          {
            if (isEnabled)
            {
              var needShow = false;
              if (!TextWindows.TryGetValue(e.Trigger.SelectedTextOverlay, out Window window))
              {
                window = new TextOverlayWindow(e.Trigger.SelectedTextOverlay);
                TextWindows[e.Trigger.SelectedTextOverlay] = window;
                needShow = true;
              }

              (window as TextOverlayWindow).AddTriggerText(e.Text, beginTime);

              if (needShow)
              {
                window.Show();
              }

              if (!TextOverlayTimer.IsEnabled)
              {
                TextOverlayTimer.Start();
              }
            }
          }
          else
          {
            // overlay no longer exists?
            e.Trigger.SelectedTextOverlay = NO_OVERLAY;
          }
        }
      });
    }

    private void StartTimer(dynamic e, bool update)
    {
      var trigger = e.Trigger;
      var timerName = e.Name;
      var endTime = DateUtil.ToDouble(DateTime.Now) + trigger.DurationSeconds;

      Application.Current.Dispatcher.InvokeAsync(() =>
      {
        if (!string.IsNullOrEmpty(trigger.SelectedTimerOverlay) && !NO_OVERLAY.Equals(trigger.SelectedTimerOverlay))
        {
          // check if it's even enabled
          if (GetTimerOverlayById(trigger.SelectedTimerOverlay, out bool isEnabled) != null)
          {
            if (isEnabled)
            {
              var needShow = false;
              if (!TimerWindows.TryGetValue(trigger.SelectedTimerOverlay, out Window window))
              {
                window = new TimerOverlayWindow(trigger.SelectedTimerOverlay);
                TimerWindows[trigger.SelectedTimerOverlay] = window;
                needShow = true;
              }

              if (needShow || !update)
              {
                (window as TimerOverlayWindow).CreateTimer(timerName, endTime);
              }
              else
              {
                (window as TimerOverlayWindow).ResetTimer(timerName, endTime);
              }

              if (needShow)
              {
                window.Show();
              }

              if (!TimerOverlayTimer.IsEnabled)
              {
                TimerOverlayTimer.Start();
              }
            }
          }
          else
          {
            // overlay no longer exists?
            trigger.SelectedTimerOverlay = NO_OVERLAY;
          }
        }
      });
    }
  }
}
