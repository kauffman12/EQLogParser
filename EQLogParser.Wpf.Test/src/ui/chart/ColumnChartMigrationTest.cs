using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQLogParser;

namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class ColumnChartMigrationTest
{
  [TestMethod]
  public void BothBenchmarksUseTheSharedPercentSurfaceWhileHiddenClassesStayInTheExportSource()
  {
    var dps = false;
    var healing = false;
    Sta.Run(() =>
    {
      Resources();
      dps = Check(new DamageColumnChart());
      healing = Check(new HealingColumnChart());
    });
    Assert.IsTrue(dps);
    Assert.IsTrue(healing);
  }

  [TestMethod]
  public void AQueuedStatsAnswerCannotRepopulateAHiddenPane()
  {
    var stayedEmpty = false;
    Sta.Run(() =>
    {
      Resources();
      var pane = new DamageColumnChart();
      var ready = typeof(ColumnChart).GetField("_ready", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("missing benchmark readiness guard");
      ready.SetValue(pane, true);
      Field<List<BenchmarkBar>>(pane, "_columns").Add(new BenchmarkBar(100, "Tank", "100", "Warrior", true, false));
      Invoke(pane, "EventsGenerationStatus", new StatsGenerationEvent
      { State = "COMPLETED", SessionId = 0, CombinedStats = new CombinedStats() });
      pane.HideContent();
      BarChartViewTest.Layout(pane);
      stayedEmpty = pane.chartView.Data.Count == 0 && Field<List<BenchmarkBar>>(pane, "_columns").Count == 0
        && Equals(pane.titleLabel.Content, Labels.NoData);
    });
    Assert.IsTrue(stayedEmpty);
  }

  private static bool Check(ColumnChart pane)
  {
    try
    {
      var all = Field<List<BenchmarkBar>>(pane, "_columns");
      all.Add(new BenchmarkBar(100, "Tank", "100", "Warrior", true, false));
      all.Add(new BenchmarkBar(80, "Owner", "80", "Wizard", true, true));
      var selected = Field<List<string>>(pane, "_selectedClasses");
      selected.Clear();
      selected.Add("Warrior");
      Invoke(pane, "ShowColumns");
      var warrior = pane.chartView.Data;
      selected.Clear();
      selected.Add("Wizard");
      Invoke(pane, "ShowColumns");
      var wizard = pane.chartView.Data;
      var color = Application.Current.Resources[BarChartData.ClassColorKey("Wizard")];
      return warrior.Count == 1 && warrior.Column(0).Primary == "Tank"
        && wizard.Axis == BarChartLayout.PercentAxis && wizard.Count == 1
        && wizard.Column(0).Secondary == "+Pets" && wizard.Column(0).Group == "Wizard"
        && color is SolidColorBrush && all.Count == 2;
    }
    finally
    {
      pane.HideContent();
    }
  }

  private static void Resources()
  {
    var app = Application.Current ?? new Application();
    app.Resources["EQTitleStyle"] ??= new Style(typeof(Label));
    app.Resources["EQIconStyle"] ??= new Style(typeof(Image));
    app.Resources["PrimaryColor"] ??= Brushes.CornflowerBlue;
  }

  private static T Field<T>(ColumnChart pane, string name)
    => typeof(ColumnChart).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(pane) is T value
      ? value : throw new InvalidOperationException($"missing benchmark field {name}");

  private static void Invoke(ColumnChart pane, string name, params object[] arguments)
  {
    var method = typeof(ColumnChart).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException($"missing benchmark method {name}");
    method.Invoke(pane, arguments);
  }
}
