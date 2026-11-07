using System.Globalization;

namespace EQLogParser;

[TestClass]
public sealed class BarChartDataTest
{
  [TestMethod]
  public void FrequencyLabelBudgetUsesTheWholeQuestionAndWideValuesRemainExact()
  {
    var bars = new[] { new HitFrequencyBar(100, 37, 0), new HitFrequencyBar(TextUtils.MaxAmount, 1, TextUtils.MaxAmount - 100) };
    var data = BarChartData.Frequency(bars);
    Assert.AreEqual(new BarChartAxis(40, 10), data.Axis);
    Assert.AreEqual(37d, data.Column(0).Value);
    Assert.AreEqual(1d, data.Column(1).Value);
    Assert.AreEqual(TextUtils.MaxAmount.ToString("N0", CultureInfo.CurrentCulture), data.Column(1).Primary);
    Assert.AreEqual("+" + (TextUtils.MaxAmount - 100).ToString("N0", CultureInfo.CurrentCulture), data.Column(1).Secondary);
    StringAssert.Contains(data.Column(1).Tooltip,
      "Diff from preceding value: " + (TextUtils.MaxAmount - 100).ToString("N0", CultureInfo.CurrentCulture));
    Assert.AreEqual(new BarChartAxis(40, 10), data.Axis, "the whole-question label budget does not change while scrolling");
    Assert.AreEqual("EQMenuIconBrush", data.Column(0).ColorKey, "use the brush ThemeConfig actually publishes");
    bars[0] = new HitFrequencyBar(100, 99, 0);
    Assert.AreEqual(37d, data.Column(0).Value, "the renderer owns a snapshot, not the host's mutable list");
    Assert.IsTrue(data.WidthSamples().Count() <= 2, "large distributions use a stable numeric label budget");
  }

  [TestMethod]
  public void FrequencyScaleFollowsVisibleBarsNotTheWholeQuestionOrOverscan()
  {
    var data = BarChartData.Frequency([
      new HitFrequencyBar(100, 11, 0), new HitFrequencyBar(120, 3, 20),
      new HitFrequencyBar(150, 80, 30), new HitFrequencyBar(170, 5, 20)]);
    var visible = BarChartLayout.Visible(data.Count, 100, 0, 200, 0);
    Assert.AreEqual(new BarChartAxis(15, 5), data.AxisFor(visible));
    Assert.AreEqual(new BarChartAxis(100, 20), data.Axis, "the fixed label budget includes peak headroom");
    Assert.AreEqual(new BarChartAxis(100, 20), data.AxisFor(BarChartLayout.Visible(data.Count, 100, 100, 200, 0)));
    Assert.AreEqual(new BarChartAxis(6, 1), data.AxisFor(new BarChartRange(3, 4)));
    Assert.AreEqual(new BarChartAxis(1, 1), data.AxisFor(new BarChartRange(0, 0)));
    Assert.AreEqual(80d, data.Column(2).Value, "viewport scaling must not alter the captured values");
  }

  [TestMethod]
  public void VisiblePeakAtATickCeilingGetsOneMoreTickWithoutChangingTheCount()
  {
    var data = BarChartData.Frequency([new HitFrequencyBar(100, 20, 0), new HitFrequencyBar(120, 46, 20)]);
    Assert.AreEqual(new BarChartAxis(25, 5), data.AxisFor(new BarChartRange(0, 1)));
    Assert.AreEqual(new BarChartAxis(50, 10), data.AxisFor(new BarChartRange(1, 2)));
    Assert.AreEqual(20d, data.Column(0).Value);
    Assert.AreEqual(46d, data.Column(1).Value);
  }

