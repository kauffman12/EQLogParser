namespace EQLogParser;

[TestClass]
public sealed class ChartRollingSeriesTest
{
  [TestMethod]
  public void TwoBurstsTenSecondsApartPlotTheZeroBetweenThem()
  {
    var result = ChartRollingSeries.Build(Points((100, 6_000_000), (110, 6_000_000)), 110);
    CollectionAssert.AreEqual(new[] { 100d, 105d, 106d, 109d, 110d }, result.Select(p => p.CurrentTime).ToArray());
    CollectionAssert.AreEqual(new long[] { 1_000_000, 1_000_000, 0, 0, 1_000_000 },
      result.Select(p => p.RollingDps).ToArray());
  }

  [TestMethod]
  public void ARealNonzeroTroughIsNotForcedToZero()
  {
    var result = ChartRollingSeries.Build(Points((100, 6_000_000), (104, 3_000_000), (110, 6_000_000)), 110);
    Assert.AreEqual(500_000, result.Single(p => p.CurrentTime == 106).RollingDps);
    Assert.AreEqual(500_000, result.Single(p => p.CurrentTime == 109).RollingDps);
    Assert.IsFalse(result.Any(p => p.RollingDps == 0));
  }

  [TestMethod]
  public void AnotherPlayersDataEdgeExposesAQuietTail()
  {
    var source = Points((100, 600));
    var result = ChartRollingSeries.Build(source, 108);
    CollectionAssert.AreEqual(new[] { 100d, 105d, 106d, 108d }, result.Select(p => p.CurrentTime).ToArray());
    Assert.AreEqual(0, result[^1].RollingDps);
    Assert.AreEqual(108d, result[^1].CurrentTime);
  }

  [TestMethod]
  public void NoNewLogSecondMeansNoInventedDecayOrFutureZero()
  {
    var source = Points((100, 600));
    var result = ChartRollingSeries.Build(source, 100);
    Assert.HasCount(1, result);
    Assert.AreEqual(100, result[0].RollingDps);
    Assert.AreEqual(100d, result[0].CurrentTime);
    var repeated = ChartRollingSeries.Build(source, 100);
    Assert.AreEqual(result[0].RollingDps, repeated[0].RollingDps);
    Assert.HasCount(1, repeated);
  }

  [TestMethod]
  public void ArrivalAndExpiryAtTheSameSecondAreOneNetChange()
  {
    var result = ChartRollingSeries.Build(Points((100, 600), (106, 600)), 112);
    Assert.IsFalse(result.Any(p => p.CurrentTime == 106 && p.RollingTotal == 0));
    CollectionAssert.AreEqual(new[] { 100d, 111d, 112d }, result.Select(p => p.CurrentTime).ToArray());
    Assert.AreEqual(0, result[^1].RollingDps);
  }

  [TestMethod]
  public void FractionalTimesShareTheMetersWholeSecondLabels()
  {
    var source = Points((100.1, 600), (100.9, 600), (105.9, 600));
    var result = ChartRollingSeries.Build(source, 106.9);
    Assert.AreEqual(200, result[0].RollingDps);
    Assert.AreEqual(300, result.Single(p => p.CurrentTime == 105).RollingDps);
    Assert.AreEqual(100, result[^1].RollingDps);
    Assert.AreEqual(106d, result[^1].CurrentTime);
  }

  [TestMethod]
  public void QuietYearsCostOnlyTheTransitionAndEndpoint()
  {
    var result = ChartRollingSeries.Build(Points((100, 600)), 1_000_000_000);
    Assert.HasCount(4, result);
    Assert.AreEqual(0, result[^1].RollingTotal);
    Assert.AreEqual(1_000_000_000d, result[^1].CurrentTime);
  }

  [TestMethod]
  public void RollingGeometryDoesNotMutateOrBecomeAggregateData()
  {
    var source = Points((100, 600), (110, 600));
    source[0].Total = 600;
    source[1].Total = 1200;
    source[0].ValuePerSecond = 600;
    var result = ChartRollingSeries.Build(source, 116);
    Assert.HasCount(2, source);
    Assert.AreEqual(600, source[0].Total);
    Assert.AreEqual(1200, source[1].Total);
    Assert.AreEqual(600, source[0].ValuePerSecond);
    Assert.AreEqual(0, source[0].RollingTotal);
    Assert.AreNotSame(source[0], result[0]);
    Assert.AreEqual("Player +Pets", result[0].Name);
    Assert.AreEqual("Player", result[0].PlayerName);
    Assert.AreEqual(DateUtil.FromDotNetSeconds(100), result[0].DateTime);
  }

  [TestMethod]
  public void GroupsSumAmountsBeforeDividingAndShareTheQuietTail()
  {
    var merged = ChartAggregation.MergeSeries("Group", [Points((100, 5)), Points((100, 5))]);
    Assert.IsNotNull(merged);
    var result = ChartRollingSeries.Build(merged, 108);
    Assert.AreEqual(10, result[0].RollingTotal);
    Assert.AreEqual(1, result[0].RollingDps);
    Assert.AreEqual(0, result[^1].RollingDps);
  }

