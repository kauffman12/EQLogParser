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

    private readonly string OVERLAY_FILE = "triggerOverlays.json";
    private readonly TriggerNode OverlayNodes;
    private readonly DispatcherTimer CountdownTimer;
    private readonly DispatcherTimer OverlayUpdateTimer;
    private readonly ConcurrentDictionary<string, TimerOverlayWindow> TimerWindows = new ConcurrentDictionary<string, TimerOverlayWindow>();
    private readonly ConcurrentDictionary<string, TimerOverlayWindow> PreviewTimerWindows = new ConcurrentDictionary<string, TimerOverlayWindow>();

    public TriggerOverlayManager()
    {
      var json = ConfigUtil.ReadConfigFile(OVERLAY_FILE);

      OverlayNodes = (json != null) ? JsonSerializer.Deserialize<TriggerNode>(json, new JsonSerializerOptions { IncludeFields = true }) : new TriggerNode();
      OverlayNodes.Nodes?.ForEach(node =>
      {
        Application.Current.Resources["TimerOverlayText-" + node.OverlayData.Id] = node.OverlayData.Name;
        // copy initializes other resources
        TriggerUtil.Copy(new TimerOverlayPropertyModel(), node.OverlayData);
      });

      CountdownTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 250) };
      OverlayUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 1) };
      OverlayUpdateTimer.Tick += OverlayDataUpdated;
    }

    internal void Start()
    {
      TriggerManager.Instance.EventsNewTimer += EventsNewTimer;
      TriggerManager.Instance.EventsUpdateTimer += EventsUpdateTimer;
      CountdownTimer.Tick += CountdownTimerTick;
    }

    internal void Stop()
    {
      TriggerManager.Instance.EventsNewTimer -= EventsNewTimer;
      TriggerManager.Instance.EventsUpdateTimer -= EventsUpdateTimer;
      CountdownTimer.Stop();
      CountdownTimer.Tick -= CountdownTimerTick;
      TimerWindows.ForEach(keypair => keypair.Value.Close());
      TimerWindows.Clear();
      SaveOverlays();
    }


    internal TriggerTreeViewNode GetOverlayTreeView() => TriggerUtil.GetTreeView(OverlayNodes, "Overlays");
    private void EventsNewTimer(object sender, Trigger e) => StartTimer(e, false);
    private void EventsUpdateTimer(object sender, Trigger e) => StartTimer(e, true);

    internal void PreviewTimerOverlay(string id)
    {
      if (!PreviewTimerWindows.ContainsKey(id))
      {
        var beginTime = DateUtil.ToDouble(DateTime.Now);
        PreviewTimerWindows[id] = new TimerOverlayWindow(id, true);
        PreviewTimerWindows[id].CreateTimer("Trigger Name #1", beginTime + 200, true);
        PreviewTimerWindows[id].CreateTimer("Trigger Name #2", beginTime + 100, true);
        PreviewTimerWindows[id].CreateTimer("Trigger Name #3", beginTime + 250, true);
        PreviewTimerWindows[id].CreateTimer("Trigger Name #4", beginTime + 60, true);
        PreviewTimerWindows[id].CreateTimer("Trigger Name #5", beginTime + 180, true);
        PreviewTimerWindows[id].Show();
      }
      else
      {
        PreviewTimerWindows[id].Close();
        PreviewTimerWindows.TryRemove(id, out _);
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
      var list = new List<string> { "No Overlay" };
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
      var list = new List<string> { "No Overlay" };
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

    private void OverlayDataUpdated(object sender, EventArgs e)
    {
      OverlayUpdateTimer.Stop();
      SaveOverlays();
    }

    private void CountdownTimerTick(object sender, EventArgs e)
    {
      var removed = new List<string>();
      foreach (var keypair in TimerWindows)
      {
        if (keypair.Value.Tick())
        {
          CountdownTimer.Stop();
          removed.Add(keypair.Key);
        }
      }

      foreach (var id in removed)
      {
        if (TimerWindows.TryRemove(id, out var timer))
        {
          timer.Close();
        }
      }

      if (TimerWindows.Count == 0)
      {
        CountdownTimer.Stop();
      }
    }

    private void StartTimer(Trigger trigger,  bool update)
    {
      var endTime = DateUtil.ToDouble(DateTime.Now) + trigger.DurationSeconds;
      Application.Current.Dispatcher.InvokeAsync(() =>
      {
        if (!string.IsNullOrEmpty(trigger.SelectedTimerOverlay) && !"No Overlay".Equals(trigger.SelectedTimerOverlay))
        {
          // check if it's even enabled
          if (GetTimerOverlayById(trigger.SelectedTimerOverlay, out bool isEnabled) != null)
          {
            if (isEnabled)
            {
              var needShow = false;
              if (!TimerWindows.TryGetValue(trigger.SelectedTimerOverlay, out var window))
              {
                window = new TimerOverlayWindow(trigger.SelectedTimerOverlay);
                TimerWindows[trigger.SelectedTimerOverlay] = window;
                needShow = true;
              }

              if (needShow || !update)
              {
                window.CreateTimer(trigger.Name, endTime);
              }
              else
              {
                window.ResetTimer(trigger.Name, endTime);
              }

              if (needShow)
              {
                window.Show();
              }

              if (!CountdownTimer.IsEnabled)
              {
                CountdownTimer.Start();
              }
            }
          }
          else
          {
            // overlay no longer exists?
            trigger.SelectedTimerOverlay = "No Overlay";
            TriggerManager.Instance.UpdateTriggers();
          }
        }
      });
    }
  }
}
