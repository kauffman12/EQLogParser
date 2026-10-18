using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class UiAnimationScanTest
  {
    [TestMethod]
    public void FindsAnAnimatedStyledPropertyEvenInCollapsedContentWithoutChangingIt()
    {
      var report = string.Empty;
      var stillAnimated = false;
      var stillCollapsed = false;
      Sta.Run(() =>
      {
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2)));
        var border = new Border { Name = "HiddenTarget", Visibility = Visibility.Collapsed, Style = style };
        var root = new Grid { Name = "TestPane" };
        root.Children.Add(border);
        border.BeginAnimation(FrameworkElement.MarginProperty,
          new ThicknessAnimation(new Thickness(2), new Thickness(20), TimeSpan.FromSeconds(10)));
        try
        {
          report = UiAnimationScan.Capture([root]);
          stillAnimated = border.HasAnimatedProperties;
          stillCollapsed = border.Visibility == Visibility.Collapsed;
        }
        finally
        {
          border.BeginAnimation(FrameworkElement.MarginProperty, null);
        }
      });

      StringAssert.Contains(report, "animated: Grid[TestPane]/Border[HiddenTarget]");
      StringAssert.Contains(report, "property=FrameworkElement.Margin");
      StringAssert.Contains(report, "source=Style");
      StringAssert.Contains(report, "visibility=Collapsed; isVisible=False");
      StringAssert.Contains(report, "animatedProperties=1; errors=0");
      StringAssert.Contains(report, "property-clock: layer=local");
      StringAssert.Contains(report, "clockInspectionErrors=0");
      Assert.IsTrue(stillAnimated, "A diagnostic must not remove the animation it is inspecting.");
      Assert.IsTrue(stillCollapsed);
    }

    [TestMethod]
    public void FindsAnAnimatedBrushReachedThroughAStyle()
    {
      var report = string.Empty;
      Sta.Run(() =>
      {
        var brush = new SolidColorBrush(Colors.Red);
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
          new ColorAnimation(Colors.Red, Colors.Blue, TimeSpan.FromSeconds(10)));
        try
        {
          var style = new Style(typeof(Border));
          style.Setters.Add(new Setter(Border.BackgroundProperty, brush));
          var border = new Border { Name = "BrushOwner", Style = style };
          report = UiAnimationScan.Capture([border]);
        }
        finally
        {
          brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        }
      });

      StringAssert.Contains(report, "Border[BrushOwner].Background/SolidColorBrush");
      StringAssert.Contains(report, "property=SolidColorBrush.Color");
      StringAssert.Contains(report, "animatedProperties=1; errors=0");
    }

    [TestMethod]
    public void DistinguishesActiveRepeatingAndFillingClocksWithoutControllingThem()
    {
      var report = string.Empty;
      var unchanged = false;
      Sta.Run(() =>
      {
        var border = new Border { Name = "ClockTarget" };
        var repeating = new ThicknessAnimation(new Thickness(0), new Thickness(10), TimeSpan.FromSeconds(1))
        {
          RepeatBehavior = RepeatBehavior.Forever,
          AutoReverse = true
        }.CreateClock();
        var filling = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1)).CreateClock();
        border.ApplyAnimationClock(FrameworkElement.MarginProperty, repeating);
        border.ApplyAnimationClock(UIElement.OpacityProperty, filling);
        try
        {
          var repeatController = repeating.Controller
            ?? throw new InvalidOperationException("Missing test clock controller");
          var fillController = filling.Controller
            ?? throw new InvalidOperationException("Missing test clock controller");
          repeatController.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(250), TimeSeekOrigin.BeginTime);
          fillController.SeekAlignedToLastTick(TimeSpan.FromSeconds(2), TimeSeekOrigin.BeginTime);
          var beforeTime = repeating.CurrentTime;
          report = UiAnimationScan.Capture([border]);
          unchanged = repeating.CurrentState == ClockState.Active && repeating.CurrentTime == beforeTime
            && filling.CurrentState == ClockState.Filling;
        }
        finally
        {
          border.BeginAnimation(FrameworkElement.MarginProperty, null);
          border.BeginAnimation(UIElement.OpacityProperty, null);
        }
      });

      StringAssert.Contains(report, "state=Active");
      StringAssert.Contains(report, "state=Filling");
      StringAssert.Contains(report, "repeat=Forever | autoReverse=True");
      StringAssert.Contains(report, "time=00:00:00.2500000");
      StringAssert.Contains(report, "duration=00:00:01");
      StringAssert.Contains(report, "reachableProperty=True");
      StringAssert.Contains(report, "clockInspectionErrors=0");
      Assert.IsTrue(unchanged, "Inspection may not tick, seek, pause or remove the clocks it reads.");
    }

    [TestMethod]
    public void TimingTreeIncludesAnActiveClockWithNoScannedTarget()
    {
      var report = string.Empty;
      var stillActive = false;
      Sta.Run(() =>
      {
        var clock = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1))
        {
          RepeatBehavior = RepeatBehavior.Forever
        }.CreateClock();
        var controller = clock.Controller ?? throw new InvalidOperationException("Missing test clock controller");
        try
        {
          controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(250), TimeSeekOrigin.BeginTime);
          report = UiAnimationScan.Capture([]);
          stillActive = clock.CurrentState == ClockState.Active;
        }
        finally
        {
          controller.Remove();
        }
      });

      StringAssert.Contains(report, "unmatchedActiveAnimations=1");
      StringAssert.Contains(report, "timing-clock: reachableProperty=False");
      StringAssert.Contains(report, "state=Active");
      StringAssert.Contains(report, "clockInspectionErrors=0");
      Assert.IsTrue(stillActive);
    }

    [TestMethod]
    public void FindsClocksInAStyleTriggerLayer()
    {
      var report = string.Empty;
      Sta.Run(() =>
      {
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(10));
        Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        var trigger = new System.Windows.Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        trigger.EnterActions.Add(new BeginStoryboard { Storyboard = storyboard });
        var style = new Style(typeof(Border));
        style.Triggers.Add(trigger);
        var border = new Border { Name = "StyledTarget", Style = style };
        border.BeginInit();
        border.EndInit();
        border.IsEnabled = false;
        try
        {
          report = UiAnimationScan.Capture([border]);
        }
        finally
        {
          border.Style = null;
        }
      });

      StringAssert.Contains(report, "property=UIElement.Opacity");
      StringAssert.Contains(report, "property-clock: layer=trigger:");
      StringAssert.Contains(report, "type=DoubleAnimation");
      StringAssert.Contains(report, "clockInspectionErrors=0");
    }

    [TestMethod]
    public void ReportsAnIndeterminateBarEvenWithoutARealizedTemplate()
    {
      var report = string.Empty;
      Sta.Run(() =>
      {
        var bar = new ProgressBar { Name = "loadBar", IsIndeterminate = true, Visibility = Visibility.Collapsed };
        report = UiAnimationScan.Capture([bar, bar]);
      });

      StringAssert.Contains(report, "indicator: ProgressBar[loadBar] | IsIndeterminate=True | visibility=Collapsed");
      Assert.AreEqual(1, report.Split("indicator:").Length - 1, "Overlapping roots must not duplicate a target.");
      StringAssert.Contains(report, "errors=0");
    }
  }
}