  [TestMethod]
  public void GroupRollingIncludesAMemberWithoutABucketAtTheOtherMembersArrival()
  {
    var merged = ChartAggregation.MergeSeries("Group", [Points((100, 600), (106, 0)), Points((105, 600))]);
    Assert.IsNotNull(merged);
    Assert.IsTrue(merged.All(p => p.RollingTotal == 0 && p.RollingDps == 0),
      "group aggregation keeps source buckets; only plotting computes rolling rates");
    var plotted = ChartRollingSeries.Build(merged, 111);
    Assert.AreEqual(1200, plotted.Single(p => p.CurrentTime == 105).RollingTotal);
    Assert.AreEqual(200, plotted.Single(p => p.CurrentTime == 105).RollingDps);
    Assert.AreEqual(600, plotted.Single(p => p.CurrentTime == 106).RollingTotal);
    Assert.AreEqual(0, plotted[^1].RollingTotal);
  }

  [TestMethod]
  public void FifthSecondIncludesTheFirstHitAndSixthSecondExpiresIt()
  {
    var plotted = ChartRollingSeries.Build(Points((100, 100), (105, 200), (106, 300)), 106);
    Assert.AreEqual(16, plotted[0].RollingDps, "even a single hit divides by the full six seconds");
    Assert.AreEqual(300, plotted.Single(p => p.CurrentTime == 105).RollingTotal);
    Assert.AreEqual(500, plotted.Single(p => p.CurrentTime == 106).RollingTotal);
    Assert.AreEqual(83, plotted[^1].RollingDps);
  }

  [TestMethod]
  public void WideAmountsAreNeverRoundedThroughFloatingPoint()
  {
    var amount = (1L << 53) + 1;
    var result = ChartRollingSeries.Build(Points((100, amount), (100, amount)), 106);
    Assert.AreEqual(amount * 2, result[0].RollingTotal);
    Assert.AreEqual(amount * 2 / 6, result[0].RollingDps);
    Assert.AreEqual(0, result[^1].RollingTotal);
  }

  [TestMethod]
  public void EmptyAndOutOfRangeInputsHaveNoInventedAnswer()
  {
    Assert.IsEmpty(ChartRollingSeries.Build([], 100));
    Assert.IsEmpty(ChartRollingSeries.Build(Points((100, 600)), 99));
    Assert.IsEmpty(ChartRollingSeries.Build(Points((100, 600)), double.NaN));
    var result = ChartRollingSeries.Build(Points((100, 600), (110, 1200)), 108);
    Assert.AreEqual(0, result[^1].RollingTotal);
    Assert.AreEqual(108d, result[^1].CurrentTime);
  }

  [TestMethod]
  public void ZeroOutcomesAndOldGapMarkersDoNotAddRollingTransitions()
  {
    var result = ChartRollingSeries.Build(Points((100, 600), (106, 0), (134, 0), (140, 600)), 140);
    CollectionAssert.AreEqual(new[] { 100d, 105d, 106d, 139d, 140d }, result.Select(p => p.CurrentTime).ToArray());
    var zero = ChartRollingSeries.Build(Points((100, 0), (110, 0)), 110);
    Assert.HasCount(2, zero);
    Assert.IsTrue(zero.All(p => p.RollingTotal == 0));
  }

  [TestMethod]
  public void EveryPlottedLogSecondAgreesWithTheMetersBuckets()
  {
    var random = new Random(7361);
    var source = new List<DataPoint>();
    var second = 100;
    for (var i = 0; i < 300; i++)
    {
      source.AddRange(Points((second, random.Next(0, 500_000))));
      second += random.Next(0, 12);
    }
    var through = second + 6;
    var plotted = ChartRollingSeries.Build(source, through);
    var window = new MeterRecentWindow();
    var cursor = 0;
    var point = 0;
    for (var time = 100; time <= through; time++)
    {
      window.Advance(time);
      while (cursor < source.Count && source[cursor].CurrentTime == time)
        window.Add("Player", "", time, source[cursor++].TotalPerSecond, time);
      var totals = new Dictionary<(string Name, string Owner), long>();
      window.SumInto(totals, time);
      var expected = totals.Values.Sum();
      while (point + 1 < plotted.Count && plotted[point + 1].CurrentTime <= time) point++;
      Assert.AreEqual(expected, plotted[point].RollingTotal, $"Log second {time}");
      Assert.AreEqual(expected / 6, plotted[point].RollingDps, $"Log second {time}");
      if (point + 1 < plotted.Count && plotted[point + 1].CurrentTime - plotted[point].CurrentTime > 1)
        Assert.AreEqual(plotted[point].RollingTotal, plotted[point + 1].RollingTotal,
          "The connecting line across missing seconds must be flat, not a fabricated slope");
    }
  }

  private static List<DataPoint> Points(params (double Time, long Amount)[] points) => points.Select(p => new DataPoint
  {
    Name = "Player +Pets", PlayerName = "Player", CurrentTime = p.Time, TotalPerSecond = p.Amount,
    FightTotal = p.Amount, AttemptsPerSecond = 1
  }).ToList();
}
