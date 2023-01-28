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
    private double StandardTime = double.NaN;

    public TimerBar()
    {
      InitializeComponent();
    }

    internal string GetBarName() => BarName;
    internal double GetRemainingTime(double currentTime) => EndTime - currentTime;
    internal double SetStandardTime(double standardTime) => StandardTime = standardTime;

    internal void Init(string overlayId, string barName, double endTime, bool preview = false)
    {
      BarName = title.Text = barName;
      progress.SetResourceReference(SfLinearProgressBar.ProgressColorProperty, "TimerBarProgressColor-" + overlayId);
      progress.SetResourceReference(SfLinearProgressBar.TrackColorProperty, "TimerBarTrackColor-" + overlayId);
      progress.SetResourceReference(SfLinearProgressBar.HeightProperty, "TimerBarHeight-" + overlayId);
      timeText.SetResourceReference(TextBlock.FontSizeProperty, "TimerBarFontSize-" + overlayId);
      title.SetResourceReference(TextBlock.FontSizeProperty, "TimerBarFontSize-" + overlayId);
      timeText.SetResourceReference(TextBlock.ForegroundProperty, "TimerBarFontColor-" + overlayId);
      title.SetResourceReference(TextBlock.ForegroundProperty, "TimerBarFontColor-" + overlayId);
      StartTime = DateUtil.ToDouble(DateTime.Now);
      EndTime = endTime;
      Duration = EndTime - StartTime;
      Tick(preview);
    }

    internal void Update(double endTime)
    {
      StartTime = DateUtil.ToDouble(DateTime.Now);
      EndTime = endTime;
      Duration = EndTime - StartTime;
      Tick();
    }

    internal bool Tick(bool preview = false)
    {
      var updateTime = preview ? DateUtil.ToDouble(DateTime.Now) + 30 : DateUtil.ToDouble(DateTime.Now);
      var secondsLeft = EndTime - updateTime;
      timeText.Text = DateUtil.FormatSimpleMS(secondsLeft < 0 ? 0 : secondsLeft);

      bool done;
      if (Duration > 0)
      {
        var mod = double.IsNaN(StandardTime) ? Duration : StandardTime;
        var remaining = (secondsLeft / mod) * 100;
        progress.Progress = remaining < 0 ? 0 : remaining;
        done = secondsLeft < 0;
      }
      else
      {
        done = true;
      }

      return done;
    }
  }
}
