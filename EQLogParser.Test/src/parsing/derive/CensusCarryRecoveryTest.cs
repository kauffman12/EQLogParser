using System.Reflection;

namespace EQLogParser;

[TestClass]
public class CensusCarryRecoveryTest
{
  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void ARetryAfterAChunkReadFailureCountsEveryRowOnce(bool failHealing)
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    var attacker = facts.InternName("Attacker");
    var defender = facts.InternName("Defender");
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Defender", IdentityKind.Npc, RuleStrength.Strong, "test");
    var damageCount = failHealing ? 1 : DamageFactTable.FactChunkSize + 1;
    var healCount = failHealing ? HealFactTable.HealChunkSize + 1 : 1;
    for (var i = 0; i < damageCount; i++)
      facts.AddFact(new DamageFact(i + 1, FixtureTime.BaseL, attacker, defender, 1, 0, 0, 0, ushort.MaxValue));
    for (var i = 0; i < healCount; i++)
      heals.AddHeal(new HealFact(i + 1, FixtureTime.BaseL, attacker, defender, 2, 0, 0, 0, 0, 0));
    facts.ColdifyNow();
    heals.ColdifyNow();
    var carry = new CensusCarry();

    // Fail after one complete chunk was added to the totals, then restore the store before retrying.
    void FailBuild() => Assert.ThrowsExactly<KeyNotFoundException>(() =>
      ClassificationReport.Build(timeline, facts, heals, null, null, null, carry));
    if (failHealing) WithMissingTail<HealFact>(heals, FailBuild);
    else WithMissingTail<DamageFact>(facts, FailBuild);

    for (var retry = 0; retry < 2; retry++)
    {
      var row = ClassificationReport.Build(timeline, facts, heals, null, null, null, carry).Find("Attacker");
      Assert.IsNotNull(row);
      Assert.AreEqual((double)damageCount, row.Damage);
      Assert.AreEqual(2d * healCount, row.Healing);
      Assert.AreEqual((long)damageCount + healCount, row.Events);
      Assert.AreEqual(damageCount, row.HitsOnMobs);
    }
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void ASkippedNameIsRecoveredWithoutDoubleCountingTheAcceptedPrefix(bool healing)
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    var first = facts.InternName("First");
    var target = facts.InternName("Target");
    var smallCount = facts.InternedNames.Count;
    var late = facts.InternName("Late");
    if (healing)
    {
      heals.AddHeal(new HealFact(1, FixtureTime.BaseL, first, target, 10, 0, 0, 0, 0, 0));
      heals.AddHeal(new HealFact(2, FixtureTime.BaseL, late, target, 20, 0, 0, 0, 0, 0));
    }
    else
    {
      facts.AddFact(new DamageFact(1, FixtureTime.BaseL, first, target, 10, 0, 0, 0, ushort.MaxValue));
      facts.AddFact(new DamageFact(2, FixtureTime.BaseL, late, target, 20, 0, 0, 0, ushort.MaxValue));
    }
    var carry = new CensusCarry();
    carry.Fold(facts, heals, null, facts.InternedNames, smallCount, facts.FactCount, heals.HealCount);
    Assert.AreEqual(1L, carry.Events[first]);
    var report = ClassificationReport.Build(null, facts, heals, null, null, null, carry);
    var firstRow = report.Find("First");
    var lateRow = report.Find("Late");
    Assert.IsNotNull(firstRow);
    Assert.IsNotNull(lateRow);
    Assert.AreEqual(10d, healing ? firstRow.Healing : firstRow.Damage);
    Assert.AreEqual(20d, healing ? lateRow.Healing : lateRow.Damage);
    Assert.AreEqual(1L, firstRow.Events);
    Assert.AreEqual(1L, lateRow.Events);
  }

  private static void WithMissingTail<T>(object table, Action action) where T : struct
  {
    var store = Field(table, "_rows");
    if (Field(store, "_chunks") is not T[][] chunks) throw new InvalidOperationException("Wrong chunk type");
    var tail = chunks[1];
    Array.Clear(chunks, 1, 1);
    try
    {
      action();
    }
    finally
    {
      chunks[1] = tail;
    }
  }

  private static object Field(object owner, string name) =>
    owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
      ?? throw new InvalidOperationException($"Missing field {name}");
}
