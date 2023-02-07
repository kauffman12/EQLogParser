using Syncfusion.UI.Xaml.ProgressBar;
using System;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TimerBar.xaml
  /// </summary>
  public partial class TimerBar : UserControl
  {
    private double EndTime;
    private double StartTime;
    private double Duration;
    private string Key;
    private Trigger Trigger;
    private string OverlayId;
    private bool EndEarly;
    private double TimeRemaining;
    private double StandardTime = double.NaN;
    private bool CurrentIsCooldown = false;
    private bool CurrentIsWaiting = false;

    public TimerBar()
    {
      InitializeComponent();
    }

    internal void SetCooldown(bool isCooldown)
    {
      CurrentIsCooldown = isCooldown;
      if (CurrentIsCooldown)
      {
        EndTime = StartTime + Trigger.ResetDurationSeconds;
        TimeRemaining = EndTime - DateUtil.ToDouble(DateTime.Now);
        progress.SetResourceReference(SfLinearProgressBar.ProgressColorProperty, "TimerBarResetColor-" + OverlayId);
        CurrentIsWaiting = false;
      }
      else
      {
        progress.SetResourceReference(SfLinearProgressBar.ProgressColorProperty, "TimerBarProgressColor-" + OverlayId);
      }
    }

    internal bool CanCooldown() => Trigger.ResetDurationSeconds > 0;
    internal bool IsCooldown() => CurrentIsCooldown;
    internal bool IsWaiting() => CurrentIsWaiting;
    internal string GetBarKey() => Key;
    internal double GetRemainingTime() => TimeRemaining;
    internal double SetStandardTime(double standardTime) => StandardTime = standardTime;

    internal void Init(string overlayId, string key, string displayName, double endTime, Trigger trigger, bool preview = false)
    {
      title.Text = displayName;
      Key = key;
      Trigger = trigger;
      OverlayId = overlayId;
      EndEarly = false;
      progress.SetResourceReference(SfLinearProgressBar.ProgressColorProperty, "TimerBarProgressColor-" + OverlayId);
      progress.SetResourceReference(SfLinearProgressBar.TrackColorProperty, "TimerBarTrackColor-" + OverlayId);
      progress.SetResourceReference(SfLinearProgressBar.HeightProperty, "TimerBarHeight-" + OverlayId);
      timeText.SetResourceReference(TextBlock.FontSizeProperty, "TimerBarFontSize-" + OverlayId);
      title.SetResourceReference(TextBlock.FontSizeProperty, "TimerBarFontSize-" + OverlayId);
      timeText.SetResourceReference(TextBlock.ForegroundProperty, "TimerBarFontColor-" + OverlayId);
      title.SetResourceReference(TextBlock.ForegroundProperty, "TimerBarFontColor-" + OverlayId);
      StartTime = DateUtil.ToDouble(DateTime.Now);
      EndTime = endTime;
      Duration = EndTime - StartTime;
      TimeRemaining = Duration;
      Tick(preview);
    }

    internal void EndTimer() => EndEarly = true;

    internal void Update(double endTime)
    {
      CurrentIsCooldown = false;
      CurrentIsWaiting = false;
      progress.SetResourceReference(SfLinearProgressBar.ProgressColorProperty, "TimerBarProgressColor-" + OverlayId);
      StartTime = DateUtil.ToDouble(DateTime.Now);
      EndTime = endTime;
      Duration = EndTime - StartTime;
      EndEarly = false;
      TimeRemaining = Duration;
      Tick();
    }

    internal bool Tick(bool preview = false)
    {
      bool done = true;

      if (!CurrentIsWaiting)
      {
        if (!CurrentIsCooldown)
        {
          if (Duration > 0 && !EndEarly)
          {
            var updateTime = preview ? DateUtil.ToDouble(DateTime.Now) + 30 : DateUtil.ToDouble(DateTime.Now);
            TimeRemaining = EndTime - updateTime;
            timeText.Text = DateUtil.FormatSimpleMS(TimeRemaining < 0 ? 0 : TimeRemaining);
            var mod = double.IsNaN(StandardTime) ? Duration : StandardTime;
            var remaining = (TimeRemaining / mod) * 100;
            progress.Progress = remaining < 0 ? 0 : remaining;
            done = TimeRemaining < 0;
          }
          else
          {
            done = true;
          }
        }
        else
        {
          if (Trigger.ResetDurationSeconds > 0)
          {
            var updateTime = preview ? DateUtil.ToDouble(DateTime.Now) + 30 : DateUtil.ToDouble(DateTime.Now);
            TimeRemaining = EndTime - updateTime;
            timeText.Text = DateUtil.FormatSimpleMS(TimeRemaining < 0 ? 0 : TimeRemaining);
            var mod = Trigger.ResetDurationSeconds;
            var remaining = (TimeRemaining / mod) * 100;
            progress.Progress = 100.0 - (remaining < 0 ? 0 : remaining);
            done = TimeRemaining < 0;

            if (done)
            {
              Duration = Trigger.DurationSeconds;
              timeText.Text = DateUtil.FormatSimpleMS(Duration);
              TimeRemaining = Duration;
              CurrentIsWaiting = true;
            }
          }
          else
          {
            done = true;
          }
        }
      }

      return done;
    }
  }
}
