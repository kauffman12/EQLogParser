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
        chart.Clear();
        var method = typeof(LineChart).GetMethod("EventsThemeChanged", BindingFlags.Instance | BindingFlags.NonPublic)
          ?? throw new MissingMethodException("EventsThemeChanged");
        ThemeConfig.EventsThemeChanged -= method.CreateDelegate<Action<string>>(chart);
        ThemeConfig.CurrentFontSize = previousFont;
      }
    });
    CollectionAssert.AreEqual(new[] { 1, 1 }, counts);
    CollectionAssert.AreEqual(new[] { "Group 1", "Group 1" }, labels);
    CollectionAssert.AreEqual(new long[] { 600, 1200 }, totals);
  }
}
