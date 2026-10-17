namespace EQLogParser;

[TestClass]
public sealed class CaptureExportLensTest
{
  private Func<string, bool> _validator = _ => false;

  [TestInitialize]
  public void Setup()
  {
    PipelineHarness.EnsureDataStore();
    PlayerRegistry.Instance.Clear();
    _validator = CombatRecordLookup.IsValidClassName;
    CombatRecordLookup.IsValidClassName = name => name is "Cleric" or "Bard";
  }

  [TestCleanup]
  public void Cleanup()
  {
    PlayerRegistry.Instance.Clear();
    CombatRecordLookup.IsValidClassName = _validator;
  }

  [TestMethod]
  public void ProductionLensResolvesPossessiveAndConfessedOwnersAtFiniteLogTimes()
  {
    var facts = new DamageFactTable(16);
    var pet = facts.InternName("Bulgar`s pet");
    var custom = facts.InternName("Fluffy");
    facts.AddEvidence(new EvidenceFact(1, 100, custom, EvidenceFact.EvPetLeader, facts.InternAux("Bulgar")));
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(facts, timeline);
    var lens = CaptureExportLens.Create(timeline, facts.InternedNames);
    Assert.AreEqual("Bulgar", lens.At(facts.NameOf(pet), 100).Owner);
    Assert.AreEqual("Bulgar", lens.At("Fluffy", 100).Owner);
    Assert.IsTrue(lens.At("Fluffy", 100).Side);
    timeline.SetIdentity("Fluffy", IdentityKind.Npc, RuleStrength.Manual, "R10-manual");
    Assert.AreEqual("Pet", lens.At("Fluffy", 100).Kind, "later classification cannot mutate the export snapshot");
  }

  [TestMethod]
  public void ASavedOwnerOutsideTheNamePoolStillHasItsClassInTheFrozenLookup()
  {
    var registry = PlayerRegistry.Instance;
    registry.AddPetToPlayer("Fluffy", "Bulgar");
    registry.SetDefaultPlayerClass("Bulgar", "Cleric", init: true);
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Fluffy", IdentityKind.Pet, RuleStrength.Certain, "R26-savedpet");
    var lens = CaptureExportLens.Create(timeline, ["Fluffy"]);
    registry.Clear();
    Assert.AreEqual("Bulgar", lens.At("Fluffy", 100).Owner);
    Assert.AreEqual("Cleric", lens.At("Bulgar", 100).Class);
  }

  [TestMethod]
  public void CharmSideAndOwnershipFollowTheRecordTime()
  {
    var timeline = new EntityTimeline();
    timeline.SetIdentity("A skeleton", IdentityKind.Npc, RuleStrength.Certain, "R1-target");
    timeline.AddAffiliation(AffiliationKind.PetOfPlayer, "A skeleton", 100, 200, RuleStrength.Certain,
      "R9-charm", "Bulgar");
    var lens = CaptureExportLens.Create(timeline, ["A skeleton", "Bulgar"]);
    Assert.IsFalse(lens.At("A skeleton", 99).Side);
    Assert.IsTrue(lens.At("A skeleton", 100).Side);
    Assert.AreEqual("Bulgar", lens.At("A skeleton", 199).Owner);
    Assert.IsFalse(lens.At("A skeleton", 200).Side);
    Assert.AreEqual(string.Empty, lens.At("A skeleton", 200).Owner);
  }

  [TestMethod]
  [DataRow(nameof(IdentityKind.Player), false)]
  [DataRow(nameof(IdentityKind.Merc), false)]
  [DataRow(nameof(IdentityKind.Pet), false)]
  [DataRow(nameof(IdentityKind.Npc), true)]
  public void CharmFlipsExportDirectionExactlyAsItFlipsProjection(string kindWord, bool duringCharm)
  {
    var kind = Enum.Parse<IdentityKind>(kindWord);
    var time = (long)FixtureTime.Base;
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Probeactor", kind, RuleStrength.Certain, "R1-target");
    timeline.AddAffiliation(AffiliationKind.Friendly, "Probeactor", time, time + 10,
      RuleStrength.Certain, "R9-charm", "Probecharmer");
    var lens = CaptureExportLens.Create(timeline, ["Probeactor", "Probecharmer"]);
    Assert.AreEqual(!duringCharm, lens.At("Probeactor", time - 1).Side);
    Assert.AreEqual(duringCharm, lens.At("Probeactor", time).Side);
    Assert.AreEqual(duringCharm, lens.At("Probeactor", time + 9).Side);
    Assert.AreEqual(!duringCharm, lens.At("Probeactor", time + 10).Side);
    Assert.AreEqual(FightProjection.SideAt(timeline, "Probeactor", time) == FightProjection.Side.Player,
      lens.At("Probeactor", time).Side);
  }

  [TestMethod]
  public void ClassSnapshotUsesTheEventsPersonaAndSurvivesRegistryReset()
  {
    var registry = PlayerRegistry.Instance;
    registry.SetDefaultPlayerClass("Bulgar", "Bard", init: true);
    registry.SetActivePlayerClass("Bulgar", "Cleric", 1, 100);
    registry.SetActivePlayerClass("Bulgar", "Bard", 1, 200);
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Bulgar", IdentityKind.Player, RuleStrength.Certain, "R1-target");
    var lens = CaptureExportLens.Create(timeline, ["Bulgar", "Unseen"]);
    registry.Clear();
    Assert.AreEqual("Cleric", lens.At("Bulgar", 150).Class, "saved/latest class must not overwrite an older persona");
    Assert.AreEqual("Bard", lens.At("Bulgar", 250).Class);
    Assert.AreEqual(string.Empty, lens.At("Unseen", 150).Class);
  }
}
