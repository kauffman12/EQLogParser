namespace EQLogParser;

[TestClass]
public sealed class ChartSeriesRecoveryTest
{
  [TestMethod]
  public void BackwardsBucketsAreSortedAndExpireAtTheirRealSeconds()
  {
    var builder = new ChartSeriesBuilder("Player");
    foreach (var time in new[] { 100d, 104, 102, 105 })
      builder.Add(new DataPoint { CurrentTime = time, Total = 600 }, false, false, true);
    builder.Finish();
    CollectionAssert.AreEqual(new[] { 100d, 102, 104, 105 }, builder.Points.Select(p => p.CurrentTime).ToArray());
    CollectionAssert.AreEqual(new long[] { 600, 1200, 1800, 2400 }, builder.Points.Select(p => p.Total).ToArray());
    var merged = ChartAggregation.MergeSeries("Group", [builder.Points]);
    Assert.IsNotNull(merged);
    var rolling = ChartRollingSeries.Build(merged, 111);
    Assert.AreEqual(1200L, rolling.Single(p => p.CurrentTime == 109).RollingTotal,
      "the second-102 hit expired at 108, not when a later source bucket happens to expire");
    Assert.AreEqual(0L, rolling[^1].RollingTotal);
  }

  [TestMethod]
  public void LateBucketsReplaceGapMarkersAndMergeExistingSecondsWithoutMutatingPublishedPoints()
  {
    var builder = new ChartSeriesBuilder("Player", "Owner");
    builder.Add(new DataPoint { CurrentTime = 100, Total = 600 }, true, false, true);
    builder.Add(new DataPoint { CurrentTime = 140, Total = 600 }, false, true, true);
    builder.Finish();
    var published = builder.Points.ToArray();
    builder.Add(new DataPoint { CurrentTime = 120, Total = 300 }, true, true, true);
    builder.Add(new DataPoint { CurrentTime = 100 }, true, true, false);
    builder.Add(new DataPoint { CurrentTime = 120, Total = 300 }, false, false, true);
    builder.Finish();
    builder.Finish();
    CollectionAssert.AreEqual(new[] { 100d, 120, 140 }, builder.Points.Select(p => p.CurrentTime).ToArray());
    Assert.AreEqual(2L, builder.Points[0].AttemptsPerSecond);
    Assert.AreEqual(1L, builder.Points[0].HitsPerSecond);
    Assert.AreEqual(2L, builder.Points[1].HitsPerSecond);
    Assert.AreEqual(1800L, builder.Points[^1].Total);
    Assert.AreEqual(4u, builder.Points[^1].FightHits);
    Assert.AreEqual(2u, builder.Points[^1].FightCritHits);
    Assert.AreEqual(2u, builder.Points[^1].FightTcHits);
    Assert.AreEqual(StatsUtil.RatePerSecond(1800, 41), builder.Points[^1].ValuePerSecond);
    Assert.AreEqual("Owner", builder.Points[^1].PlayerName);
    Assert.AreEqual(1200L, published[^1].Total);
    Assert.AreEqual(1L, published[0].AttemptsPerSecond);
    Assert.AreEqual(106d, published[1].CurrentTime, "old published gap markers remain untouched");
  }

  [TestMethod]
  public void ShuffledInputAndRepeatedFlushesMatchTheSortedStreamIncludingMissesAndFractionalTimes()
  {
    var random = new Random(7139);
    var records = Enumerable.Range(0, 150).Select(i =>
      (Time: 100d + random.Next(100) + (i % 2 * 0.5), Amount: i % 4 == 0 ? 0L : i * 17L,
        Hit: i % 4 != 0, Crit: i % 3 == 0, Twin: i % 5 == 0)).ToArray();
    var expected = new ChartSeriesBuilder("Player");
    foreach (var record in records.OrderBy(r => r.Time))
      expected.Add(new DataPoint { CurrentTime = record.Time, Total = record.Amount }, record.Crit, record.Twin, record.Hit);
    expected.Finish();

    var actual = new ChartSeriesBuilder("Player");
    foreach (var record in records)
    {
      var input = new DataPoint { CurrentTime = record.Time, Total = record.Amount };
      actual.Add(input, record.Crit, record.Twin, record.Hit);
      input.Total = -1;
      actual.Finish();
    }
    Assert.HasCount(expected.Points.Count, actual.Points);
    for (var i = 0; i < expected.Points.Count; i++)
    {
      var a = actual.Points[i];
      var e = expected.Points[i];
      Assert.AreEqual(e.CurrentTime, a.CurrentTime);
      Assert.AreEqual(e.Total, a.Total);
      Assert.AreEqual(e.TotalPerSecond, a.TotalPerSecond);
      Assert.AreEqual(e.AttemptsPerSecond, a.AttemptsPerSecond);
      Assert.AreEqual(e.FightHits, a.FightHits);
      Assert.AreEqual(e.FightCritHits, a.FightCritHits);
      Assert.AreEqual(e.FightTcHits, a.FightTcHits);
      Assert.AreEqual(e.ValuePerSecond, a.ValuePerSecond);
      Assert.AreEqual(e.Avg, a.Avg);
      Assert.AreEqual(e.CritRate, a.CritRate);
      Assert.AreEqual(e.TcRate, a.TcRate);
    }
  }
}
