namespace EQLogParser;

[TestClass]
public sealed class MeterPendingPrefixTest
{
  [TestInitialize]
  public void Initialize()
  {
    PipelineHarness.EnsureDataStore();
    PlayerRegistry.Instance.Clear();
    RecordsStore.Instance.Clear();
  }

  [TestMethod]
  public void AChangedExclusionInvalidatesBothScansEvenWhenTheirCountsStillMatch()
  {
    var saved = AppSettings.IsBaneDamageEnabled;
    try
    {
      AppSettings.IsBaneDamageEnabled = false;
      var facts = new DamageFactTable(8);
      var player = facts.InternName("Illuminai");
      var rat = facts.InternName("a rat");
      var timeline = new EntityTimeline();
      timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "manual");
      timeline.SetIdentity("a rat", IdentityKind.Npc, RuleStrength.Manual, "manual");
      facts.AddFact(new DamageFact(1, FixtureTime.BaseL, player, rat, 600, LabelTypes.Bane,
        0, 0, DamageFactTable.NoSubtype));
      var projection = new FightProjection.FightProjectionCache();
      var rows = projection.Project(facts, timeline);
      var input = projection.Index.CaptureMeterInput(rows, facts, double.NegativeInfinity, double.PositiveInfinity);
      var pending = new OverlayPendingActivity(0);
      var activity = new MeterActivityIndex();
      pending.Update(facts, timeline);
      activity.Update(input);
      Assert.IsTrue(pending.Covers(1, timeline));
      Assert.IsTrue(activity.InputsCurrent);
      AppSettings.IsBaneDamageEnabled = true;
      Assert.IsFalse(pending.Covers(1, timeline), "covering the ordinal alone is not proof of the receipt's question");
      Assert.IsFalse(activity.InputsCurrent);
      pending.Update(facts, timeline);
      activity.Update(input);
      Assert.IsTrue(pending.Covers(1, timeline));
      Assert.IsTrue(activity.InputsCurrent);
      Assert.IsTrue(activity.For(rows.Single()).HasDamage);
      Assert.AreEqual(1, pending.LatestSequence);
    }
    finally
    {
      AppSettings.IsBaneDamageEnabled = saved;
    }
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void AReceiptBeyondThePendingPrefixDefersWithoutReopeningACompletedPull(bool noise)
  {
    var facts = new DamageFactTable(8);
    var player = facts.InternName("Illuminai");
    var rat = facts.InternName("a rat");
    var bat = facts.InternName("a bat");
    var guard = facts.InternName("a guard");
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "manual");
    foreach (var npc in new[] { "a rat", "a bat", "a guard" })
      timeline.SetIdentity(npc, IdentityKind.Npc, RuleStrength.Manual, "manual");
    facts.AddFact(new DamageFact(1, FixtureTime.BaseL, player, rat, 600, LabelTypes.Melee,
      0, 0, DamageFactTable.NoSubtype));
    facts.AddDeath(new DeathFact(2, FixtureTime.BaseL + 1, rat, player));
    var projection = new FightProjection.FightProjectionCache();
    var rows = projection.Project(facts, timeline);
    var pending = new OverlayPendingActivity(facts.FactCount);
    var arrivals = new MeterArrivals();
    arrivals.Record(facts.FactCount, 0);
    var lifecycle = new DamageOverlayLifecycle();
    var calculation = new DamageMeterCalculation();
    var attempts = 0;

    var result = MeterBuildRetry.Run(() =>
    {
      pending.Update(facts, timeline);
      var candidates = lifecycle.SelectCandidates(1, rows, 0);
      calculation.Activity.Update(projection.Index.CaptureMeterInput(candidates.Rows, facts,
        candidates.FromTime, double.PositiveInfinity, candidates.FirstOrdinal));
      if (++attempts == 1)
      {
        // Ingest lands after pending was read but before the receipt worker captures its prefix.
        facts.AddFact(new DamageFact(3, FixtureTime.BaseL + 1, noise ? guard : player, bat,
          noise ? 100 : 0, noise ? LabelTypes.Melee : LabelTypes.Miss, 0, 0, DamageFactTable.NoSubtype));
        arrivals.Record(facts.FactCount, 2);
      }
      var read = arrivals.BeginRead(timeline);
      var arrival = read.Complete(facts);
      if (!calculation.Activity.InputsCurrent || !pending.Covers(arrival.Through, timeline))
        return MeterBuildResult.Deferred;
      Assert.IsTrue(lifecycle.TrySelect(1, rows, 2, 0, arrival, read.Through, out var scope,
        pending, calculation.Activity.For));
      return MeterBuildResult.Completed(calculation.Update(projection.Index.CaptureMeterInput(scope.Rows,
        facts, scope.FromTime, double.PositiveInfinity, scope.FirstOrdinal, scope.WindowSecond)));
    });

    Assert.IsFalse(result.IsDeferred);
    Assert.AreEqual(noise ? 1 : 2, attempts, "only a newly eligible receipt needs a newer pending prefix");
    Assert.AreEqual(0, lifecycle.SelectCandidates(1, rows, 0).FirstOrdinal,
      "deferring must not retire any accounting");
    if (noise)
    {
      Assert.IsNotNull(result.Stats);
      Assert.AreEqual(600L, result.Stats.DamageStats.RaidStats.Total);
    }
    else
    {
      Assert.IsNull(result.Stats, "the new pull's miss cannot renew the rat's completed result");
      Assert.AreEqual(3, pending.LatestSequence);
    }
  }
}
