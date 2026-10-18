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
