namespace EQLogParser;

[TestClass]
public class MercHealingBoardTest
{
  [TestMethod]
  [DataRow("Clericone", "a hired bazu", 500L)]
  [DataRow("a gingerbread soother", "a gingerbread soother", 500L)]
  [DataRow("a gingerbread soother", "Clericone", 500L)]
  [DataRow("Clericone", "Candlefolk Mender", 500L)]
  [DataRow("Clericone", "a frostmaw sentinel", 0L)]
  public void ABoardCountsHealingToAndFromMercsWithoutRegistrySightings(
    string healer, string healed, long expectedTotal)
  {
    PipelineHarness.EnsureDataStore();
    PlayerRegistry.Instance.Clear();
    var facts = new DamageFactTable(16);
    var heals = new HealFactTable(facts);
    var time = (long)FixtureTime.Base;
    heals.AddHeal(new HealFact(0, time, heals.InternName(healer), heals.InternName(healed),
      500, overTotal: 0, typeId: LabelTypes.Heal, flags: 0, modMask: -1, subIdx: HealFact.NoSpell));
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(facts, timeline, heals);
    if (EQDataStore.Instance.IsKnownMerc(healed))
    {
      Assert.AreEqual(IdentityKind.Merc, timeline.Identity(healed));
    }
    Assert.IsFalse(PlayerRegistry.Instance.IsPetOrPlayerOrMerc(healed), "the registry must not mask the missing live lookup");

    var previous = IdentityLookup.LiveVerdict;
    IdentityLookup.LiveVerdict = timeline.IdentityAt;
    try
    {
      var range = new TimeRange();
      range.Add(new TimeSegment(time, time + 1));
      var builder = new HealingStatsBuilder();
      builder.BuildTotalStats(new GenerateStatsOptions
      {
        Source = "merc healing board test",
        AllRanges = range,
        Heals = HealSummarySource.Materialize(heals, range)
      });
      var result = builder.GetLastStats();
      Assert.IsNotNull(result?.CombinedStats);
      Assert.AreEqual(expectedTotal, result.CombinedStats.RaidStats.Total);
      if (expectedTotal > 0)
      {
        var stats = result.CombinedStats.StatsList.Single();
        Assert.AreEqual(healer, stats.OrigName, ignoreCase: true);
        Assert.AreEqual(expectedTotal, stats.Total);
      }
    }
    finally
    {
      IdentityLookup.LiveVerdict = previous;
      PlayerRegistry.Instance.Clear();
    }
  }
}
