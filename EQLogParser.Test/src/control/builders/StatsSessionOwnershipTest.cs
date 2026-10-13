namespace EQLogParser;

[TestClass]
public sealed class StatsSessionOwnershipTest
{
  [TestMethod]
  public void EveryBoardsEmptyAnswerCarriesTheCaptureThatSuppliedItsInputs()
  {
    var damage = new DamageStatsBuilder();
    var tanking = new TankingStatsBuilder();
    var healing = new HealingStatsBuilder();
    var seen = new List<StatsGenerationEvent>();
    damage.EventsGenerationStatus += seen.Add;
    tanking.EventsGenerationStatus += seen.Add;
    healing.EventsGenerationStatus += seen.Add;

    static GenerateStatsOptions Options() => new()
      { SessionId = 42, Source = "stats session ownership test", AllRanges = new TimeRange(), Heals = [] };
    damage.BuildTotalStats(Options());
    tanking.BuildTotalStats(Options());
    healing.BuildTotalStats(Options());

    Assert.AreEqual(3, seen.Select(e => e.Type).Distinct().Count());
    Assert.IsTrue(seen.Count >= 6, "each board must stamp both STARTED and its empty answer");
    foreach (var e in seen) Assert.AreEqual(42, e.SessionId, $"{e.Type} {e.State}");
  }

  [TestMethod]
  public void AHealingResliceKeepsItsInputsOriginalCaptureStamp()
  {
    var builder = new HealingStatsBuilder();
    var seen = new List<StatsGenerationEvent>();
    builder.EventsGenerationStatus += seen.Add;
    List<(double, HealRecord)> heals =
    [
      (1_000d, new HealRecord
        { Healer = "Caster", Healed = "Target", Type = Labels.Heal, SubType = "Superior Healing", Total = 100 })
    ];
    builder.BuildTotalStats(new GenerateStatsOptions
    {
      SessionId = 42,
      Source = "stats session ownership full",
      AllRanges = new TimeRange(new TimeSegment(1_000, 1_010)),
      Heals = heals
    });
    seen.Clear();
    builder.RebuildTotalStats(new GenerateStatsOptions { Source = "stats session ownership reslice", Heals = heals });
    Assert.IsTrue(seen.Count >= 2, "the reslice must have emitted start and answer events");
    foreach (var e in seen) Assert.AreEqual(42, e.SessionId, "a reslice must not restamp old inputs as a new capture");
  }
}
