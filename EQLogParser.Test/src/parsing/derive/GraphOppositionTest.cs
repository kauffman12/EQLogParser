#nullable enable annotations
namespace EQLogParser;

[TestClass]
public class GraphOppositionTest
{
  private const long Start = FixtureTime.BaseL;
  private const string Mob = "UnlistedSpear";

  [TestInitialize]
  public void Setup()
  {
    PipelineHarness.EnsureDataStore();
    PlayerRegistry.Instance.Clear();
  }

  [TestMethod]
  [DataRow(Mob, "Bite of the Asp XXXVII")]
  [DataRow("UnlistedKnuckle", "Blood of Lherre Rk. III")]
  public void SustainedRaidAttacksOutweighOneIsolatedOpposingHit(string mob, string effect)
  {
    var facts = Capture(mob, 200, 1, 1, 0);
    facts.AddFact(new DamageFact(facts.FactCount, Start + 1000, facts.InternName(effect), facts.InternName(mob),
      19000, LabelTypes.Dot, DamageFact.FlagAttackerIsSpell, 0, facts.InternSubtype(effect)));
    var timeline = Classify(facts);
    Assert.AreEqual(IdentityKind.Npc, timeline.Identity(mob));
    var index = new FightFactIndex(timeline);
    var rows = FightProjection.Build(facts, timeline, index.OnFact);
    Assert.IsFalse(rows.Any(r => r.Name == effect));
    var tick = FightSummarySource.Build(rows, index, facts).Fights
      .SelectMany(f => f.DamageBlocks.SelectMany(b => b.Actions)).OfType<DamageRecord>().Single();
    Assert.AreEqual(Labels.Unk, tick.Attacker);
    Assert.AreEqual(mob, tick.Defender);
    Assert.AreEqual(19000L, tick.Total);
  }

  [TestMethod]
  [DataRow(49, 1, 1, 0, true)]
  [DataRow(48, 1, 1, 0, false)]
  [DataRow(1000, 3, 3, 0, false)]
  [DataRow(1000, 2, 1, 60, false)]
  [DataRow(1000, 1, 1, 0, true)]
  public void OppositionNeedsSmallShareAndNeitherBreadthNorDuration(int raidHits, int hostileHits,
    int hostileNames, int span, bool npc)
  {
    var facts = Capture(Mob, raidHits, hostileHits, hostileNames, span);
    Assert.AreEqual(npc ? IdentityKind.Npc : IdentityKind.Unknown, Classify(facts).Identity(Mob));
  }

  [TestMethod]
  public void ASingleRaidOpponentCannotBeAmplifiedIntoBreadthWhenEvidenceConflicts()
  {
    var facts = Capture(Mob, 200, 1, 1, 0, raidNames: 1, spacing: 40);
    Assert.AreEqual(IdentityKind.Unknown, Classify(facts).Identity(Mob));
  }

  [TestMethod]
  public void PlayerInferenceStillRefusesEvenOneRaidSideAttack()
  {
    var facts = new DamageFactTable();
    for (var i = 0; i < 200; i++) Hit(facts, "Quietcaster", $"A hostile {i % 3}", i);
    facts.AddEvidence(new EvidenceFact(0, Start, facts.InternName("Raider"), EvidenceFact.EvJoinedRaid));
    Hit(facts, "Quietcaster", "Raider", 201);
    Assert.AreEqual(IdentityKind.Unknown, Classify(facts).Identity("Quietcaster"));
  }

  [TestMethod]
  public void AContradictionGrowingPastTheLimitRetractsACarriedNpcVerdict()
  {
    var facts = Capture(Mob, 200, 1, 1, 0);
    var carry = new ClassificationState();
    Assert.AreEqual(IdentityKind.Npc, Classify(facts, carry).Identity(Mob));
    for (var i = 0; i < 10; i++) Hit(facts, Mob, "A hostile 0", 401 + i);
    var continued = Classify(facts, carry);
    Assert.AreEqual(IdentityKind.Unknown, continued.Identity(Mob));
    Assert.AreEqual(Classify(facts).StateStamp(), continued.StateStamp());
  }

  private static EntityTimeline Classify(DamageFactTable facts, ClassificationState? carry = null)
  {
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(facts, timeline, state: carry);
    return timeline;
  }

  private static DamageFactTable Capture(string mob, int raidHits, int hostileHits, int hostileNames, int span,
    int raidNames = 3, int spacing = 2)
  {
    Assert.IsFalse(EQDataStore.Instance.IsKnownNpc(mob), "this fixture must exercise inference, not the NPC list");
    var facts = new DamageFactTable();
    for (var i = 0; i < raidNames; i++)
      facts.AddEvidence(new EvidenceFact(i, Start, facts.InternName($"Raider{i}"), EvidenceFact.EvJoinedRaid));
    for (var i = 0; i < raidHits; i++) Hit(facts, mob, $"Raider{i % raidNames}", i * spacing);
    for (var i = 0; i < hostileHits; i++)
      Hit(facts, mob, $"A hostile {i % hostileNames}", (raidHits * spacing) + (i * span));
    return facts;
  }

  private static void Hit(DamageFactTable facts, string attacker, string defender, int seconds)
    => facts.AddFact(new DamageFact(facts.FactCount, Start + seconds, facts.InternName(attacker),
      facts.InternName(defender), 100, LabelTypes.Melee, 0, 0, DamageFactTable.NoSubtype));
}
