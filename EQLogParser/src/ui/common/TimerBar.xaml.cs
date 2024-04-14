using Syncfusion.UI.Xaml.ProgressBar;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TimerBar.xaml
  /// </summary>
  public partial class TimerBar
  {
    public enum State
    {
      None,
      Active,
      Idle,
      Reset
    };

    private readonly DoubleAnimation _animation;
    private readonly Storyboard _storyboard;
    private string _overlayId;
    private State _theState = State.None;
    private TimerData _lastTimerData;
    private bool _isAnimationRunning;
    private string _lastDisplayName;

    public TimerBar()
    {
      InitializeComponent();

      // setup animation for smoother update
      _animation = new DoubleAnimation
      {
        // 500ms between ticks. see TriggerManager:WindowTick
        // use something slightly less to avoid pausing
        Duration = TimeSpan.FromMilliseconds(480),
        EasingFunction = null
      };

      _storyboard = new Storyboard();
      _storyboard.Children.Add(_animation);

      _storyboard.Completed += (_, _) =>
      {
        _isAnimationRunning = false;
      };

      Storyboard.SetTarget(_animation, progress);
      Storyboard.SetTargetProperty(_animation, new PropertyPath(ProgressBarBase.ProgressProperty));
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
    }

    internal State GetState() => _theState;

    internal void Update(string displayName, string timeText, double remaining, TimerData timerData)
    {
      // only reset colors if the timer has been assigned to something else
      if (_lastTimerData != timerData)
      {
        if (timerData?.FontColor != null)
        {
          var brush = TriggerUtil.GetBrush(timerData.FontColor);
          time.Foreground = brush;
          title.Foreground = brush;
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

        // only animate if it's the same timer bar as previous
        if ((_lastDisplayName == null || _lastDisplayName == displayName) && !_isAnimationRunning)
        {
          // animate during update cycle
          _animation.From = progress.Progress;
          _animation.To = targetProgress;

          // start
          _storyboard.Begin(this, true);
          _isAnimationRunning = true;
        }
        else
        {
          _storyboard.Stop(this);
        }
      }
      else
      {
        // 3 is an increasing timer.
        progress.Progress = timerData?.TimerType == 3 ? 100 - remaining : remaining;
      }

      title.Text = displayName;
      time.Text = timeText;
      _lastDisplayName = displayName;
    }

    internal void SetActive(TimerData timerData)
    {
      if (timerData?.ActiveColor != null)
      {
        if (TriggerUtil.GetBrush(timerData.ActiveColor) is var color && progress.ProgressColor != color)
        {
          progress.ProgressColor = color;
          _theState = State.None;
        }
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
