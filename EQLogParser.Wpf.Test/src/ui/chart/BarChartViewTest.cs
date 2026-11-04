using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EQLogParser;

namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class BarChartViewTest
{
  [TestMethod]
  public void LargeExtentsStayBoundedAndTheFrequencyScaleFollowsTheVisibleColumns()
  {
    var firstCount = 0;
    var lastCount = 0;
    var limit = 0d;
    var atEnd = false;
    var lastRealized = false;
    var scaleFollowsViewport = false;
    var poolReused = false;
    WithView(view =>
    {
      var bars = Enumerable.Range(1, 50_000).Select(index => new HitFrequencyBar(index, index == 1 ? 37 : 1, 1)).ToArray();
      view.SetData(BarChartData.Frequency(bars));
      Layout(view);
      firstCount = view.RealizedColumnCount;
      var roots = view.strip.Children.OfType<Canvas>().ToArray();
      var maximum = view.DisplayedAxis.Maximum;
      var axisWidth = view.axisColumn.Width.Value;
      view.scroll.ScrollToHorizontalOffset(view.scroll.ExtentWidth);
      Layout(view);
      lastCount = view.RealizedColumnCount;
      limit = Math.Ceiling(view.scroll.ViewportWidth / view.ColumnStride) + 5;
      atEnd = Math.Abs(view.scroll.HorizontalOffset + view.scroll.ViewportWidth - view.scroll.ExtentWidth) < 1;
      lastRealized = view.strip.Children.OfType<Canvas>().Any(column =>
        Math.Abs(Canvas.GetLeft(column) - ((bars.Length - 1) * view.ColumnStride)) < 1);
      scaleFollowsViewport = maximum == 40 && view.DisplayedAxis.Maximum == 1
        && view.axisColumn.Width.Value == axisWidth
        && view.axis.Children.OfType<TextBlock>().Select(label => label.Text).SequenceEqual(new[] { "0", "1" });
      poolReused = roots.Take(Math.Min(firstCount, lastCount)).All(root => view.strip.Children.Contains(root));
    });
    Assert.IsTrue(firstCount > 0 && firstCount <= limit);
    Assert.IsTrue(lastCount > 0 && lastCount <= limit);
    Assert.IsTrue(atEnd);
    Assert.IsTrue(lastRealized);
    Assert.IsTrue(scaleFollowsViewport);
    Assert.IsTrue(poolReused);
  }

  [TestMethod]
  public void EmptyReplacementDropsColumnsHoverTargetsAxisAndScrollableExtent()
  {
    var empty = false;
    WithView(view =>
    {
      view.SetData(BarChartData.Frequency(Enumerable.Range(1, 500).Select(i => new HitFrequencyBar(i, 2, 1)).ToArray()));
      Layout(view);
      view.scroll.ScrollToHorizontalOffset(view.scroll.ExtentWidth);
      Layout(view);
      var oldTargets = view.strip.Children.OfType<Canvas>().ToArray();
      view.SetData(BarChartData.Frequency([]));
      Layout(view);
      empty = view.RealizedColumnCount == 0 && view.strip.Width == 0 && view.axis.Children.Count == 0
        && view.scroll.HorizontalOffset == 0 && view.emptyMessage.Visibility == Visibility.Visible
        && oldTargets.All(target => target.Parent == null && target.ToolTip == null);
    });
    Assert.IsTrue(empty);
  }

  [TestMethod]
  public void ResizingAndFontChangesKeepTheDataQuestionAndPreserveLogicalScrollPosition()
  {
    var sameData = false;
    var positionKept = false;
    var newGeometry = false;
    WithView(view =>
    {
      var data = BarChartData.Frequency(Enumerable.Range(1, 1000).Select(i => new HitFrequencyBar(i, 7, 1)).ToArray());
      view.SetData(data);
      Layout(view);
      view.scroll.ScrollToHorizontalOffset(100 * view.ColumnStride);
      Layout(view);
      var logical = view.scroll.HorizontalOffset / view.ColumnStride;
      var stride = view.ColumnStride;
      Window.GetWindow(view).Width = 700;
      Layout(view);
      ThemeConfig.CurrentFontSize = 22;
      view.RefreshPresentation();
      Layout(view);
      sameData = ReferenceEquals(data, view.Data);
      positionKept = Math.Abs(logical - (view.scroll.HorizontalOffset / view.ColumnStride)) < 0.05;
      newGeometry = view.ColumnStride > stride && view.RealizedColumnCount > 0;
    });
    Assert.IsTrue(sameData);
    Assert.IsTrue(positionKept);
    Assert.IsTrue(newGeometry);
  }

  [TestMethod]
  public void FrequencyBarsActuallyPaintWithThePublishedThemeBrush()
  {
    var painted = false;
    WithView(view =>
    {
      var data = BarChartData.Frequency([new HitFrequencyBar(100, 11, 0)]);
      view.SetData(data);
      Layout(view);
      var bar = view.strip.Children.OfType<Canvas>().Single().Children.OfType<System.Windows.Shapes.Rectangle>().Single();
      if (bar.ActualWidth <= 0 || bar.ActualHeight <= 0) return;
      var center = bar.TransformToAncestor(view).Transform(new Point(bar.ActualWidth / 2, bar.ActualHeight / 2));
      var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
        (int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
      bitmap.Render(view);
      var pixel = new byte[4];
      bitmap.CopyPixels(new Int32Rect((int)center.X, (int)center.Y, 1, 1), pixel, 4, 0);
      painted = Application.Current.Resources[data.Column(0).ColorKey] is SolidColorBrush brush
        && brush.Color.A == 255 && pixel[3] == 255
        && pixel[0] == brush.Color.B && pixel[1] == brush.Color.G && pixel[2] == brush.Color.R;
    });
    Assert.IsTrue(painted, "positive heights and realized controls are not proof that a bar painted");
  }

  [TestMethod]
  public void ApplicationColorResourcesRepaintExistingBarsWithoutReplacingTheData()
  {
    var repainted = false;
    WithView(view =>
    {
      var resources = Application.Current.Resources;
      var previous = resources["EQMenuIconBrush"];
      try
      {
        var data = BarChartData.Frequency([new HitFrequencyBar(100, 1, 0)]);
        view.SetData(data);
        Layout(view);
        var bar = view.strip.Children.OfType<Canvas>().Single().Children.OfType<System.Windows.Shapes.Rectangle>().Single();
        resources["EQMenuIconBrush"] = Brushes.OrangeRed;
        Layout(view);
        repainted = bar.Fill is SolidColorBrush brush && brush.Color == Colors.OrangeRed
          && ReferenceEquals(data, view.Data);
      }
      finally
      {
        resources["EQMenuIconBrush"] = previous;
      }
    });
    Assert.IsTrue(repainted);
  }

  [TestMethod]
  public void WheelBubblesAtTheBoundaryAndAccumulatesHighResolutionBursts()
  {
    var boundaryBubbled = false;
    var handledAsExpected = false;
    WithView(view =>
    {
      view.SetData(BarChartData.Frequency(Enumerable.Range(1, 500).Select(i => new HitFrequencyBar(i, 1, 1)).ToArray()));
      Layout(view);
      var atStart = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
      { RoutedEvent = Mouse.MouseWheelEvent };
      view.scroll.RaiseEvent(atStart);
      boundaryBubbled = !atStart.Handled;
      var expected = BarChartLayout.WheelOffset(0, -30, SystemParameters.WheelScrollLines,
        view.ColumnStride, view.scroll.ViewportWidth, view.scroll.ExtentWidth);
      var movement = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -30)
      { RoutedEvent = Mouse.MouseWheelEvent };
      view.scroll.RaiseEvent(movement);
      var second = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -30)
      { RoutedEvent = Mouse.MouseWheelEvent };
      view.scroll.RaiseEvent(second);
      var expectedBurst = BarChartLayout.WheelOffset(expected, -30, SystemParameters.WheelScrollLines,
        view.ColumnStride, view.scroll.ViewportWidth, view.scroll.ExtentWidth);
      Layout(view);
      handledAsExpected = movement.Handled == (expected > 0) && second.Handled == (expectedBurst > expected)
        && Math.Abs(view.scroll.HorizontalOffset - expectedBurst) < 0.1;
    });
    Assert.IsTrue(boundaryBubbled);
    Assert.IsTrue(handledAsExpected);
  }

  internal static void Layout(FrameworkElement view)
  {
    view.UpdateLayout();
    var frame = new DispatcherFrame();
    var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(20) };
    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
    timer.Start();
    Dispatcher.PushFrame(frame);
    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Normal);
    view.UpdateLayout();
  }

  private static void WithView(Action<BarChartView> body)
  {
    Sta.Run(() =>
    {
      var app = Application.Current ?? new Application();
      var window = new Window { Width = 1000, Height = 600 };
      var font = ThemeConfig.CurrentFontSize;
      try
      {
        ThemeConfig.CurrentFontSize = 12;
        app.Resources["ContentForeground"] ??= Brushes.White;
        app.Resources["ContentBackground"] ??= Brushes.Black;
        app.Resources["EQMenuIconBrush"] ??= Brushes.CornflowerBlue;
        var view = new BarChartView();
        window.Content = view;
        window.Show();
        Layout(view);
        body(view);
      }
      finally
      {
        window.Close();
        ThemeConfig.CurrentFontSize = font;
      }
    });
  }
}
