using Syncfusion.UI.Xaml.ProgressBar;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace EQLogParser
{
    public partial class TimerBar : UserControl
    {
        private enum State
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

            double targetProgress = timerData?.TimerType == 3 ? 100 - remaining : remaining;

            progress.Progress = targetProgress;

            DoubleAnimation animation = new DoubleAnimation
            {
                From = progress.Progress,
                To = targetProgress,
                Duration = TimeSpan.FromSeconds(1.0), // Setting 1 gives us a nice smooth animation
                EasingFunction = null // I believe this defaults to a linear easing function
            };

            Storyboard storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            Storyboard.SetTarget(animation, progress);
            Storyboard.SetTargetProperty(animation, new PropertyPath(ProgressBarBase.ProgressProperty));

            storyboard.Begin();

            title.Text = displayName;
            time.Text = timeText;
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

        private void UnloadWindow(object sender, RoutedEventArgs e)
        {
            _lastTimerData = null;
        }
    }
}
