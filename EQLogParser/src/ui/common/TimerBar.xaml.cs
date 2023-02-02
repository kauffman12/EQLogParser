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
    private string BarName;
    private Trigger Trigger;
    private string OverlayId;
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
      EndTime = DateUtil.ToDouble(DateTime.Now) + Trigger.ResetDurationSeconds;
      progress.SetResourceReference(SfLinearProgressBar.ProgressColorProperty, "TimerBarResetColor-" + OverlayId);
    }

    internal bool IsCooldown() => CurrentIsCooldown;
    internal string GetBarName() => BarName;
    internal double GetRemainingTime(double currentTime) => EndTime - currentTime;
    internal double SetStandardTime(double standardTime) => StandardTime = standardTime;

    internal void Init(string overlayId, string barName, double endTime, Trigger trigger, bool preview = false)
    {
      BarName = title.Text = barName;
      Trigger = trigger;
      OverlayId = overlayId;
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
      Tick(preview);
    }

    internal void Update(double endTime)
    {
      if (CurrentIsCooldown)
      {
        CurrentIsCooldown = false;
        CurrentIsWaiting = false;
      }

      progress.SetResourceReference(SfLinearProgressBar.ProgressColorProperty, "TimerBarProgressColor-" + OverlayId);
      StartTime = DateUtil.ToDouble(DateTime.Now);
      EndTime = endTime;
      Duration = EndTime - StartTime;
      Tick();
    }

    internal bool Tick(bool preview = false)
    {
      bool done = true;

      if (!CurrentIsCooldown)
      {
        if (Duration > 0)
        {
          var updateTime = preview ? DateUtil.ToDouble(DateTime.Now) + 30 : DateUtil.ToDouble(DateTime.Now);
          var secondsLeft = EndTime - updateTime;
          timeText.Text = DateUtil.FormatSimpleMS(secondsLeft < 0 ? 0 : secondsLeft);
          var mod = double.IsNaN(StandardTime) ? Duration : StandardTime;
          var remaining = (secondsLeft / mod) * 100;
          progress.Progress = remaining < 0 ? 0 : remaining;
          done = secondsLeft < 0;
        }
        else
        {
          done = true;
        }
      }
      else if (!CurrentIsWaiting)
      {
        if (Trigger.ResetDurationSeconds > 0)
        {
          var updateTime = preview ? DateUtil.ToDouble(DateTime.Now) + 30 : DateUtil.ToDouble(DateTime.Now);
          var secondsLeft = EndTime - updateTime;
          timeText.Text = DateUtil.FormatSimpleMS(secondsLeft < 0 ? 0 : secondsLeft);
          var mod = Trigger.ResetDurationSeconds;
          var remaining = (secondsLeft / mod) * 100;
          progress.Progress = 100.0 - (remaining < 0 ? 0 : remaining);
          done = secondsLeft < 0;

          if (done)
          {
            Duration = Trigger.DurationSeconds;
            timeText.Text = DateUtil.FormatSimpleMS(Duration);
            progress.SetResourceReference(SfLinearProgressBar.ProgressColorProperty, "TimerBarProgressColor-" + OverlayId);
            CurrentIsWaiting = true;
          }
        }
        else
        {
          done = true;
        }
      }

      return done;
    }
  }
}
