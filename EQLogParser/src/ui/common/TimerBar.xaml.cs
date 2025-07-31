using Syncfusion.UI.Xaml.ProgressBar;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  public partial class TimerBar
  {
    public enum State
    {
      None,
      Active,
      Idle,
      Reset
    };

    private string _overlayId;
    private State _theState = State.None;
    private TimerData _lastTimerData;

    public TimerBar()
    {
      InitializeComponent();
      progress.SizeChanged += ProgressSizeChanged;
    }

    internal void Init(string overlayId)
    {
      _overlayId = overlayId;
      progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, "TimerBarActiveColor-" + _overlayId);
      progress.SetResourceReference(ProgressBarBase.TrackColorProperty, "TimerBarTrackColor-" + _overlayId);
      progress.SetResourceReference(HeightProperty, "TimerBarHeight-" + _overlayId);
      time.SetResourceReference(TextBlock.FontSizeProperty, "TimerBarFontSize-" + _overlayId);
      title.SetResourceReference(TextBlock.FontSizeProperty, "TimerBarFontSize-" + _overlayId);
      time.SetResourceReference(TextBlock.FontFamilyProperty, "TimerBarFontFamily-" + _overlayId);
      title.SetResourceReference(TextBlock.FontFamilyProperty, "TimerBarFontFamily-" + _overlayId);
      time.SetResourceReference(TextBlock.FontWeightProperty, "TimerBarFontWeight-" + _overlayId);
      title.SetResourceReference(TextBlock.FontWeightProperty, "TimerBarFontWeight-" + _overlayId);
    }

    internal State GetState() => _theState;

    internal void Update(string displayName, string timeText, double remaining, TimerData timerData)
    {
      // only reset colors if the timer has been assigned to something else
      if (_lastTimerData != timerData)
      {
        if (timerData?.FontColor != null && UiUtil.GetBrush(timerData.FontColor, false) is { } brush && brush.Color.A > 0)
        {
          if (time.Foreground != brush || title.Foreground != brush)
          {
            time.Foreground = brush;
            title.Foreground = brush;
          }
        }
        else
        {
          time.SetResourceReference(TextBlock.ForegroundProperty, "TimerBarFontColor-" + _overlayId);
          title.SetResourceReference(TextBlock.ForegroundProperty, "TimerBarFontColor-" + _overlayId);
        }

        _lastTimerData = timerData;
      }

      // set icon if needed
      if (theIcon.Source != timerData?.TimerIcon)
      {
        theIcon.Source = timerData?.TimerIcon;
        SetIconHeight();
      }

      // no need to animate short duration timers
      if (timerData != null && timerData.TimerType != 2)
      {
        // 3 is an increasing timer.
        var targetProgress = timerData.TimerType == 3 ? 100 - remaining : remaining;
        progress.Progress = targetProgress;
      }
      else
      {
        // 3 is an increasing timer.
        progress.Progress = timerData?.TimerType == 3 ? 100 - remaining : remaining;
      }

      title.Text = displayName;
      time.Text = timeText;
    }

    internal void SetActive(TimerData timerData)
    {
      if (timerData?.ActiveColor != null && UiUtil.GetBrush(timerData.ActiveColor, false) is { } brush && brush.Color.A > 0)
      {
        if (progress.ProgressColor != brush)
        {
          progress.ProgressColor = brush;
        }

        _theState = State.None;
      }
      else if (_theState != State.Active)
      {
        progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, "TimerBarActiveColor-" + _overlayId);
        _theState = State.Active;
      }
    }

    internal void SetReset()
    {
      if (_theState != State.Reset)
      {
        progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, "TimerBarResetColor-" + _overlayId);
        _theState = State.Reset;
      }
    }

    internal void SetIdle()
    {
      if (_theState != State.Idle)
      {
        progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, "TimerBarIdleColor-" + _overlayId);
        _theState = State.Idle;
      }
    }

    private void ProgressSizeChanged(object sender, SizeChangedEventArgs e) => SetIconHeight();

    private void SetIconHeight()
    {
      if (theIcon.Source == null)
      {
        if (theIcon.ActualHeight != 0 && theIcon.ActualWidth != 0)
        {
          theIcon.Height = 0;
          theIcon.Width = 0;
        }
      }
      else
      {
        var newHeight = (progress.ActualHeight > 0) ? progress.ActualHeight - 1 : 0;
        if (!newHeight.Equals(theIcon.ActualHeight))
        {
          theIcon.Height = newHeight;
          theIcon.Width = double.NaN;
        }
      }
    }

    private void UnloadWindow(object sender, RoutedEventArgs e)
    {
      _lastTimerData = null;
      progress.SizeChanged -= ProgressSizeChanged;
    }
  }
}
