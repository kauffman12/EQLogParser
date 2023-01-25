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

    public TimerBar(string name, double endTime)
    {
      InitializeComponent();

      StartTime = DateUtil.ToDouble(DateTime.Now);
      EndTime = endTime;
      Duration = EndTime - StartTime;
      BarName = title.Text = name;
      Tick();
    }

    public string GetBarName() => BarName;

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
