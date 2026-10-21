using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FontAwesome5;

namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class LineChartSelectionTest
{
  [TestMethod]
  public void GroupSelectionSurvivesBothTheInitialUpdateAndARebuild()
  {
    var counts = new List<int>();
    var labels = new List<string>();
    var totals = new List<long>();
    Sta.Run(() =>
    {
      var app = Application.Current ?? new Application();
      app.Resources["EQTitleStyle"] ??= new Style(typeof(Label));
      app.Resources["EQIconStyle"] ??= new Style(typeof(ImageAwesome));
      var previousFont = ThemeConfig.CurrentFontSize;
      ThemeConfig.CurrentFontSize = 12;
      var chart = new LineChart(["Aggregate DPS"], true);
      try
      {
        var player = new PlayerStats { Name = "Player", OrigName = "Player" };
        var group = new GroupEntry { Name = "Group 1", Members = [player] };
        for (var generation = 1; generation <= 2; generation++)
        {
          var block = new ActionGroup { BeginTime = 100 };
          block.Actions.Add(new HealRecord { Healer = "Player", Type = Labels.Heal, Total = generation * 600 });
          var update = new DataPointEvent
          {
            Action = "UPDATE", DataGeneration = generation, Iterator = new HealGroupCollection([[block]])
          };
          update.Selected.Add(player);
          update.SelectedGroups.Add(group);
          chart.HandleUpdateEvent(update);
          counts.Add(chart.sfLineChart.Series.Count);
          if (chart.sfLineChart.Series.Count > 0)
          {
            var series = chart.sfLineChart.Series[0];
            labels.Add(series.Label);
            if (series.ItemsSource is List<DataPoint> points) totals.Add(points[^1].Total);
          }
        }
      }
      finally
      {
        CloseChart(chart, previousFont);
      }
    });
    CollectionAssert.AreEqual(new[] { 1, 1 }, counts);
    CollectionAssert.AreEqual(new[] { "Group 1", "Group 1" }, labels);
    CollectionAssert.AreEqual(new long[] { 600, 1200 }, totals);
  }

  [TestMethod]
  public void RollingModesPlotQuietSecondsButAggregateModesKeepTheirOriginalBuckets()
  {
    double[] rollingTimes = [];
    long[] rates = [];
    long[] rollingAmounts = [];
    double[] aggregateTimes = [];
    long[] aggregateAmounts = [];
    Sta.Run(() =>
    {
      var app = Application.Current ?? new Application();
      app.Resources["EQTitleStyle"] ??= new Style(typeof(Label));
      app.Resources["EQIconStyle"] ??= new Style(typeof(ImageAwesome));
      var previousFont = ThemeConfig.CurrentFontSize;
      ThemeConfig.CurrentFontSize = 12;
      var chart = new LineChart(["Rolling HPS", "Rolling Healing", "Aggregate Healing"]);
      try
      {
        var blocks = new List<ActionGroup>();
        foreach (var (time, name) in new[] { (100, "Player"), (108, "Other"), (110, "Player"), (118, "Other") })
        {
          var block = new ActionGroup { BeginTime = time };
          block.Actions.Add(new HealRecord { Healer = name, Type = Labels.Heal, Total = 6_000_000 });
          blocks.Add(block);
        }
        chart.HandleUpdateEvent(new DataPointEvent
        {
          Action = "UPDATE", DataGeneration = 1, Iterator = new HealGroupCollection([blocks])
        });
        var points = PlayerPoints(chart);
        rollingTimes = points.Select(p => p.CurrentTime).ToArray();
        rates = points.Select(p => p.RollingDps).ToArray();
        chart.choicesList.SelectedIndex = 1;
        rollingAmounts = PlayerPoints(chart).Select(p => p.RollingTotal).ToArray();
        chart.choicesList.SelectedIndex = 2;
        points = PlayerPoints(chart);
        aggregateTimes = points.Select(p => p.CurrentTime).ToArray();
        aggregateAmounts = points.Select(p => p.Total).ToArray();
      }
      finally
      {
        CloseChart(chart, previousFont);
      }
    });
    CollectionAssert.AreEqual(new[] { 100d, 105d, 106d, 109d, 110d, 115d, 116d, 118d }, rollingTimes);
    CollectionAssert.AreEqual(new long[] { 1_000_000, 1_000_000, 0, 0, 1_000_000, 1_000_000, 0, 0 }, rates);
    CollectionAssert.AreEqual(rates.Select(rate => rate * 6).ToArray(), rollingAmounts);
    CollectionAssert.AreEqual(new[] { 100d, 110d }, aggregateTimes);
    CollectionAssert.AreEqual(new long[] { 6_000_000, 12_000_000 }, aggregateAmounts);
  }

  private static List<DataPoint> PlayerPoints(LineChart chart) =>
    chart.sfLineChart.Series.First(series => series.Label == "Player").ItemsSource is List<DataPoint> points
      ? points : throw new InvalidOperationException("Player series has no chart data");

  private static void CloseChart(LineChart chart, double previousFont)
  {
    chart.Clear();
    var method = typeof(LineChart).GetMethod("EventsThemeChanged", BindingFlags.Instance | BindingFlags.NonPublic)
      ?? throw new MissingMethodException("EventsThemeChanged");
    ThemeConfig.EventsThemeChanged -= method.CreateDelegate<Action<string>>(chart);
    ThemeConfig.CurrentFontSize = previousFont;
  }
}
