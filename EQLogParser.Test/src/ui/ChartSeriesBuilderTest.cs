namespace EQLogParser;

[TestClass]
public sealed class ChartSeriesBuilderTest
{
  [TestMethod]
  public void FutureRecordsCannotChangeAnEarlierPointsDps()
  {
    var points = Build((100, 600), (102, 600));
    Assert.AreEqual(2, points.Count);
    Assert.AreEqual(600, points[0].ValuePerSecond);
    Assert.AreEqual(400, points[1].ValuePerSecond);
    Assert.AreEqual(600, points[0].Total);
  }

  /*
   * The clock's own failure mode: the walk hands one name's groups over in log order, so a record behind the newest second is a
   * producer fault rather than data. It used to throw, from inside LineChart's paint path, which had no catch — the whole chart then
   * fell to the application handler and kept its previous plot with its update key unset, so every repeated gesture walked the night
   * again. Now the second is refused, the denominator cannot shrink, and the count carries how much of the line was affected.
   */
  [TestMethod]
  public void OutOfOrderRecordsDegradeTheClockInsteadOfThrowing()
  {
    var clock = new ChartActivityClock();

    clock.Add(100);
    Assert.AreEqual(1, clock.Seconds, "the first second of a series counts as one");

    clock.Add(104);          // ordinary advance: the first second plus four of activity
    Assert.AreEqual(5, clock.Seconds);
    Assert.AreEqual(0, clock.OutOfOrder);

    clock.Add(102);          // behind its own newest second: refused, so no duration is subtracted or added
    Assert.AreEqual(5, clock.Seconds, "a backwards record cannot move the denominator either way");
    Assert.AreEqual(1, clock.OutOfOrder);

    clock.Add(103);          // still behind, because the newest second stayed where it was: counted again, silently
    Assert.AreEqual(5, clock.Seconds);
    Assert.AreEqual(2, clock.OutOfOrder);

    clock.Add(140);          // forward again across the engagement gap: one second, the same law as ever
    Assert.AreEqual(6, clock.Seconds);
    Assert.AreEqual(2, clock.OutOfOrder, "recovering is not a new fault");
  }

  [TestMethod]
  public void MissesCountAsAttemptsButNeverAsHitsCritsOrTwincasts()
  {
    var builder = new ChartSeriesBuilder("Player");
    builder.Add(new DataPoint { CurrentTime = 100, Total = 600 }, true, true, true);
    builder.Add(new DataPoint { CurrentTime = 100 }, true, true, false);
    builder.Finish();
    var point = builder.Points.Single();
    Assert.AreEqual(2, point.AttemptsPerSecond);
    Assert.AreEqual(1, point.HitsPerSecond);
    Assert.AreEqual(1u, point.FightHits);
    Assert.AreEqual(1, point.CritsPerSecond);
    Assert.AreEqual(1, point.TcPerSecond);
    Assert.AreEqual(600, point.Avg);
    Assert.AreEqual(100d, point.CritRate);
    Assert.AreEqual(100d, point.TcRate);

    var group = ChartAggregation.MergeSeries("Group", [builder.Points]);
    Assert.IsNotNull(group);
    Assert.AreEqual(600, group[0].Avg);
    Assert.AreEqual(100d, group[0].CritRate);
  }

  [TestMethod]
  public void GapMarkersKeepCumulativeAmountsAndRatesWithoutBuyingCombatSeconds()
  {
    var points = Build((100, 600), (140, 600));
    CollectionAssert.AreEqual(new[] { 100d, 106d, 134d, 140d }, points.Select(p => p.CurrentTime).ToArray());
    CollectionAssert.AreEqual(new long[] { 600, 600, 600, 1200 }, points.Select(p => p.Total).ToArray());
    foreach (var marker in points.Skip(1).Take(2))
    {
      Assert.AreEqual(600, marker.FightTotal);
      Assert.AreEqual(1u, marker.FightHits);
      Assert.AreEqual(600, marker.Avg);
      Assert.AreEqual(600, marker.ValuePerSecond);
      Assert.AreEqual(0, marker.TotalPerSecond);
      Assert.AreEqual(0, marker.AttemptsPerSecond);
      Assert.AreEqual(0, marker.HitsPerSecond);
    }
    Assert.AreEqual(600, points[^1].ValuePerSecond);

    var merged = ChartAggregation.MergeSeries("Group", [points]);
    Assert.IsNotNull(merged);
    CollectionAssert.AreEqual(points.Select(p => p.Total).ToArray(), merged.Select(p => p.Total).ToArray());
    CollectionAssert.AreEqual(points.Select(p => p.ValuePerSecond).ToArray(),
      merged.Select(p => p.ValuePerSecond).ToArray());
  }

