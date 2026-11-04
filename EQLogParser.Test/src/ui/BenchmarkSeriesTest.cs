namespace EQLogParser;

[TestClass]
public sealed class BenchmarkSeriesTest
{
  [TestMethod]
  public void ClassGroupingPercentTruncationAndPetNamesMatchTheBenchmarkContract()
  {
    var players = new[]
    {
      Player("Caster", "Wizard", 1), Player("TankTwo", "Warrior", 2),
      Player("Owner +Pets", "Wizard", 10, "Owner"), Player("Tank", "Warrior", 3)
    };
    var bars = BenchmarkSeries.Prepare(players, _ => true);
    CollectionAssert.AreEqual(new[] { "Tank", "TankTwo", "Owner", "Caster" }, bars.Select(bar => bar.Name).ToArray());
    CollectionAssert.AreEqual(new[] { 100, 66, 100, 10 }, bars.Select(bar => bar.Percent).ToArray());
    CollectionAssert.AreEqual(new[] { true, false, true, false }, bars.Select(bar => bar.IsFirst).ToArray());
    Assert.IsTrue(bars[2].HasPets);
    Assert.AreEqual(StatsUtil.FormatTotals(10, 1), bars[2].Total);
    Assert.AreEqual("Wizard", bars[2].ClassName);
  }

  [TestMethod]
  public void ExcludedNamesDoNotBecomeTheClassLeaderAndZeroTotalsCreateNoBars()
  {
    var bars = BenchmarkSeries.Prepare([
      Player("NotOurs", "Warrior", 1000), Player("Tank", "Warrior", 10), Player("Empty", "Wizard", 0)
    ], name => name != "NotOurs");
    Assert.AreEqual(1, bars.Count);
    Assert.AreEqual("Tank", bars[0].Name);
    Assert.AreEqual(100, bars[0].Percent);
    Assert.IsTrue(bars[0].IsFirst);
  }

  [TestMethod]
  public void DisplayFilteringDoesNotChangeTheCompleteExportSource()
  {
    var bars = BenchmarkSeries.Prepare([Player("Tank", "Warrior", 10), Player("Caster", "Wizard", 20)], _ => true);
    var visible = BarChartData.Benchmark(bars.Where(bar => bar.ClassName == "Warrior").ToArray());
    Assert.AreEqual(1, visible.Count);
    Assert.AreEqual(2, bars.Count);
    Assert.AreEqual("Caster", bars[1].Name);
    Assert.AreEqual(100, bars[1].Percent);
  }

  private static PlayerStats Player(string name, string className, long total, string original = "")
    => new() { Name = name, OrigName = original.Length == 0 ? name : original, ClassName = className, Total = total };
}
