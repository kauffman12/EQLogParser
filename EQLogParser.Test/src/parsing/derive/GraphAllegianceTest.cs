#nullable enable annotations
namespace EQLogParser;

[TestClass]
public class GraphAllegianceTest
{
  private const long Start = FixtureTime.BaseL;
  private const string Boss = "Xev Bristlebane";
  private const string Pet = "Plimpy";
  private const string Effect = "Curse XVII Rk. III";
  private int _seq;

  [TestInitialize]
  public void Setup()
  {
    PipelineHarness.EnsureDataStore();
    PlayerRegistry.Instance.Clear();
    ClassificationRules.ResetRuleHealth();
    _seq = 0;
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void TargetingARaidPetCannotEraseTheBossOrCreateASpellFight(bool targeted)
  {
    var (facts, heals) = Capture(targeted);
    for (var i = 0; i < 15; i++) Heal(facts, heals, i, 80);
    var timeline = Classify(facts, heals, out var outcome);

    Assert.AreEqual(IdentityKind.Npc, timeline.IdentityWithSource(Boss, out var source));
    Assert.AreEqual("R7-side", source);
    Assert.AreEqual(targeted ? IdentityKind.Npc : IdentityKind.Player, timeline.Identity(Pet));
    Assert.AreEqual(targeted, outcome.OurPets.Contains(Pet));
    Assert.AreEqual(targeted, timeline.IsOurPetAt(Pet, Start + 70));
    Assert.IsNull(timeline.OwnerOf(Pet, Start + 70), "allegiance does not invent an owner");

    var index = new FightFactIndex(timeline);
    var rows = FightProjection.Build(facts, timeline, index.OnFact);
    Assert.IsFalse(rows.Any(r => r.Name == Effect || r.Name == Pet));
    Assert.AreEqual(700L, rows.Sum(r => r.DamageToOwner));
    Assert.AreEqual(40L, rows.Sum(r => r.DamageByOwner));
    Assert.AreEqual(3L, index.DamageFactCount);
    Assert.AreEqual(4L, index.TankingFactCount);

    var records = FightSummarySource.Build(rows, index, facts).Fights
      .SelectMany(f => f.DamageBlocks.SelectMany(b => b.Actions)).OfType<DamageRecord>().ToList();
    var tick = records.Single(r => r.Total == 500);
    Assert.AreEqual(Labels.Unk, tick.Attacker);
    Assert.AreEqual(Boss, tick.Defender);
    Assert.AreEqual(Effect, tick.SubType);
    var spell = facts.Facts[facts.FactCount - 1];
    Assert.AreEqual(MeterPanels.Damage,
      MeterActivityIndex.AcceptsPending(spell, timeline, Effect, Boss, MeterDamageFilter.Current));
  }

  [TestMethod]
  public void GraphOnlyPlayerEvidenceCanVetoPetAllegianceButCannotSupportIt()
  {
    var (facts, heals) = Capture(true);
    for (var i = 0; i < 15; i++) Heal(facts, heals, i, 80);
    for (var i = 0; i < 3; i++)
      Hit(facts, "Quietraider", $"A hostile creature {i}", i * 40, 100);
    for (var i = 0; i < 10; i++) Hit(facts, Pet, "Quietraider", 90 + i, 10);

    var timeline = Classify(facts, heals, out var outcome);
    Assert.AreEqual(IdentityKind.Player, timeline.Identity("Quietraider"));
    Assert.IsFalse(outcome.OurPets.Contains(Pet), "moving R18 before its safety veto would adopt a hostile");
    Assert.IsFalse(timeline.IsOurPetAt(Pet, Start + 100));
    Assert.AreEqual(IdentityKind.Unknown, timeline.Identity(Boss));
  }

  [TestMethod]
  public void GraphOnlyHealersCannotSupplyPositivePetProof()
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    facts.AddEvidence(new EvidenceFact(_seq++, Start, facts.InternName(Pet), EvidenceFact.EvTargetedNpc));
    for (var i = 0; i < 15; i++)
    {
      var healer = $"Quiethealer{i}";
      for (var mob = 0; mob < 3; mob++)
        Hit(facts, healer, $"A hostile creature {mob}", mob * 40, 100);
      heals.AddHeal(new HealFact(_seq++, Start + 80, facts.InternName(healer), facts.InternName(Pet),
        100, 0, LabelTypes.Heal, 0, 0, HealFact.NoSpell));
    }
    var timeline = Classify(facts, heals, out var outcome);
    Assert.AreEqual(IdentityKind.Player, timeline.Identity("Quiethealer0"));
    Assert.IsFalse(outcome.OurPets.Contains(Pet), "graph-only healers cannot bootstrap allegiance");
    Assert.IsFalse(timeline.IsOurPetAt(Pet, Start + 80));
  }

