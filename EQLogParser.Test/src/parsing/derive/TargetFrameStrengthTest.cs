namespace EQLogParser;

[TestClass]
public class TargetFrameStrengthTest
{
  [TestMethod]
  [DataRow(EvidenceFact.EvJoinedGroup, (int)IdentityKind.Merc, 70, "R13-merc")]
  [DataRow(EvidenceFact.EvMercJoinedGroup, (int)IdentityKind.Merc, 70, "R13-merc")]
  [DataRow(EvidenceFact.EvWhoRoster, (int)IdentityKind.Player, 100, "R2-who")]
  public void AnNpcFrameBeatsBehaviorButNotPositivePlayerProof(
    byte evidence, int expectedKind, int expectedStrength, string expectedSource)
  {
    PipelineHarness.EnsureDataStore();
    PlayerRegistry.Instance.Clear();
    var facts = new DamageFactTable(16);
    var name = facts.InternName("Frameprobeone");
    var time = (long)FixtureTime.Base;
    facts.AddEvidence(new EvidenceFact(0, time, name, evidence));
    facts.AddEvidence(new EvidenceFact(1, time + 1, name, EvidenceFact.EvTargetedNpc));
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(facts, timeline);

    var kind = timeline.IdentityAt(facts.NameOf(name), double.PositiveInfinity, out var strength, out var source);
    Assert.AreEqual((IdentityKind)expectedKind, kind);
    Assert.AreEqual(expectedStrength, strength);
    Assert.AreEqual(expectedSource, source);
  }

  [TestMethod]
  public void ContradictoryFramesReportAConflictWithoutClaimingCertainHostility()
  {
    PipelineHarness.EnsureDataStore();
    PlayerRegistry.Instance.Clear();
    var facts = new DamageFactTable(16);
    var name = facts.InternName("Frameprobeone");
    var time = (long)FixtureTime.Base;
    facts.AddEvidence(new EvidenceFact(0, time, name, EvidenceFact.EvTargetedPlayer));
    facts.AddEvidence(new EvidenceFact(1, time + 1, name, EvidenceFact.EvTargetedNpc));
    var timeline = new EntityTimeline();
    var outcome = ClassificationRules.Apply(facts, timeline);

    CollectionAssert.Contains(outcome.Conflicts, facts.NameOf(name));
    Assert.AreEqual(IdentityKind.Npc,
      timeline.IdentityAt(facts.NameOf(name), double.PositiveInfinity, out var strength, out var source));
    Assert.AreEqual(70, strength);
    Assert.AreEqual("R1-conflict", source);
  }

  [TestMethod]
  public void ALateNpcFrameKeepsTheMercOnOurSideInCarriedAndFreshPasses()
  {
    PipelineHarness.EnsureDataStore();
    PlayerRegistry.Instance.Clear();
    var facts = new DamageFactTable(16);
    var bot = facts.InternName("a hired bazu");
    var mob = facts.InternName("a skeleton");
    var time = (long)FixtureTime.Base;
    facts.AddFact(new DamageFact(0, time, bot, mob, 400, LabelTypes.Melee, 0, 0, ushort.MaxValue));
    var state = new ClassificationState();
    ClassificationRules.Apply(facts, new EntityTimeline(), state: state);
    facts.AddEvidence(new EvidenceFact(1, time + 1, bot, EvidenceFact.EvTargetedNpc));

    var carried = new EntityTimeline();
    ClassificationRules.Apply(facts, carried, state: state);
    var fresh = new EntityTimeline();
    ClassificationRules.Apply(facts, fresh);
    Assert.AreEqual(fresh.StateStamp(), carried.StateStamp());
    Assert.AreEqual(IdentityKind.Merc, carried.Identity(facts.NameOf(bot)));
    var rows = FightProjection.Build(facts, carried);
    Assert.AreEqual(1, rows.Count);
    Assert.AreEqual("A skeleton", rows[0].Name);
    Assert.AreEqual(400, rows[0].DamageToOwner);
  }
}
