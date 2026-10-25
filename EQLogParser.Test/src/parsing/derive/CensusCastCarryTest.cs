namespace EQLogParser;

[TestClass]
public class CensusCastCarryTest
{
  [TestMethod]
  public void CastEvidenceArrivingBetweenCensusesKeepsTheFirstProofForEachGate()
  {
    PipelineHarness.EnsureDataStore();
    const string playerSpell = "Boastful Bellow XLVII";
    const string laterSpell = "Focused Paragon of Spirit XXXIV";
    const string petSpell = "Hobble of Spirits Snare VI";
    var facts = new DamageFactTable();
    var caster = facts.InternName("Caster");
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Caster", IdentityKind.Player, RuleStrength.Strong, "R4-spell");
    var carry = new CensusCarry();
    var empty = ClassificationReport.Build(timeline, facts, null, null, null, null, carry);
    Assert.IsNull(empty.Find("Caster")?.ReasonDetail);

    facts.AddEvidence(new EvidenceFact(1, FixtureTime.BaseL, caster, EvidenceFact.EvCast, facts.InternAux(petSpell)));
    ClassificationReport.Build(timeline, facts, null, null, null, null, carry);
    Assert.AreEqual(petSpell, carry.CastProofFor("R20-petspell", "Caster"));
    Assert.IsNull(carry.CastProofFor("R4-spell", "Caster"));

    facts.AddEvidence(new EvidenceFact(2, FixtureTime.BaseL + 1, caster, EvidenceFact.EvCast,
      facts.InternAux(playerSpell)));
    var first = ClassificationReport.Build(timeline, facts, null, null, null, null, carry);
    Assert.AreEqual(playerSpell, first.Find("Caster")?.ReasonDetail);

    facts.AddEvidence(new EvidenceFact(3, FixtureTime.BaseL + 2, caster, EvidenceFact.EvCast,
      facts.InternAux(laterSpell)));
    var last = ClassificationReport.Build(timeline, facts, null, null, null, null, carry);
    var fresh = ClassificationReport.Build(timeline, facts, null, null, null);
    Assert.AreEqual(playerSpell, last.Find("Caster")?.ReasonDetail);
    Assert.AreEqual(fresh.Find("Caster")?.ReasonDetail, last.Find("Caster")?.ReasonDetail);
    Assert.AreEqual(petSpell, carry.CastProofFor("R20-petspell", "Caster"));
    Assert.IsNull(empty.Find("Caster")?.ReasonDetail, "Published reports must not acquire later proof.");
  }
}