  [TestMethod]
  public void GroupDpsCountsTheFirstSecondAndEveryInclusiveSecond()
  {
    var group = ChartAggregation.MergeSeries("Group", [Build((100, 600), (101, 600))]);
    Assert.IsNotNull(group);
    Assert.AreEqual(600, group[0].ValuePerSecond);
    Assert.AreEqual(600, group[1].ValuePerSecond);
    Assert.AreEqual(1200, group[1].FightTotal);
    Assert.AreEqual(2u, group[1].FightHits);
  }

  [TestMethod]
  public void GroupCarriesQuietMembersAndUsesOneSharedActivityClock()
  {
    var group = ChartAggregation.MergeSeries("Group", [Build((100, 600)), Build((102, 600), (140, 600))]);
    Assert.IsNotNull(group);
    Assert.AreEqual(1200, group[1].Total);
    Assert.AreEqual(400, group[1].ValuePerSecond);
    Assert.AreEqual(1800, group[^1].Total);
    Assert.AreEqual(450, group[^1].ValuePerSecond);
    Assert.AreEqual(600, group[^1].Avg);
  }

  [TestMethod]
  public void TheThirtySecondGapBoundaryIsUnchanged()
  {
    var joined = Build((100, 310), (130, 310));
    Assert.AreEqual(2, joined.Count);
    Assert.AreEqual(20, joined[^1].ValuePerSecond);
    var separate = Build((100, 310), (131, 310));
    Assert.AreEqual(4, separate.Count);
    Assert.AreEqual(310, separate[^1].ValuePerSecond);
  }

  [TestMethod]
  public void EmptyAndRepeatedFinishAddNoPointsAndInputsAreNotRetained()
  {
    var builder = new ChartSeriesBuilder("Pet", "Owner");
    builder.Finish();
    Assert.AreEqual(0, builder.Points.Count);
    var record = new DataPoint { CurrentTime = 100, Total = 300 };
    builder.Add(record, false, false, true);
    record.Total = 900;
    builder.Finish();
    builder.Finish();
    var point = builder.Points.Single();
    Assert.AreEqual(300, point.Total);
    Assert.AreEqual("Pet", point.Name);
    Assert.AreEqual("Owner", point.PlayerName);
    Assert.AreNotSame(record, point);
  }

  [TestMethod]
  public void FlushingThenContinuingDoesNotDuplicateOrCarryABucket()
  {
    var builder = new ChartSeriesBuilder("Player");
    builder.Add(new DataPoint { CurrentTime = 100, Total = 300 }, false, false, true);
    builder.Finish();
    var published = builder.Points[0];
    builder.Add(new DataPoint { CurrentTime = 100, Total = 300 }, false, false, true);
    builder.Finish();
    Assert.AreEqual(1, builder.Points.Count);
    Assert.AreEqual(300, published.Total, "Previously published snapshots must not mutate");
    Assert.AreEqual(600, builder.Points[0].TotalPerSecond);
    builder.Add(new DataPoint { CurrentTime = 102, Total = 600 }, false, false, true);
    builder.Finish();
    Assert.AreEqual(2, builder.Points.Count);
    Assert.AreEqual(600, builder.Points[1].TotalPerSecond);
    Assert.AreEqual(400, builder.Points[1].ValuePerSecond);
  }

  [TestMethod]
  public void EmptyMembersDoNotHideActiveMembersOrManufactureAGroup()
  {
    Assert.IsNull(ChartAggregation.MergeSeries("Empty", []));
    Assert.IsNull(ChartAggregation.MergeSeries("Empty", [[], []]));
    var merged = ChartAggregation.MergeSeries("Group", [[], Build((100, 600)), []]);
    Assert.IsNotNull(merged);
    Assert.AreEqual(600, merged.Single().ValuePerSecond);
  }

  [TestMethod]
  public void GroupLookupUsesTheCombinedPetSeriesWithoutChangingItsInputs()
  {
    var points = Build((100, 600), (140, 600));
    points[0].PlayerName = "Owner";
    var group = new GroupEntry
    {
      Name = "Group 1", Members = [new PlayerStats { Name = "Owner +Pets", OrigName = "Owner" }]
    };
    var result = ChartAggregation.AggregateGroups(new() { ["Owner +Pets"] = points }, [group]);
    Assert.AreEqual(1, result.Count);
    Assert.AreEqual("Group 1", result[0][0].Name);
    Assert.AreEqual(1200, result[0][^1].Total);
    Assert.AreEqual(600, result[0][^1].ValuePerSecond);
    Assert.AreEqual("Player", points[0].Name);
    Assert.AreEqual(4, points.Count);
  }

