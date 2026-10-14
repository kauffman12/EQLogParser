namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class DerivedSnapshotWatermarkTest
  {
    [TestMethod]
    public void SnapshotKeepsItsProcessedDamageCountWhenTheLiveCaptureGrows()
    {
      var facts = new DamageFactTable(8);
      var attacker = facts.InternName("Illuminai");
      var defender = facts.InternName("a rat");
      facts.AddFact(new DamageFact(0, 1_000, attacker, defender, 500, LabelTypes.Melee, 0, 0, ushort.MaxValue));
      var timeline = new EntityTimeline();
      timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "manual");
      var snapshot = DerivedFightRows.Build([], timeline, 20, facts, new FightFactIndex());
      Assert.AreEqual(1, snapshot.DamageFactCount, "the damage clock must not use the combined heal/evidence count");
      facts.AddFact(new DamageFact(1, 1_002, attacker, defender, 200, LabelTypes.Melee, 0, 0, ushort.MaxValue));
      Assert.AreEqual(1, snapshot.DamageFactCount);
      Assert.AreEqual(2, facts.FactCount);
      snapshot.PendingActivity.Update(facts, snapshot.Timeline);
      Assert.IsTrue(snapshot.PendingActivity.IsRecent(1_003, 3));
      var next = DerivedFightRows.Build([], timeline, 21, facts, new FightFactIndex());
      Assert.AreEqual(2, next.DamageFactCount);
      next.PendingActivity.Update(facts, next.Timeline);
      Assert.IsFalse(next.PendingActivity.IsRecent(1_003, 3), "a fresh snapshot retires the pending hint");
    }
  }
}
