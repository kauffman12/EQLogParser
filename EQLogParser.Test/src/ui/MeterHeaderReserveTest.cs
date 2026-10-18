namespace EQLogParser;

[TestClass]
public sealed class MeterHeaderReserveTest
{
  [TestMethod]
  [DataRow(200.0, 27.0, 39.0, 212.0)]
  [DataRow(220.0, 29.0, 41.0, 232.0)]
  [DataRow(250.0, 31.0, 46.0, 265.0)]
  [DataRow(200.0, 27.0, 27.0, 200.0)]
  [DataRow(200.0, 31.0, 27.0, 200.0)]
  [DataRow(200.0, 27.0, 37.333333, 210.333333)]
  public void TallerToolbarPreservesTheSavedRowSpace(double height, double header, double toolbar, double expected)
  {
    var reserve = new MeterHeaderReserve();
    var adjusted = reserve.Update(height, header, toolbar);
    Assert.AreEqual(expected, adjusted, 0.000001);
    Assert.AreEqual(height - header, adjusted - Math.Max(header, toolbar), 0.000001,
      "Extra header height may not be taken from the last row's viewport.");
  }

  [TestMethod]
  public void RepeatedMeasurementsDoNotGrowTheWindowAgain()
  {
    var reserve = new MeterHeaderReserve();
    var height = 200.0;
    for (var i = 0; i < 20; i++) height = reserve.Update(height, 27, 39);
    Assert.AreEqual(212.0, height);
  }

  [TestMethod]
  public void GrowingAndShrinkingToolbarPaysOnlyTheDifference()
  {
    var reserve = new MeterHeaderReserve();
    var height = reserve.Update(200, 27, 39);
    Assert.AreEqual(212.0, height);
    height = reserve.Update(height, 27, 45);
    Assert.AreEqual(218.0, height);
    height = reserve.Update(height, 27, 33);
    Assert.AreEqual(206.0, height);
    height = reserve.Update(height, 27, 20);
    Assert.AreEqual(200.0, height);
  }

  [TestMethod]
  public void ChangedBaselineUsesThePreviouslyAppliedReserveNotTheNewBaselineToUndoIt()
  {
    var reserve = new MeterHeaderReserve();
    var height = reserve.Update(200, 27, 39);
    height = reserve.Update(height, 31, 39);
    Assert.AreEqual(208.0, height);
    height = reserve.Update(height, 27, 39);
    Assert.AreEqual(212.0, height);
  }

  [TestMethod]
  public void ReopeningFromTheSameSavedHeightDoesNotCompoundTheReserve()
  {
    const double savedHeight = 200;
    for (var i = 0; i < 20; i++)
    {
      var window = new MeterHeaderReserve();
      Assert.AreEqual(212.0, window.Update(savedHeight, 27, 39));
    }
  }

  [TestMethod]
  public void AnExternalHeightChangeIsNotOverwrittenByAnOldAbsoluteHeight()
  {
    var reserve = new MeterHeaderReserve();
    _ = reserve.Update(200, 27, 39);
    var adjusted = reserve.Update(250, 27, 45);
    Assert.AreEqual(256.0, adjusted);
    Assert.AreEqual(250.0 - 39, adjusted - 45);
  }

  [TestMethod]
  [DataRow(double.NaN)]
  [DataRow(double.PositiveInfinity)]
  [DataRow(0.0)]
  [DataRow(-1.0)]
  public void InvalidToolbarMeasurementsDoNotSpendOrForgetTheReserve(double invalid)
  {
    var reserve = new MeterHeaderReserve();
    var height = reserve.Update(200, 27, 39);
    Assert.AreEqual(212.0, reserve.Update(height, 27, invalid));
    Assert.AreEqual(200.0, reserve.Update(height, 27, 27));
  }

  [TestMethod]
  public void AnUnmeasuredWindowDoesNotConsumeTheFirstValidAdjustment()
  {
    var reserve = new MeterHeaderReserve();
    Assert.IsTrue(double.IsNaN(reserve.Update(double.NaN, 27, 39)));
    Assert.AreEqual(0.0, reserve.Update(0, 27, 39));
    Assert.AreEqual(212.0, reserve.Update(200, 27, 39));
  }
}
