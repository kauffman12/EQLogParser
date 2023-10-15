using Syncfusion.UI.Xaml.ProgressBar;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TimerBar.xaml
  /// </summary>
  public partial class TimerBar : UserControl
  {
    private enum State
    {
      None,
      Active,
      Idle,
      Reset
    };

    private string OverlayId;
    private State TheState = State.None;
    private TimerData LastTimerData;

    public TimerBar()
    {
      InitializeComponent();
    }

    internal void Init(string overlayId)
    {
      OverlayId = overlayId;
      progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, "TimerBarActiveColor-" + OverlayId);
      progress.SetResourceReference(ProgressBarBase.TrackColorProperty, "TimerBarTrackColor-" + OverlayId);
      progress.SetResourceReference(HeightProperty, "TimerBarHeight-" + OverlayId);
      time.SetResourceReference(TextBlock.FontSizeProperty, "TimerBarFontSize-" + OverlayId);
      title.SetResourceReference(TextBlock.FontSizeProperty, "TimerBarFontSize-" + OverlayId);
    }

    internal void Update(string displayName, string timeText, double remaining, TimerData timerData)
    {
      // only reset colors if the timer has been assigned to something else
      if (LastTimerData != timerData)
      {
        if (timerData?.FontColor != null)
        {
          var brush = TriggerUtil.GetBrush(timerData.FontColor);
          time.Foreground = brush;
          title.Foreground = brush;
        }
        else
        {
          time.SetResourceReference(TextBlock.ForegroundProperty, "TimerBarFontColor-" + OverlayId);
          title.SetResourceReference(TextBlock.ForegroundProperty, "TimerBarFontColor-" + OverlayId);
        }

        LastTimerData = timerData;
      }

      title.Text = displayName;
      time.Text = timeText;
      // 3 is an increasing timer. obviously.
      progress.Progress = timerData?.TimerType == 3 ? 100 - remaining : remaining;
    }

    internal void SetActive(TimerData timerData)
    {
      if (timerData?.ActiveColor != null)
      {
        if (TriggerUtil.GetBrush(timerData.ActiveColor) is var color && progress.ProgressColor != color)
        {
          progress.ProgressColor = color;
          TheState = State.None;
        }
      }
      else if (TheState != State.Active)
      {
        progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, "TimerBarActiveColor-" + OverlayId);
        TheState = State.Active;
      }
    }

    internal void SetReset()
    {
      if (TheState != State.Reset)
      {
        progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, "TimerBarResetColor-" + OverlayId);
        TheState = State.Reset;
      }
    }

    internal void SetIdle()
    {
      if (TheState != State.Idle)
      {
        progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, "TimerBarIdleColor-" + OverlayId);
        TheState = State.Idle;
      }
    }

    private void UnloadWindow(object sender, RoutedEventArgs e)
    {
      LastTimerData = null;
    }
  }
}
