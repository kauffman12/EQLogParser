using System;
using System.Windows;
using System.Windows.Threading;

namespace EQLogParser
{
  class OverlayManager
  {
    internal static OverlayManager Instance = new OverlayManager();
    
    private readonly DispatcherTimer CountdownTimer;
    private TimerOverlay OverlayWindow = null;

    public OverlayManager()
    {
      TriggerManager.Instance.EventsNewTimer += EventsNewTimer;
      TriggerManager.Instance.EventsUpdateTimer += EventsUpdateTimer;
      CountdownTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 250) };
      CountdownTimer.Tick += CountdownTimerTick;
    }

    public void Init()
    {

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
          OverlayWindow = new TimerOverlay();
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