  [TestMethod]
  public void BenchmarksKeepTheFixedPercentScaleClassAnnotationAndPetLabel()
  {
    var data = BarChartData.Benchmark([new BenchmarkBar(67, "Owner", "10.2M", "Wizard", true, true)]);
    var column = data.Column(0);
    Assert.AreEqual(new BarChartAxis(100, 10), data.Axis);
    Assert.AreEqual(new BarChartAxis(100, 10), data.AxisFor(new BarChartRange(0, 1)));
    Assert.AreEqual(new BarChartAxis(100, 10), data.AxisFor(new BarChartRange(0, 0)));
    Assert.AreEqual(67d, column.Value);
    Assert.AreEqual("67% (10.2M)", column.Above);
    Assert.AreEqual("Owner", column.Primary);
    Assert.AreEqual("+Pets", column.Secondary);
    Assert.AreEqual("Wizard", column.Group);
    Assert.AreEqual(BarChartData.ClassColorKey("Wizard"), column.ColorKey);
    Assert.IsTrue(data.HasSecondary);
    var follower = BarChartData.Benchmark([new BenchmarkBar(50, "Another", "1M", "Wizard", false, false)]);
    Assert.AreEqual("", follower.Column(0).Group);
    Assert.AreEqual("", follower.Column(0).Secondary);
    Assert.IsFalse(follower.HasSecondary);
  }

  [TestMethod]
  public void OnlyFrequencyUsesTheCompactSlotPreset()
  {
    var frequency = BarChartData.Frequency([new HitFrequencyBar(100, 20, 0)]);
    var benchmark = BarChartData.Benchmark([new BenchmarkBar(100, "Caster", "1M", "Wizard", true, false)]);
    Assert.AreEqual(52d, frequency.ColumnStride(40));
    Assert.AreEqual(84d, benchmark.ColumnStride(40));
    Assert.AreEqual(212d, frequency.ColumnStride(200), "wide exact labels still expand the slot");
    Assert.AreEqual(224d, benchmark.ColumnStride(200));
  }

  [TestMethod]
  public void DiLabelsAreAnExtraEstimatedFooterAndDoNotReplaceExactValuesOrDifferences()
  {
    var bars = Enumerable.Range(1, 20).Select(level =>
      new HitFrequencyBar(7050 + (450 * level), level == 15 ? 1 : 3, level == 1 ? 0 : 450)).ToArray();
    var fit = MeleeDiEstimator.TryFit(bars);
    Assert.IsNotNull(fit);
    var filtered = HitFrequencySeries.Filter(bars, 1);
    var data = BarChartData.Frequency(filtered, fit);
    var column = data.Column(14);
    Assert.IsTrue(data.HasDiLabels);
    Assert.AreEqual("DI 16", column.DiLabel);
    Assert.AreEqual(14250L.ToString("N0", CultureInfo.CurrentCulture), column.Primary);
    Assert.AreEqual("+" + 450.ToString("N0", CultureInfo.CurrentCulture), column.Secondary);
    Assert.AreEqual(3d, column.Value);
    StringAssert.Contains(column.Tooltip, "Estimated DI 16");
    StringAssert.Contains(column.Tooltip, "20/20");
    Assert.IsTrue(data.WidthSamples().Contains("DI 20"));
    Assert.AreEqual(20, fit.ObservedLevels);
  }

  [TestMethod]
  public void NoFitUnknownAmountsAndEmptyDataCannotCarryStaleDiLabels()
  {
    var bars = Enumerable.Range(1, 20).Select(level => new HitFrequencyBar(7050 + (450 * level), 3, 450)).ToArray();
    var fit = MeleeDiEstimator.TryFit(bars);
    Assert.IsNotNull(fit);
    var noFit = BarChartData.Frequency(bars);
    Assert.IsFalse(noFit.HasDiLabels);
    Assert.AreEqual("", noFit.Column(0).DiLabel);
    var unknown = BarChartData.Frequency([new HitFrequencyBar(7499, 3, 0)], fit);
    Assert.IsFalse(unknown.HasDiLabels);
    Assert.AreEqual("", unknown.Column(0).DiLabel);
    Assert.IsFalse(BarChartData.Frequency([], fit).HasDiLabels);
    Assert.IsFalse(BarChartData.Benchmark([new BenchmarkBar(100, "Caster", "1M", "Wizard", true, false)]).HasDiLabels);
  }

