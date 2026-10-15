namespace EQLogParser;

[TestClass]
public class MeterCoverageTest
{
  [TestMethod]
  public void CoverageDeltasMatchTheSharedUnionAfterEveryInsertion()
  {
    var random = new Random(391);
    var range = new TimeRange();
    var total = 0d;
    for (var i = 0; i < 10000; i++)
    {
      var begin = random.Next(0, 100000);
      var end = begin + random.Next(-2, 100);
      total += range.AddAndMeasure(new TimeSegment(begin, end));
      Assert.AreEqual(range.GetTotal(), total, $"union insertion {i}");
    }
  }

  [TestMethod]
  public void TouchingBridgingAndDuplicateRangesDoNotChargeTwice()
  {
    var coverage = new MeterCoverage();
    coverage.Add(0, 3);
    coverage.Add(10, 12);
    Assert.AreEqual(7d, coverage.Seconds);
    coverage.Add(4, 9);
    Assert.AreEqual(13d, coverage.Seconds);
    coverage.Add(0, 12);
    Assert.AreEqual(13d, coverage.Seconds);
  }

  [TestMethod]
  public void OverlappingAnnotationWindowsKeepRecordOrderWithoutRepeatingSharedRecords()
  {
    var store = RecordsStore.Instance;
    store.Clear();
    try
    {
      var time = FixtureTime.Base;
      store.Add(new SpecialRecord { Player = "Illuminai", Code = "A" }, time + 99);
      store.Add(new SpecialRecord { Player = "Illuminai", Code = "B" }, time + 105);
      store.Add(new SpecialRecord { Player = "Illuminai", Code = "C" }, time + 105);
      store.Add(new SpecialRecord { Player = "Illuminai", Code = "Z" }, time + 200);
      store.Add(new DeathRecord { Killed = "Illuminai" }, time + 111);
      store.Add(new DeathRecord { Killed = "Illuminai" }, time + 120);
      var raid = StatsUtil.CreatePlayerStats(Labels.RaidTotals);
      // The ranges stay disjoint, but their special/death offsets overlap.
      raid.AllRanges.Add(new TimeSegment(time + 100, time + 101));
      raid.AllRanges.Add(new TimeSegment(time + 110, time + 111));
      for (var pass = 0; pass < 2; pass++)
      {
        StatsUtil.PopulateSpecials(raid);
        Assert.AreEqual("AXBCX", raid.Specials["Illuminai"]);
        CollectionAssert.AreEqual(new[] { time + 111, time + 120 },
          raid.Deaths.Select(death => death.BeginTime).ToArray());
      }
    }
    finally
    {
      store.Clear();
    }
  }

  [TestMethod]
  public void AuxiliaryRangeWalkStopsBeforeFutureRecordsWithoutLosingLaterWindows()
  {
    var store = RecordsStore.Instance;
    store.Clear();
    var time = FixtureTime.Base;
    // Insert out of order: RecordsStore publishes these groups in ascending time.
    store.Add(new SpecialRecord { Player = "Illuminai", Code = "Z" }, time + 1000);
    store.Add(new SpecialRecord { Player = "Illuminai", Code = "B" }, time + 185);
    store.Add(new SpecialRecord { Player = "Illuminai", Code = "A" }, time + 85);
    store.Add(new SpecialRecord { Player = "Illuminai", Code = "N" }, time + 150);
    store.Add(new DeathRecord { Killed = "Illuminai" }, time + 220);
    store.Add(new DeathRecord { Killed = "Illuminai" }, time + 120);
    var raid = StatsUtil.CreatePlayerStats(Labels.RaidTotals);
    raid.AllRanges.Add(new TimeSegment(time + 100, time + 110));
    raid.AllRanges.Add(new TimeSegment(time + 200, time + 210));
    StatsUtil.PopulateSpecials(raid);
    Assert.AreEqual("AXBX", raid.Specials["Illuminai"]);
    Assert.HasCount(2, raid.Deaths);
  }
}
