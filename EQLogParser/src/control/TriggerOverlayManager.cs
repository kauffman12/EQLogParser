using Syncfusion.Data.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace EQLogParser
{
  class TriggerOverlayManager
  {
    internal static TriggerOverlayManager Instance = new TriggerOverlayManager();

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

    internal TriggerTreeViewNode GetOverlayTreeView() => TriggerUtil.GetTreeView(OverlayNodes, "Overlays");
    internal ObservableCollection<ComboBoxItemDetails> GetTextOverlayItems(List<string> overlayIds) => GetOverlayItems(overlayIds, true);
    internal ObservableCollection<ComboBoxItemDetails> GetTimerOverlayItems(List<string> overlayIds) => GetOverlayItems(overlayIds, false);

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

    internal void CloseOverlay(string overlayId)
    {
      if (TimerWindows.TryRemove(overlayId, out var timerWindow))
      {
        timerWindow.Close();
      }
      else if (TextWindows.TryRemove(overlayId, out var textWindow))
      {
        textWindow.Close();
      }
    }

    internal void PreviewTextOverlay(Overlay model)
    {
      if (GetTextOverlayById(model.Id, out _) is Overlay overlay)
      {
        if (!PreviewTextWindows.ContainsKey(overlay.Id))
        {
          var beginTime = DateUtil.ToDouble(DateTime.Now);
          PreviewTextWindows[overlay.Id] = new TextOverlayWindow(overlay, true);
          PreviewTextWindows[overlay.Id].AddTriggerText("Example Message", beginTime);
          PreviewTextWindows[overlay.Id].AddTriggerText("Example Message #2", beginTime);
          PreviewTextWindows[overlay.Id].Show();
        }
        else
        {
          PreviewTextWindows[overlay.Id].Close();
          PreviewTextWindows.TryRemove(overlay.Id, out _);
        }
      }
    }

    internal void PreviewTimerOverlay(Overlay model)
    {
      if (GetTimerOverlayById(model.Id, out _) is Overlay overlay)
      {
        if (!PreviewTimerWindows.ContainsKey(overlay.Id))
        {
          var beginTime = DateUtil.ToDouble(DateTime.Now);
          PreviewTimerWindows[overlay.Id] = new TimerOverlayWindow(overlay, true);
          PreviewTimerWindows[overlay.Id].CreateTimer("Example Trigger Name", beginTime + 200, new Trigger(), true);
          PreviewTimerWindows[overlay.Id].CreateTimer("Example Trigger Name #2", beginTime + 100, new Trigger(), true);
          PreviewTimerWindows[overlay.Id].CreateTimer("Example Trigger Name #3", beginTime + 250, new Trigger(),true);
          PreviewTimerWindows[overlay.Id].Show();
        }
        else
        {
          PreviewTimerWindows[overlay.Id].Close();
          PreviewTimerWindows.TryRemove(overlay.Id, out _);
        }
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

    private void EventsNewTimer(object sender, dynamic e) => StartTimer(e, false);
    private void EventsUpdateTimer(object sender, dynamic e) => StartTimer(e, true);
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
      var trigger = e.Trigger as Trigger;
      var beginTime = DateUtil.ToDouble(DateTime.Now);
      Application.Current.Dispatcher.InvokeAsync(() =>
      {
        if (trigger.SelectedOverlays != null)
        {
          trigger.SelectedOverlays.ForEach(overlayId =>
          {
            // check if it's even enabled
            if (GetTextOverlayById(overlayId, out bool isEnabled) is Overlay overlay)
            {
              if (isEnabled)
              {
                var needShow = false;
                if (!TextWindows.TryGetValue(overlayId, out Window window))
                {
                  window = new TextOverlayWindow(overlay);
                  TextWindows[overlayId] = window;
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
          });
        }
      });
    }

    private ObservableCollection<ComboBoxItemDetails> GetOverlayItems(List<string> overlayIds, bool isTextOverlay)
    {
      var list = new ObservableCollection<ComboBoxItemDetails>();
      lock (OverlayNodes)
      {
        if (OverlayNodes.Nodes != null)
        {
          OverlayNodes.Nodes?.Where(node => node.OverlayData != null && node.OverlayData.IsTextOverlay == isTextOverlay)
            .ForEach(node =>
            {
              var id = node.OverlayData.Id;
              var name = node.OverlayData.Name + " (" + id + ")";
              var isChecked = overlayIds == null ? false : overlayIds.Contains(id);
              list.Add(new ComboBoxItemDetails { IsChecked = isChecked, Text = name, Value = id });
            });
        }
      }
      return list;
    }

    private void StartTimer(dynamic e, bool update)
    {
      var trigger = e.Trigger as Trigger;
      var timerName = e.Name;
      var endTime = DateUtil.ToDouble(DateTime.Now) + trigger.DurationSeconds;

      Application.Current.Dispatcher.InvokeAsync(() =>
      {
        if (trigger.SelectedOverlays != null)
        {
          trigger.SelectedOverlays.ForEach(overlayId =>
          {
            // check if it's even enabled
            if (GetTimerOverlayById(overlayId, out bool isEnabled) is Overlay overlay)
            {
              if (isEnabled)
              {
                var needShow = false;
                if (!TimerWindows.TryGetValue(overlayId, out Window window))
                {
                  window = new TimerOverlayWindow(overlay);
                  TimerWindows[overlayId] = window;
                  needShow = true;
                }

                if (needShow || (!update && overlay.TimerMode == 0))
                {
                  (window as TimerOverlayWindow).CreateTimer(timerName, endTime, trigger);
                }
                else
                {
                  (window as TimerOverlayWindow).ResetTimer(timerName, endTime, trigger);
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
          });
        }
      });
    }
  }
}