  [TestMethod]
  public void PrivateSafetyGraphClaimsCannotBecomeFinalGraphEvidence()
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    for (var i = 0; i < 3; i++)
    {
      Hit(facts, "Quietraider", $"A hostile creature {i}", i * 40, 100);
      Hit(facts, Boss, "Quietraider", (i * 40) + 1, 10);
    }
    var timeline = Classify(facts, heals, out _);
    Assert.AreEqual(IdentityKind.Player, timeline.Identity("Quietraider"));
    Assert.AreEqual(IdentityKind.Unknown, timeline.Identity(Boss),
      "an inferred player may veto a pet claim, never bootstrap a second graph verdict");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void OwnershipIsReadAtTheAttackTimeAndRealOppositionStillVetoes(bool afterTail)
  {
    var (facts, heals) = Capture(true);
    if (afterTail) Hit(facts, Boss, Pet, 100, 10);
    var timeline = new EntityTimeline();
    timeline.AddAffiliation(AffiliationKind.PetOfPlayer, Pet, double.NegativeInfinity, Start + 90,
      RuleStrength.Certain, "R5-owner", "Ownername");
    ClassificationRules.Apply(facts, timeline, heals);
    Assert.AreEqual(afterTail ? IdentityKind.Unknown : IdentityKind.Npc, timeline.Identity(Boss));
  }

  [TestMethod]
  public void GenuineHostileOppositionIsNotWeakenedIntoAMajorityVote()
  {
    var (facts, heals) = Capture(false);
    for (var i = 0; i < 15; i++) Heal(facts, heals, i, 80);
    Hit(facts, Boss, "A real hostile", 90, 10);
    var timeline = Classify(facts, heals, out _);
    Assert.AreEqual(IdentityKind.Unknown, timeline.Identity(Boss));
  }

  [TestMethod]
  [DataRow((int)IdentityKind.Npc, (int)IdentityKind.Npc)]
  [DataRow((int)IdentityKind.Player, (int)IdentityKind.Player)]
  public void CharmedDefendersContributeTheirCurrentSideNotTheirBaseIdentity(int kind, int expected)
  {
    var facts = new DamageFactTable();
    var timeline = new EntityTimeline();
    for (var i = 0; i < 3; i++)
    {
      var victim = $"Victim{i}";
      timeline.SetIdentity(victim, (IdentityKind)kind, RuleStrength.Certain, "R1-target");
      timeline.AddAffiliation(AffiliationKind.Friendly, victim, Start, Start + 100,
        RuleStrength.Certain, "R9-charm");
      Hit(facts, Boss, victim, i * 40, 10);
    }
    ClassificationRules.Apply(facts, timeline);
    Assert.AreEqual((IdentityKind)expected, timeline.Identity(Boss));
  }

  [TestMethod]
  public void GrowingAllegianceAndItsRetractionMatchFreshReplayIncludingIdleAndReset()
  {
    var (facts, heals) = Capture(true);
    var carry = new ClassificationState();
    for (var i = 0; i < 14; i++) Heal(facts, heals, i, 80);
    var first = Classify(facts, heals, out _, carry);
    Assert.AreEqual(IdentityKind.Unknown, first.Identity(Boss));
    Heal(facts, heals, 14, 80);
    var second = AssertReplay(facts, heals, carry);
    Assert.AreEqual(IdentityKind.Npc, second.Identity(Boss));
    AssertReplay(facts, heals, carry);

    for (var i = 0; i < 10; i++) Hit(facts, Pet, "Healer0", 100 + i, 10);
    var third = AssertReplay(facts, heals, carry);
    Assert.AreEqual(IdentityKind.Unknown, third.Identity(Boss));
    Assert.IsFalse(third.IsOurPetAt(Pet, Start + 100));
    carry.Reset();
    AssertReplay(facts, heals, carry);
    Assert.AreEqual(IdentityKind.Npc, second.Identity(Boss), "a published result was mutated");
  }

