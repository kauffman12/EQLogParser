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

    public TimerBar()
    {
      InitializeComponent();
    }

    public string GetBarName() => BarName;

    public void InitOverlay(string overlayId, string barName, double endTime)
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
      Tick();
    }

    public void Update(double endTime)
    {
      StartTime = DateUtil.ToDouble(DateTime.Now);
      EndTime = endTime;
      Duration = EndTime - StartTime;
      Tick();
    }

    public bool Tick()
    {
      var updateTime = DateUtil.ToDouble(DateTime.Now);
      var secondsLeft = EndTime - updateTime;
      timeText.Text = DateUtil.FormatSimpleMS(secondsLeft < 0 ? 0 : secondsLeft);

      bool done;
      if (Duration > 0)
      {
        var remaining = (secondsLeft / Duration) * 100;
        progress.Progress = remaining < 0 ? 0 : remaining;
        done = remaining < 0;
      }
      else
      {
        done = true;
      }

      return done;
    }
  }
}
