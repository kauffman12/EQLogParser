using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EQLogParser;

namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class HitFreqChartSelectionTest
{
  private double _savedFontSize;

  [TestInitialize]
  public void Setup()
  {
    _savedFontSize = ThemeConfig.CurrentFontSize;
    ThemeConfig.CurrentFontSize = 12;
  }

  [TestCleanup]
  public void Cleanup() => ThemeConfig.CurrentFontSize = _savedFontSize;

  [TestMethod]
  public void DuplicateNamesSelectTheirOwnDistributionAndKeepTheirKeyAcrossCritChanges()
  {
    Sta.Run(() =>
    {
      using var chart = CreateChart();
      var player = new PlayerStats { Name = "Caster" };
      var direct = player.SubStatOf("Shared Spell", Labels.Dd);
      var dot = player.SubStatOf("Shared Spell", Labels.Dot);
      direct.RecordHitTotal(100, false);
      dot.RecordHitTotal(900, false);
      direct.RecordHitTotal(200, true);
      dot.RecordHitTotal(1800, true);
      chart.Update(player, null);

      Assert.AreEqual("Label", chart.hitTypeList.DisplayMemberPath);
      var choice = chart.hitTypeList.Items.Cast<HitFrequencyChoice>().Single(item => item.Key == dot.Key);
      chart.hitTypeList.SelectedItem = choice;
      Assert.AreEqual(new HitFrequencyBar(900, 1, 0), Columns(chart).Single());

      chart.critTypeList.SelectedItem = "Critical";
      Assert.AreSame(choice, chart.hitTypeList.SelectedItem);
      Assert.AreEqual(new HitFrequencyBar(1800, 1, 0), Columns(chart).Single());
      chart.critTypeList.SelectedItem = "Non-Critical";
      Assert.AreSame(choice, chart.hitTypeList.SelectedItem);
      Assert.AreEqual(900L, Columns(chart).Single().Amount);
    });
  }

  [TestMethod]
  public void FilteringPreservesDifferencesAndTheCompleteClipboardSource()
  {
    Sta.Run(() =>
    {
      using var chart = CreateChart();
      var player = new PlayerStats { Name = "Caster" };
      var row = player.SubStatOf("Spell", Labels.Dd);
      foreach (var amount in new long[] { 100, 100, 100, 100, 100, 120, 150, 150, 150 })
        row.RecordHitTotal(amount, false);
      chart.Update(player, null);
      chart.minFreqList.SelectedIndex = 1;

      CollectionAssert.AreEqual(new[] { new HitFrequencyBar(100, 5, 0), new HitFrequencyBar(150, 3, 30) },
        Columns(chart));
      Assert.AreEqual(30L, Columns(chart)[1].Difference);
      Assert.AreEqual(2, chart.chartView.Data.Count);
      Assert.AreEqual(2, Columns(chart).Length, "the clipboard source is all filtered rows, not the viewport");
    });
  }

  [TestMethod]
  public void AllFilteredOutAndEmptyPlayerUpdatesClearThePreviousSeriesAndExtent()
  {
    Sta.Run(() =>
    {
      using var chart = CreateChart();
      var player = new PlayerStats { Name = "Caster" };
      var row = player.SubStatOf("Spell", Labels.Dd);
      row.RecordHitTotal(100, false);
      row.RecordHitTotal(150, false);
      chart.Update(player, null);
      Assert.AreEqual(2, chart.chartView.Data.Count);

      chart.minFreqList.SelectedIndex = 1;
      Assert.AreEqual(0, Columns(chart).Length);
      Assert.AreEqual(0, chart.chartView.Data.Count);
      Assert.AreEqual(0d, chart.chartView.strip.Width);
      Assert.AreEqual(0, chart.chartView.RealizedColumnCount);

      chart.minFreqList.SelectedIndex = 0;
      Assert.AreEqual(2, chart.chartView.Data.Count);
      chart.Update(new PlayerStats { Name = "Empty" }, null);
      Assert.AreEqual(0, chart.hitTypeList.Items.Count);
      Assert.AreEqual(0, chart.critTypeList.Items.Count);
      Assert.AreEqual(0, Columns(chart).Length);
      Assert.AreEqual(0, chart.chartView.Data.Count);
    });
  }

  [TestMethod]
  public void PlayerAndPetSwitchesCannotKeepThePreviousPlayersChoice()
  {
    Sta.Run(() =>
    {
      using var chart = CreateChart();
      var selected = new PlayerStats { Name = "Owner +Pets" };
      var owner = new PlayerStats { Name = "Owner" };
      owner.SubStatOf("Spell", Labels.Dd).RecordHitTotal(100, false);
      var pet = new PlayerStats { Name = "Pet" };
      pet.SubStatOf("Bite", "Melee").RecordHitTotal(200, true);
      var combined = new CombinedStats();
      combined.Children[selected.Name] = [owner, pet];
      chart.Update(selected, combined);
      Assert.AreEqual(100L, Columns(chart).Single().Amount);

      chart.playerList.SelectedItem = "Pet";
      Assert.AreEqual("Critical", chart.critTypeList.SelectedItem);
      Assert.AreEqual("Bite", ((HitFrequencyChoice)chart.hitTypeList.SelectedItem).Label);
      Assert.AreEqual(200L, Columns(chart).Single().Amount);
      chart.playerList.SelectedItem = "Owner";
      Assert.AreEqual("Non-Critical", chart.critTypeList.SelectedItem);
      Assert.AreEqual(100L, Columns(chart).Single().Amount);
    });
  }

  [TestMethod]
  public void MeleeDiLabelsSurviveFrequencyFilteringButNotCritSpellOrAmbiguousSelections()
  {
    var correct = false;
    Sta.Run(() =>
    {
      using var chart = CreateChart();
      var player = new PlayerStats { Name = "Attacker" };
      var melee = player.SubStatOf("Hits", Labels.Melee);
      var spell = player.SubStatOf("Spell Pattern", Labels.Dd);
      for (var level = 1; level <= 20; level++)
      {
        for (var hit = 0; hit < (level == 15 ? 1 : 3); hit++) melee.RecordHitTotal(7050 + (450 * level), false);
        for (var hit = 0; hit < 3; hit++)
        {
          // Identical numeric ladders in each lane must not bypass the critical/spell eligibility gates.
          melee.RecordHitTotal(7050 + (450 * level), true);
          spell.RecordHitTotal(7050 + (450 * level), false);
        }
      }
      chart.Update(player, null);
      chart.hitTypeList.SelectedItem = chart.hitTypeList.Items.Cast<HitFrequencyChoice>()
        .Single(choice => choice.Key == melee.Key);
      var regular = chart.chartView.Data.HasDiLabels && chart.chartView.Data.Column(0).DiLabel == "DI 1";
      chart.minFreqList.SelectedIndex = 1;
      var filtered = chart.chartView.Data.Count == 19 && chart.chartView.Data.Column(14).DiLabel == "DI 16"
        && Columns(chart)[14] == new HitFrequencyBar(14250, 3, 450);
      chart.critTypeList.SelectedItem = "Critical";
      var noCrit = !chart.chartView.Data.HasDiLabels && chart.chartView.Data.Column(0).DiLabel.Length == 0;
      chart.critTypeList.SelectedItem = "Non-Critical";
      var restored = chart.chartView.Data.HasDiLabels;
      chart.hitTypeList.SelectedItem = chart.hitTypeList.Items.Cast<HitFrequencyChoice>()
        .Single(choice => choice.Key == spell.Key);
      var noSpell = !chart.chartView.Data.HasDiLabels;
      var ambiguous = new PlayerStats { Name = "Another" };
      var sparse = ambiguous.SubStatOf("Hits", Labels.Melee);
      for (var hit = 0; hit < 3; hit++) sparse.RecordHitTotal(7500, false);
      chart.Update(ambiguous, null);
      correct = regular && filtered && noCrit && restored && noSpell && !chart.chartView.Data.HasDiLabels
        && chart.chartView.Data.Count == 1 && chart.chartView.Data.Column(0).DiLabel.Length == 0;
    });
    Assert.IsTrue(correct);
  }

  [TestMethod]
  public void TopSliderAndNativeScrollStayInSyncThroughDraggingWheelResizeAndEmptyResults()
  {
    var synchronized = false;
    Sta.Run(() =>
    {
      using var chart = CreateChart();
      var window = new Window { Content = chart, Width = 1400, Height = 650 };
      try
      {
        var player = new PlayerStats { Name = "Caster" };
        var row = player.SubStatOf("Spell", Labels.Dd);
        for (var amount = 1; amount <= 200; amount++) row.RecordHitTotal(amount, false);
        chart.Update(player, null);
        window.Show();
        BarChartViewTest.Layout(chart);
        var enabled = chart.pageSlider.IsEnabled && chart.pageSlider.Maximum > 0;
        var maximum = chart.pageSlider.Maximum;
        chart.pageSlider.Value = maximum;
        BarChartViewTest.Layout(chart);
        var lastReached = Math.Abs(chart.chartView.scroll.HorizontalOffset + chart.chartView.scroll.ViewportWidth
          - chart.chartView.scroll.ExtentWidth) < 1;

        chart.chartView.scroll.ScrollToHorizontalOffset(4.5 * chart.chartView.ColumnStride);
        BarChartViewTest.Layout(chart);
        var bottomUpdatesTop = Math.Abs(chart.pageSlider.Value - 4.5) < 0.01;
        chart.chartView.scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        { RoutedEvent = Mouse.MouseWheelEvent });
        BarChartViewTest.Layout(chart);
        var wheelUpdatesTop = Math.Abs(chart.pageSlider.Value - chart.chartView.Navigation.Position) < 0.01;

        window.Width = 1000;
        BarChartViewTest.Layout(chart);
        var resizeUpdatesRange = chart.pageSlider.Maximum > maximum
          && Math.Abs(chart.pageSlider.Maximum - chart.chartView.Navigation.Maximum) < 0.01;
        var fullClipboardSource = Columns(chart).Length == 200;
        chart.minFreqList.SelectedIndex = 1;
        BarChartViewTest.Layout(chart);
        synchronized = enabled && lastReached && bottomUpdatesTop && wheelUpdatesTop && resizeUpdatesRange
          && fullClipboardSource && !chart.pageSlider.IsEnabled && chart.pageSlider.Value == 0
          && chart.pageSlider.Maximum == 0;
      }
      finally
      {
        window.Close();
      }
    });
    Assert.IsTrue(synchronized);
  }

  private static HitFreqChart CreateChart()
  {
    var app = Application.Current ?? new Application();
    app.Resources["EQIconStyle"] ??= new Style(typeof(Image));
    app.Resources["ContentForeground"] ??= Brushes.White;
    app.Resources["EQMenuIconBrush"] ??= Brushes.CornflowerBlue;
    app.Resources["EQGridTitleHeight"] ??= 36d;
    return new HitFreqChart();
  }

  // Inspect the host's complete clipboard source, not just the realized viewport.
  private static HitFrequencyBar[] Columns(HitFreqChart chart)
  {
    var field = typeof(HitFreqChart).GetField("_columns", BindingFlags.Instance | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException("the chart's clipboard source is missing");
    if (field.GetValue(chart) is not IEnumerable columns)
      throw new InvalidOperationException("the chart's clipboard source must be enumerable");
    return columns.Cast<HitFrequencyBar>().ToArray();
  }
}
