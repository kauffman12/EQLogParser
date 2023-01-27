using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    private TimerOverlayWindow OverlayWindow = null;

    public TriggerOverlayManager()
    {
      var json = ConfigUtil.ReadConfigFile(OVERLAY_FILE);
      OverlayNodes = (json != null) ? JsonSerializer.Deserialize<TriggerNode>(json, new JsonSerializerOptions { IncludeFields = true }) : new TriggerNode();
      TriggerManager.Instance.EventsNewTimer += EventsNewTimer;
      TriggerManager.Instance.EventsUpdateTimer += EventsUpdateTimer;
      CountdownTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 250) };
      CountdownTimer.Tick += CountdownTimerTick;
      OverlayUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 1) };
      OverlayUpdateTimer.Tick += OverlayDataUpdated;
    }

    public void Init()
    {

    }

    internal TriggerTreeViewNode GetOverlayTreeView() => TriggerUtil.GetTreeView(OverlayNodes, "Overlays");

    internal List<string> GetTimerOverlayItems()
    {
      var list = new List<string> { "No Overlay" };
      lock (OverlayNodes)
      {
        OverlayNodes.Nodes.ForEach(node => list.Add(node.OverlayData.Name + " (" + node.OverlayData.Id + ")"));
      }
      return list;
    }

    internal Overlay GetTimerOverlayById(string id)
    {
      Overlay data = null;
      lock (OverlayNodes)
      {
        if (OverlayNodes.Nodes.Find(node => node.OverlayData.Id == id && node.OverlayData.IsTimerOverlay) is TriggerNode node)
        {
          data = node.OverlayData;
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
      if (OverlayWindow.Tick())
      {
        CountdownTimer.Stop();
        OverlayWindow.Close();
        OverlayWindow = null;
      }
    }

    private void EventsNewTimer(object sender, Trigger e)
    {
      StartTimer(e.Name, e.DurationSeconds, false);
    }

    private void EventsUpdateTimer(object sender, Trigger e)
    {
      StartTimer(e.Name, e.DurationSeconds, true);
    }

    private void StartTimer(string name, long seconds, bool update)
    {
      var endTime = DateUtil.ToDouble(DateTime.Now) + seconds;
      Application.Current.Dispatcher.InvokeAsync(() =>
      {
        if (OverlayWindow == null)
        {
          OverlayWindow = new TimerOverlayWindow("test");
          OverlayWindow.Show();
        }

        if (update)
        {
          OverlayWindow.ResetTimer(name, endTime);
        }
        else
        {
          OverlayWindow.CreateTimer(name, endTime);
        }

        if (!CountdownTimer.IsEnabled)
        {
          CountdownTimer.Start();
        }
      });
    }
  }
}