  [TestMethod]
  public void ExtendingTheHealTailRecoloursPreviouslyWalkedEdges()
  {
    var (facts, heals) = Capture(true);
    for (var i = 0; i < 15; i++) Heal(facts, heals, i, 80);
    Hit(facts, Boss, Pet, 400, 10);
    var carry = new ClassificationState();
    var first = AssertReplay(facts, heals, carry);
    Assert.AreEqual(IdentityKind.Unknown, first.Identity(Boss));
    Heal(facts, heals, 0, 200);
    var second = AssertReplay(facts, heals, carry);
    Assert.AreEqual(IdentityKind.Npc, second.Identity(Boss));
  }

  [TestMethod]
  public void ALateNpcFrameForARaidAllyCannotEraseTheBoss()
  {
    var (facts, heals) = Capture(false);
    for (var i = 0; i < 15; i++) Heal(facts, heals, i, 80);
    var carry = new ClassificationState();
    Assert.AreEqual(IdentityKind.Npc, AssertReplay(facts, heals, carry).Identity(Boss));
    facts.AddEvidence(new EvidenceFact(_seq++, Start + 200, facts.InternName(Pet), EvidenceFact.EvTargetedNpc));
    var second = AssertReplay(facts, heals, carry);
    Assert.AreEqual(IdentityKind.Npc, second.Identity(Pet));
    Assert.IsTrue(second.IsOurPetAt(Pet, Start + 80));
    Assert.AreEqual(IdentityKind.Npc, second.Identity(Boss));
  }

  private (DamageFactTable Facts, HealFactTable Heals) Capture(bool targeted)
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    for (var i = 0; i < 15; i++)
      facts.AddEvidence(new EvidenceFact(_seq++, Start, facts.InternName($"Healer{i}"),
        EvidenceFact.EvJoinedRaid));
    if (targeted)
      facts.AddEvidence(new EvidenceFact(_seq++, Start, facts.InternName(Pet), EvidenceFact.EvTargetedNpc));
    Hit(facts, "Healer0", Boss, 0, 100);
    Hit(facts, Boss, "Healer0", 0, 10);
    Hit(facts, Boss, "Healer1", 40, 10);
    Hit(facts, Boss, Pet, 50, 10);
    Hit(facts, Boss, "Healer2", 80, 10);
    Hit(facts, Pet, Boss, 80, 100);
    facts.AddFact(new DamageFact(_seq++, Start + 81, facts.InternName(Effect), facts.InternName(Boss),
      500, LabelTypes.Dot, DamageFact.FlagAttackerIsSpell, 0, facts.InternSubtype(Effect)));
    return (facts, heals);
  }

  private void Hit(DamageFactTable facts, string attacker, string defender, int offset, long amount)
    => facts.AddFact(new DamageFact(_seq++, Start + offset, facts.InternName(attacker),
      facts.InternName(defender), amount, LabelTypes.Melee, 0, 0, DamageFactTable.NoSubtype));

  private void Heal(DamageFactTable facts, HealFactTable heals, int healer, int offset)
    => heals.AddHeal(new HealFact(_seq++, Start + offset, facts.InternName($"Healer{healer}"),
      facts.InternName(Pet), 100, 0, LabelTypes.Heal, 0, 0, HealFact.NoSpell));

  private static EntityTimeline Classify(DamageFactTable facts, HealFactTable heals,
    out ClassificationOutcome outcome, ClassificationState? carry = null)
  {
    var timeline = new EntityTimeline();
    outcome = ClassificationRules.Apply(facts, timeline, heals, carry);
    Assert.HasCount(0, outcome.FailedRules);
    return timeline;
  }

  private static EntityTimeline AssertReplay(DamageFactTable facts, HealFactTable heals, ClassificationState carry)
  {
    var timeline = Classify(facts, heals, out _, carry);
    var replay = Classify(facts, heals, out _);
    Assert.AreEqual(replay.StateStamp(), timeline.StateStamp());
    return timeline;
  }
}
