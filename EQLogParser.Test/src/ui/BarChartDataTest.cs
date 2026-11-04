using System.Globalization;

namespace EQLogParser;

[TestClass]
public sealed class BarChartDataTest
{
  [TestMethod]
  public void FrequencyScaleUsesTheEntireQuestionAndWideLabelsAndTooltipsRemainExact()
  {
    var bars = new[] { new HitFrequencyBar(100, 37, 0), new HitFrequencyBar(TextUtils.MaxAmount, 1, TextUtils.MaxAmount - 100) };
    var data = BarChartData.Frequency(bars);
    Assert.AreEqual(new BarChartAxis(40, 10), data.Axis);
    Assert.AreEqual(37d, data.Column(0).Value);
    Assert.AreEqual(1d, data.Column(1).Value);
    Assert.AreEqual(TextUtils.MaxAmount.ToString("N0", CultureInfo.CurrentCulture), data.Column(1).Primary);
    Assert.AreEqual("+" + (TextUtils.MaxAmount - 100).ToString("N0", CultureInfo.CurrentCulture), data.Column(1).Secondary);
    StringAssert.Contains(data.Column(1).Tooltip, "before filtering");
    Assert.AreEqual(new BarChartAxis(40, 10), data.Axis, "asking for another column cannot rescale the axis");
    bars[0] = new HitFrequencyBar(100, 99, 0);
    Assert.AreEqual(37d, data.Column(0).Value, "the renderer owns a snapshot, not the host's mutable list");
    Assert.IsTrue(data.WidthSamples().Count() <= 2, "large distributions use a stable numeric label budget");
  }

  [TestMethod]
  public void BenchmarksKeepTheFixedPercentScaleClassAnnotationAndPetLabel()
  {
    var data = BarChartData.Benchmark([new BenchmarkBar(67, "Owner", "10.2M", "Wizard", true, true)]);
    var column = data.Column(0);
    Assert.AreEqual(new BarChartAxis(100, 10), data.Axis);
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
