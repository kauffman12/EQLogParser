namespace EQLogParser;

[TestClass]
public sealed class ManualPetOwnershipTest
{
  private const string Server = "ManualPetOwnershipTest";
  private const string Pet = "Reviewfluffy";
  private const string ChosenOwner = "Chosenowner";
  private const long T0 = FixtureTime.BaseL;
  private string _savedConfigDir = string.Empty;
  private string _savedServer = string.Empty;
  private string _root = string.Empty;

  [TestInitialize]
  public void Setup()
  {
    PipelineHarness.EnsureDataStore();
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServer = ConfigUtil.ServerName;
    _root = Path.Combine(Path.GetTempPath(), "manual-pet-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_root);
    ConfigUtil.ConfigDir = _root;
    ConfigUtil.ServerName = Server;
    PlayerRegistry.Instance.Clear();
    IdentityPriorStore.Instance.Init(Server);
    IdentityOverrideStore.Instance.Init(Server);
  }

  [TestCleanup]
  public void Cleanup()
  {
    PlayerRegistry.Instance.Clear();
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServer;
    IdentityPriorStore.Instance.Init(_savedServer);
    IdentityOverrideStore.Instance.Init(_savedServer);
    TakenBackNames.Clear();
    UnaskedRefresh.ClearGesture();
    Directory.Delete(_root, true);
  }

  [TestMethod]
  public void AManualOwnerWinsAcrossFreshAndCarriedPassesAndReachesExport()
  {
    var facts = Confessions();
    Assert.IsTrue(ClassificationCommands.ApplyVerdict(Pet, IdentityKind.Pet, ChosenOwner));
    var state = new ClassificationState();
    var first = Classify(facts, state);
    var next = Classify(facts, state);
    Assert.AreEqual(first.StateStamp(), next.StateStamp());
    var lens = CaptureExportLens.Create(next, facts.InternedNames);
    var index = new FightFactIndex(next);
    var rows = FightProjection.Build(facts, next, index.OnFact);
    var materialized = FightSummarySource.Build(rows, index, facts).Fights;
    var hit = materialized.SelectMany(fight => fight.DamageBlocks).SelectMany(block => block.Actions)
      .OfType<DamageRecord>().Single();
    Assert.AreEqual(ChosenOwner, hit.AttackerOwner);
    foreach (var time in new[] { T0 - 1, T0, T0 + 1, T0 + 2 })
    {
      Assert.AreEqual(ChosenOwner, next.OwnerOf(Pet, time));
      Assert.AreEqual(ChosenOwner, lens.At(Pet, time).Owner);
    }
  }

  [TestMethod]
  public void LaterAutomaticMappingsCannotReplaceTheManualPairEvenAfterReopening()
  {
    var facts = Confessions();
    Assert.IsTrue(ClassificationCommands.ApplyVerdict(Pet, IdentityKind.Pet, ChosenOwner));
    IdentityOverrideStore.Instance.Init(Server);
    PlayerRegistry.Instance.Clear();
    PlayerRegistry.Instance.AddPetToPlayer(Pet, "Firstowner", init: true);
    PlayerRegistry.Instance.AddPetToPlayer(Pet, "Secondowner");
    Assert.AreEqual(ChosenOwner, PlayerRegistry.Instance.GetPlayerFromPet(Pet));
    Assert.AreEqual(ChosenOwner, Classify(facts).OwnerOf(Pet, T0));

    Assert.IsTrue(ClassificationCommands.ApplyVerdict(Pet, IdentityKind.Pet, "Replacementowner"));
    IdentityOverrideStore.Instance.Init(Server);
    Assert.AreEqual("Replacementowner", Classify(facts).OwnerOf(Pet, T0));
  }

  [TestMethod]
  public void CharmTemporarilyOverridesTheManualOwnerButNotTheManualKind()
  {
    var facts = Confessions();
    ClassificationCommands.ApplyVerdict(Pet, IdentityKind.Pet, ChosenOwner);
    var timeline = new EntityTimeline();
    RegistrySeed.Apply(timeline, facts, T0, T0 + 1);
    ClassificationRules.Apply(facts, timeline);
    timeline.AddAffiliation(AffiliationKind.Friendly, Pet, T0 + 2, T0 + 4,
      RuleStrength.Certain, "R9-charm", "Charmer");
    IdentityOverrideStore.Instance.Apply(timeline);
    Assert.AreEqual(ChosenOwner, timeline.OwnerOf(Pet, T0 + 1));
    Assert.AreEqual("Charmer", timeline.OwnerOf(Pet, T0 + 2));
    Assert.IsTrue(timeline.IsCharmedAt(Pet, T0 + 3));
    Assert.AreEqual(FightProjection.Side.Npc, FightProjection.SideAt(timeline, Pet, T0 + 3));
    Assert.AreEqual(IdentityKind.Pet, timeline.IdentityAt(Pet, T0 + 3, out var strength, out _));
    Assert.AreEqual(RuleStrength.Manual, strength);
    Assert.AreEqual(ChosenOwner, timeline.OwnerOf(Pet, T0 + 4));
    Assert.IsFalse(timeline.IsCharmedAt(Pet, T0 + 4));
  }

  [TestMethod]
  public void CertainCapturedOwnershipRemainsAboveTheLeaderSentence()
  {
    var facts = Confessions();
    var timeline = new EntityTimeline();
    timeline.AddAffiliation(AffiliationKind.PetOfPlayer, Pet, double.NegativeInfinity,
      double.PositiveInfinity, RuleStrength.Certain, "R5-owner", "Proofowner");
    ClassificationRules.Apply(facts, timeline);
    Assert.AreEqual("Proofowner", timeline.OwnerOf(Pet, T0));
    Assert.AreEqual("Proofowner", timeline.OwnerOf(Pet, T0 + 1));
  }

  [TestMethod]
  public void TakingBackTheManualWordRestoresTheCapturedOwnershipWindows()
  {
    var facts = Confessions();
    ClassificationCommands.ApplyVerdict(Pet, IdentityKind.Pet, ChosenOwner);
    Assert.AreEqual(ChosenOwner, Classify(facts).OwnerOf(Pet, T0));
    ClassificationCommands.Recalculate(Pet);
    IdentityOverrideStore.Instance.Init(Server);
    var restored = Classify(facts);
    Assert.AreEqual("Firstowner", restored.OwnerOf(Pet, T0));
    Assert.AreEqual("Secondowner", restored.OwnerOf(Pet, T0 + 1));
  }

  [TestMethod]
  public void ALegacyKindOnlyOverrideDoesNotPromoteRememberedOwnershipToManual()
  {
    ConfigUtil.SaveIdentityOverrides([new(Pet, "Pet")], Server);
    IdentityOverrideStore.Instance.Init(Server);
    PlayerRegistry.Instance.AddPetToPlayer(Pet, ChosenOwner, init: true);
    var facts = Confessions();
    Assert.AreEqual("Firstowner", Classify(facts).OwnerOf(Pet, T0));
    // Explicitly accepting that remembered pair must not be mistaken for an existing manual owner.
    Assert.IsTrue(PetAssignment.Assign(Pet, ChosenOwner));
    Assert.AreEqual(ChosenOwner, Classify(facts).OwnerOf(Pet, T0));
    Assert.IsFalse(PetAssignment.Assign(Pet, ChosenOwner), "the same explicit pair is a no-op");
  }

  [TestMethod]
  public void ChangingToAKindOnlyVerdictRemovesTheManualOwnerFromDisk()
  {
    ClassificationCommands.ApplyVerdict(Pet, IdentityKind.Pet, ChosenOwner);
    ClassificationCommands.ApplyVerdict(Pet, IdentityKind.Player);
    IdentityOverrideStore.Instance.Init(Server);
    PlayerRegistry.Instance.AddPetToPlayer(Pet, "Thirdowner");
    Assert.AreEqual("Thirdowner", PlayerRegistry.Instance.GetPlayerFromPet(Pet));
    var timeline = new EntityTimeline();
    IdentityOverrideStore.Instance.Apply(timeline);
    Assert.IsNull(timeline.OwnerOf(Pet, T0));
    Assert.AreEqual(IdentityKind.Player, timeline.IdentityAt(Pet, T0));
  }

  private static DamageFactTable Confessions()
  {
    var facts = new DamageFactTable(16);
    var name = facts.InternName(Pet);
    facts.AddEvidence(new EvidenceFact(0, T0, name, EvidenceFact.EvPetLeader, facts.InternAux("Firstowner")));
    facts.AddEvidence(new EvidenceFact(1, T0 + 1, name, EvidenceFact.EvPetLeader, facts.InternAux("Secondowner")));
    facts.AddFact(new DamageFact(2, T0, name, facts.InternName("a review target"), 100,
      LabelTypes.Melee, 0, LineModifiersParser.None, DamageFactTable.NoSubtype));
    return facts;
  }

  private static EntityTimeline Classify(DamageFactTable facts, ClassificationState? state = null)
  {
    var timeline = new EntityTimeline();
    RegistrySeed.Apply(timeline, facts, T0, T0 + 1);
    ClassificationRules.Apply(facts, timeline, state: state);
    IdentityOverrideStore.Instance.Apply(timeline);
    return timeline;
  }
}
