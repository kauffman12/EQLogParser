namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class DerivedSnapshotWatermarkTest
  {
    [TestMethod]
    public void SnapshotKeepsItsProcessedDamageCountWhenTheLiveCaptureGrows()
    {
      /*
       * Fact clocks are .NET-epoch SECONDS, the same constant the headless suite builds every fixture on
       * (EQLogParser.Test/src/parsing/derive/FixtureTime.cs). Written with bare literals this test asked
       * "is 1_003 within 3 seconds of the newest fact" about a timestamp near 1970, which no amount of
       * pending-activity logic can answer yes to: the ask and the fact have to share the calendar.
       */
      const long T0 = 63_871_286_400L;
      var facts = new DamageFactTable(8);
      var attacker = facts.InternName("Illuminai");
      var defender = facts.InternName("a rat");
      facts.AddFact(new DamageFact(0, T0, attacker, defender, 500, LabelTypes.Melee, 0, 0, ushort.MaxValue));
      var timeline = new EntityTimeline();
      timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "manual");
      var snapshot = DerivedFightRows.Build([], timeline, 20, facts, new FightFactIndex());
      Assert.AreEqual(1, snapshot.DamageFactCount, "the damage clock must not use the combined heal/evidence count");
      facts.AddFact(new DamageFact(1, T0 + 2, attacker, defender, 200, LabelTypes.Melee, 0, 0, ushort.MaxValue));
      Assert.AreEqual(1, snapshot.DamageFactCount);
      Assert.AreEqual(2, facts.FactCount);
      snapshot.PendingActivity.Update(facts, snapshot.Timeline);
      Assert.IsTrue(snapshot.PendingActivity.IsRecent(T0 + 3, 3), "the unprojected suffix is recent activity");
      var next = DerivedFightRows.Build([], timeline, 21, facts, new FightFactIndex());
      Assert.AreEqual(2, next.DamageFactCount);
      next.PendingActivity.Update(facts, next.Timeline);
      Assert.IsFalse(next.PendingActivity.IsRecent(T0 + 3, 3), "a fresh snapshot retires the pending hint");
    }
  }
}