  [TestMethod]
  public void CombinedCriticalExportKeepsEveryRowAndItsOriginalDifference()
  {
    var player = new PlayerStats { Name = "Keyce`s pet" };
    var row = player.SubStatOf("Slashes", Labels.Melee);
    foreach (var bar in HitFrequencyFixtures.CombinedKeyceCriticals())
      for (var hit = 0; hit < bar.Frequency; hit++) row.RecordHitTotal(bar.Amount, true);
    var choice = HitFrequencySeries.ForPlayers(player, null)[player.Name].Single();
    var fit = choice.DiFor(true);
    Assert.IsNotNull(fit);
    var data = BarChartData.Frequency(choice.Critical, fit);
    var rows = TextUtils.ReadTsv(data.FrequencyTsv());
    CollectionAssert.AreEqual(new[] { "Hit Value", "Frequency", "Difference" }, rows[0]);
    Assert.AreEqual(22, rows.Count, "all 21 amounts survive, including the outlier");
    CollectionAssert.AreEqual(new[] { "68377", "288", "3216" }, rows[^1]);
    CollectionAssert.AreEqual(new[] { "52674", "1", "364" }, rows[16]);
    CollectionAssert.AreEqual(new[] { "55520", "35", "2846" }, rows[17]);
    Assert.AreEqual(719, rows.Skip(1).Sum(fields => int.Parse(fields[1], CultureInfo.CurrentCulture)));
    Assert.AreEqual("DI 20", data.Column(20).DiLabel);
    Assert.AreEqual("", data.Column(15).DiLabel);
    StringAssert.Contains(data.Column(15).Tooltip, "Outside Estimated DI Ladder");
    StringAssert.Contains(data.Column(20).Tooltip, "718/719");
    // Asking for a narrow viewport cannot turn the export into a visible-page export.
    data.AxisFor(new BarChartRange(0, 2));
    Assert.AreEqual(22, TextUtils.ReadTsv(data.FrequencyTsv()).Count);
    var filtered = BarChartData.Frequency(HitFrequencySeries.Filter(choice.Critical, 1), fit);
    var filteredRows = TextUtils.ReadTsv(filtered.FrequencyTsv());
    Assert.AreEqual(21, filteredRows.Count);
    Assert.IsFalse(filteredRows.Skip(1).Any(fields => fields[0] == "52674"));
    CollectionAssert.AreEqual(new[] { "55520", "35", "2846" }, filteredRows[16]);
  }

  [TestMethod]
  public void FrequencyExportKeepsWideNumbersWithoutExtraColumns()
  {
    var bars = new[] { new HitFrequencyBar(TextUtils.MaxAmount, 3, TextUtils.MaxAmount - 1) };
    var rows = TextUtils.ReadTsv(BarChartData.Frequency(bars).FrequencyTsv());
    CollectionAssert.AreEqual(new[] { "Hit Value", "Frequency", "Difference" }, rows[0]);
    CollectionAssert.AreEqual(new[] { TextUtils.MaxAmount.ToString(CultureInfo.CurrentCulture), "3",
      (TextUtils.MaxAmount - 1).ToString(CultureInfo.CurrentCulture) }, rows[1]);
    Assert.AreEqual(1, TextUtils.ReadTsv(BarChartData.Frequency([]).FrequencyTsv()).Count);
    Assert.ThrowsExactly<InvalidOperationException>(() => BarChartData.Benchmark([]).FrequencyTsv());
  }

  [TestMethod]
  public void EmptyPresentationsHaveNoColumnsAndUsableAxes()
  {
    foreach (var empty in new[] { BarChartData.Frequency([]), BarChartData.Benchmark([]) })
    {
      Assert.AreEqual(0, empty.Count);
      Assert.IsTrue(empty.Axis.Maximum > 0 && empty.Axis.Step > 0);
      Assert.IsFalse(string.IsNullOrWhiteSpace(empty.EmptyText));
    }
  }
}
