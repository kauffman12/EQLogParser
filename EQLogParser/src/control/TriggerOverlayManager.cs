using Syncfusion.Data.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  class TriggerOverlayManager
  {
    internal event EventHandler<Overlay> EventsUpdateOverlay;
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private readonly string OVERLAY_FILE = "triggerOverlays.json";
    private readonly TriggerNode OverlayNodes;
    private readonly DispatcherTimer TextOverlayTimer;
    private readonly DispatcherTimer TimerOverlayTimer;
    private readonly DispatcherTimer OverlayUpdateTimer;
    private readonly ConcurrentDictionary<string, Window> TextWindows = new ConcurrentDictionary<string, Window>();
    private readonly ConcurrentDictionary<string, Window> TimerWindows = new ConcurrentDictionary<string, Window>();
    private readonly ConcurrentDictionary<string, TextOverlayWindow> PreviewTextWindows = new ConcurrentDictionary<string, TextOverlayWindow>();
    private readonly ConcurrentDictionary<string, TimerOverlayWindow> PreviewTimerWindows = new ConcurrentDictionary<string, TimerOverlayWindow>();
    private readonly Dictionary<string, SolidColorBrush> BrushCache = new Dictionary<string, SolidColorBrush>();
    internal static TriggerOverlayManager Instance = new TriggerOverlayManager();

    public TriggerOverlayManager()
    {
      var json = ConfigUtil.ReadConfigFile(OVERLAY_FILE);

      if (json != null)
      {
        try
        {
          OverlayNodes = JsonSerializer.Deserialize<TriggerNode>(json, new JsonSerializerOptions { IncludeFields = true });
        }
        catch (Exception ex)
        {
          LOG.Error("Error Reading " + OVERLAY_FILE, ex);
          OverlayNodes = new TriggerNode();
        }
      }
      else
      {
        OverlayNodes = new TriggerNode();
      }

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
      OverlayUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 750) };
      OverlayUpdateTimer.Tick += OverlayDataUpdated;
    }

    internal ObservableCollection<ComboBoxItemDetails> GetTextOverlayItems(List<string> overlayIds) => GetOverlayItems(overlayIds, true);
    internal ObservableCollection<ComboBoxItemDetails> GetTimerOverlayItems(List<string> overlayIds) => GetOverlayItems(overlayIds, false);
    internal void Update(Overlay overlay) => EventsUpdateOverlay?.Invoke(this, overlay);

    internal TriggerTreeViewNode GetOverlayTreeView()
    {
      lock (OverlayNodes)
      {
        return TriggerUtil.GetTreeView(OverlayNodes, "Overlays");
      }
    }

    internal void Start()
    {
      TriggerManager.Instance.EventsNewTimer += EventsAddTimer;
      TriggerManager.Instance.EventsAddText += EventsAddText;
      TextOverlayTimer.Tick += TextTick;
      TimerOverlayTimer.Tick += TimerTick;
    }

    internal void Stop()
    {
      TriggerManager.Instance.EventsNewTimer -= EventsAddTimer;
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
      BrushCache.Clear();
    }

    internal void CloseOverlay(string overlayId)
    {
      if (!string.IsNullOrEmpty(overlayId))
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
    }

    internal void UpdatePreviewPosition(Overlay model)
    {
      Window window = null;
      var overlay = GetTextOverlayById(model.Id, out _);
      if (overlay != null)
      {
        if (PreviewTextWindows.TryGetValue(overlay.Id, out TextOverlayWindow textWindow))
        {
          window = textWindow;
        }
      }
      else
      {
        overlay = GetTimerOverlayById(model.Id, out _);
        if (overlay != null)
        {
          if (PreviewTimerWindows.TryGetValue(overlay.Id, out TimerOverlayWindow timerWindow))
          {
            window = timerWindow;
          }
        }
      }

      if (window != null)
      {
        window.Top = model.Top;
        window.Left = model.Left;
        window.Height = model.Height;
        window.Width = model.Width;
      }
    }

    internal void PreviewTextOverlay(Overlay model)
    {
      if (GetTextOverlayById(model.Id, out _) is Overlay overlay)
      {
        if (!PreviewTextWindows.ContainsKey(overlay.Id))
        {
          var beginTicks = DateTime.Now.Ticks;
          PreviewTextWindows[overlay.Id] = new TextOverlayWindow(overlay, true);
          PreviewTextWindows[overlay.Id].AddTriggerText("Example Message", beginTicks, null);
          PreviewTextWindows[overlay.Id].AddTriggerText("Example Message #2", beginTicks, null);
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
          PreviewTimerWindows[overlay.Id] = new TimerOverlayWindow(overlay, true);
          PreviewTimerWindows[overlay.Id].CreatePreviewTimer("Example Trigger Name", "03:00", 90.0);
          PreviewTimerWindows[overlay.Id].CreatePreviewTimer("Example Trigger Name #2", "02:00", 60.0);
          PreviewTimerWindows[overlay.Id].CreatePreviewTimer("Example Trigger Name #3", "01:00", 30.0);
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
      Application.Current?.Dispatcher.InvokeAsync(() =>
      {
        lock (OverlayNodes)
        {
          try
          {
            var json = JsonSerializer.Serialize(OverlayNodes, new JsonSerializerOptions { IncludeFields = true });
            ConfigUtil.WriteConfigFile(OVERLAY_FILE, json);
          }
          catch (Exception ex)
          {
            LOG.Error("Error Saving " + OVERLAY_FILE, ex);
          }
        }
      });
    }

    private void TextTick(object sender, EventArgs e) => WindowTick(TextWindows, TextOverlayTimer);
    private void TimerTick(object sender, EventArgs e) => WindowTick(TimerWindows, TimerOverlayTimer);

    private void WindowTick(ConcurrentDictionary<string, Window> windows, DispatcherTimer dispatchTimer)
    {
      var removed = new List<string>();
      var data = TriggerManager.Instance.GetActiveTimers();
      foreach (var keypair in windows)
      {
        var done = false;
        if (keypair.Value is TextOverlayWindow textWindow)
        {
          done = textWindow.Tick();
        }
        else if (keypair.Value is TimerOverlayWindow timerWindow)
        {
          done = timerWindow.Tick(data);
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
      var beginTicks = DateTime.Now.Ticks;
      var fontColor = e.CustomFont as string;
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
                if (!TextWindows.TryGetValue(overlayId, out Window window))
                {
                  window = new TextOverlayWindow(overlay);
                  TextWindows[overlayId] = window;
                  window.Show();
                }

                SolidColorBrush brush = null;
                if (!string.IsNullOrEmpty(fontColor))
                {
                  if (!BrushCache.TryGetValue(fontColor, out brush))
                  {
                    brush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(fontColor) };
                    BrushCache[fontColor] = brush;
                  }
                }

                (window as TextOverlayWindow).AddTriggerText(e.Text, beginTicks, brush);

                if (!TextOverlayTimer.IsEnabled)
                {
                  TextOverlayTimer.Start();
                }
              }
            }
          });
        }
      }, DispatcherPriority.Render);
    }

    private void EventsAddTimer(object sender, Trigger trigger)
    {
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
                if (!TimerWindows.TryGetValue(overlayId, out Window window))
                {
                  window = new TimerOverlayWindow(overlay);
                  TimerWindows[overlayId] = window;
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
      }, DispatcherPriority.Render);
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
  }
}