  [TestMethod]
  public void AvoidanceOnlyGroupsHaveTimeAndAttemptsButNoFakeHitRates()
  {
    var builder = new ChartSeriesBuilder("Tank");
    builder.Add(new DataPoint { CurrentTime = 100 }, false, false, false);
    builder.Add(new DataPoint { CurrentTime = 102 }, false, false, false);
    builder.Finish();
    var group = ChartAggregation.MergeSeries("Group", [builder.Points]);
    Assert.IsNotNull(group);
    Assert.AreEqual(0, group[^1].Total);
    Assert.AreEqual(0, group[^1].Avg);
    Assert.AreEqual(0d, group[^1].CritRate);
    Assert.AreEqual(1, group[^1].AttemptsPerSecond);
    Assert.AreEqual(0u, group[^1].FightHits);
  }

  [TestMethod]
  public void CursorMergeMatchesIndependentRawRecordAccounting()
  {
    var random = new Random(741);
    var records = new List<(double Time, long Amount, bool Hit, bool Crit, bool Twin)>();
    var members = new List<List<DataPoint>>();
    for (var member = 0; member < 6; member++)
    {
      var builder = new ChartSeriesBuilder("Member" + member);
      var time = 100d;
      for (var i = 0; i < 100; i++)
      {
        time += random.Next(0, 45);
        var hit = random.Next(4) != 0;
        var amount = hit ? random.Next(1, 10000) : 0;
        var crit = hit && random.Next(3) == 0;
        var twin = hit && random.Next(5) == 0;
        records.Add((time, amount, hit, crit, twin));
        builder.Add(new DataPoint { CurrentTime = time, Total = amount }, crit, twin, hit);
      }
      builder.Finish();
      members.Add(builder.Points);
    }
    var merged = ChartAggregation.MergeSeries("Group", members);
    Assert.IsNotNull(merged);
    CollectionAssert.AreEqual(members.SelectMany(m => m).Select(p => p.CurrentTime).Distinct().Order().ToArray(),
      merged.Select(p => p.CurrentTime).ToArray());
    foreach (var point in merged)
    {
      var prefix = records.Where(r => r.Time <= point.CurrentTime).ToList();
      var bucket = prefix.Where(r => r.Time == point.CurrentTime).ToList();
      var times = prefix.Select(r => r.Time).Distinct().Order().ToArray();
      var seconds = 1d;
      for (var i = 1; i < times.Length; i++)
        seconds += times[i] - times[i - 1] > 30 ? 1 : times[i] - times[i - 1];
      var total = prefix.Sum(r => r.Amount);
      var hits = prefix.Count(r => r.Hit);
      var rolling = prefix.Where(r => r.Time >= point.CurrentTime - 5).Sum(r => r.Amount);
      Assert.AreEqual(total, point.Total);
      Assert.AreEqual(total, point.FightTotal);
      Assert.AreEqual((uint)hits, point.FightHits);
      Assert.AreEqual((long)Math.Round(total / seconds, 2), point.ValuePerSecond);
      Assert.AreEqual(hits == 0 ? 0 : (long)Math.Round((decimal)total / hits, 2), point.Avg);
      Assert.AreEqual(hits == 0 ? 0 : Math.Round(100d * prefix.Count(r => r.Crit) / hits, 2), point.CritRate);
      Assert.AreEqual(hits == 0 ? 0 : Math.Round(100d * prefix.Count(r => r.Twin) / hits, 2), point.TcRate);
      Assert.AreEqual(bucket.Sum(r => r.Amount), point.TotalPerSecond);
      Assert.AreEqual((long)bucket.Count, point.AttemptsPerSecond);
      Assert.AreEqual(rolling, point.RollingTotal);
      Assert.AreEqual(rolling / 6, point.RollingDps);
    }
  }

  private static List<DataPoint> Build(params (double Time, long Amount)[] records)
  {
    var builder = new ChartSeriesBuilder("Player");
    foreach (var (time, amount) in records)
      builder.Add(new DataPoint { CurrentTime = time, Total = amount }, false, false, true);
    builder.Finish();
    return builder.Points;
  }
}
