namespace EQLogParser;

[TestClass]
public sealed class BarChartLayoutTest
{
  [TestMethod]
  [DataRow(0, 1L, 1L)]
  [DataRow(1, 1L, 1L)]
  [DataRow(2, 2L, 1L)]
  [DataRow(7, 8L, 2L)]
  [DataRow(37, 40L, 10L)]
  [DataRow(int.MaxValue, 2_500_000_000L, 500_000_000L)]
  public void FrequencyTicksAreIntegerNiceStepsWithWideRounding(int maximum, long upper, long step)
  {
    var axis = BarChartLayout.FrequencyAxis(maximum);
    Assert.AreEqual(upper, axis.Maximum);
    Assert.AreEqual(step, axis.Step);
    Assert.IsTrue(axis.Maximum >= maximum);
    Assert.IsTrue(axis.Maximum / axis.Step <= 5);
  }

  [TestMethod]
  public void PercentIsAlwaysZeroToOneHundred()
  {
    Assert.AreEqual(new BarChartAxis(100, 10), BarChartLayout.PercentAxis);
    Assert.AreEqual(120d, BarChartLayout.BarHeight(40, 100, 300));
    Assert.AreEqual(300d, BarChartLayout.BarHeight(100, 100, 300));
    Assert.AreEqual(0d, BarChartLayout.BarHeight(-1, 100, 300));
    Assert.AreEqual(300d, BarChartLayout.BarHeight(101, 100, 300));
  }

  [TestMethod]
  public void EmptyAndZeroViewportsRealizeNothing()
  {
    Assert.AreEqual(new BarChartRange(0, 0), BarChartLayout.Visible(0, 100, 0, 800));
    Assert.AreEqual(new BarChartRange(0, 0), BarChartLayout.Visible(50_000, 100, 0, 0));
    Assert.AreEqual(new BarChartRange(0, 0), BarChartLayout.Visible(50_000, 0, 0, 800));
    Assert.AreEqual(0d, BarChartLayout.Extent(0, 100));
  }

  [TestMethod]
  public void PartialColumnsAndOverscanAreIncludedButExactRightEdgesAreNotExtraColumns()
  {
    Assert.AreEqual(new BarChartRange(0, 8), BarChartLayout.Visible(50_000, 100, 0, 800, 0));
    Assert.AreEqual(new BarChartRange(0, 9), BarChartLayout.Visible(50_000, 100, 25, 800, 0));
    Assert.AreEqual(new BarChartRange(0, 11), BarChartLayout.Visible(50_000, 100, 25, 800));
    Assert.AreEqual(new BarChartRange(0, 1), BarChartLayout.Visible(1, 100, 999, 800));
    Assert.AreEqual(new BarChartRange(0, 3), BarChartLayout.Visible(3, 100, 0, 800));
  }

  [TestMethod]
  public void TheLastColumnIsReachableAndRealizationIsBoundedAcrossTheWholeExtent()
  {
    const int count = 50_000;
    const double stride = 120;
    const double viewport = 960;
    var extent = BarChartLayout.Extent(count, stride);
    Assert.AreEqual(6_000_000d, extent);
    Assert.AreEqual(count, BarChartLayout.Visible(count, stride, extent, viewport).End);
    for (var fraction = 0; fraction <= 100; fraction++)
    {
      var range = BarChartLayout.Visible(count, stride, extent * fraction / 100, viewport);
      Assert.IsTrue(range.First >= 0 && range.End <= count);
      Assert.IsTrue(range.Count <= Math.Ceiling(viewport / stride) + 1 + 4);
    }
  }

  [TestMethod]
  public void OffsetsClampAfterReplacementAndFontChangesPreserveLogicalPosition()
  {
    Assert.AreEqual(0d, BarChartLayout.ClampOffset(-100, 6000, 960));
    Assert.AreEqual(5040d, BarChartLayout.ClampOffset(9000, 6000, 960));
    Assert.AreEqual(0d, BarChartLayout.ClampOffset(9000, 100, 960));
    Assert.AreEqual(5040d, BarChartLayout.ClampOffset(double.PositiveInfinity, 6000, 960));
    Assert.AreEqual(0d, BarChartLayout.ClampOffset(double.NaN, 6000, 960));
    Assert.AreEqual(2500d, BarChartLayout.PreservePosition(1250, 100, 200, 10000, 960));
    Assert.AreEqual(40d, BarChartLayout.PreservePosition(1250, 100, 200, 1000, 960));
  }

  [TestMethod]
  [DataRow(0d, 12d)]
  [DataRow(1d, 12d)]
  [DataRow(600d, 12d)]
  [DataRow(600d, 30d)]
  [DataRow(double.NaN, double.NaN)]
  [DataRow(double.PositiveInfinity, double.PositiveInfinity)]
  public void SmallAndInvalidGeometryNeverProducesNegativeOrNonfiniteDimensions(double height, double font)
  {
    foreach (var second in new[] { false, true })
    {
      var bounds = BarChartLayout.Bounds(height, font, second);
      foreach (var value in new[] { bounds.Top, bounds.Height, bounds.PrimaryTop, bounds.SecondaryTop, bounds.LineHeight })
        Assert.IsTrue(double.IsFinite(value) && value >= 0);
      Assert.AreEqual(0d, BarChartLayout.BarHeight(double.NaN, 40, bounds.Height));
      Assert.AreEqual(0d, BarChartLayout.BarHeight(40, 0, bounds.Height));
    }
    var normal = BarChartLayout.Bounds(600, 12, true);
    Assert.IsTrue(normal.Top >= normal.LineHeight, "a maximum bar has space for its above-bar value");
    Assert.IsTrue(normal.SecondaryTop + normal.LineHeight <= 600, "the second footer fits above the scrollbar");
  }

  [TestMethod]
  public void WheelRespectsSystemLinesPageModeFractionalDeltasAndBoundaries()
  {
    Assert.AreEqual(425d, BarChartLayout.WheelOffset(500, 30, 3, 100, 400, 6000));
    Assert.AreEqual(500d, BarChartLayout.WheelOffset(500, 120, 0, 100, 400, 6000));
    Assert.AreEqual(100d, BarChartLayout.WheelOffset(500, 120, -1, 100, 400, 6000));
    Assert.AreEqual(0d, BarChartLayout.WheelOffset(0, 120, 3, 100, 400, 6000));
    Assert.AreEqual(5600d, BarChartLayout.WheelOffset(5600, -120, 3, 100, 400, 6000));
  }
}
