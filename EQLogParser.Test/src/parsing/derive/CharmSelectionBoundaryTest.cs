namespace EQLogParser;

[TestClass]
public class CharmSelectionBoundaryTest
{
  private const long Start = FixtureTime.BaseL;

  [TestMethod]
  public void ASpellSelectionCannotImportOverlappingCustodyOrPostCharmCombat()
  {
    var (facts, timeline) = Capture();
    Hit(facts, "Raider", "Bruiser", 0, 100);
    Hit(facts, "Raider", "Bruiser", 11, 200);
    Hit(facts, "Blood of Lherre Rk. III", "Raider", 12, 25, true);
    Hit(facts, "Bruiser", "Boss", 13, 300);
    Hit(facts, "Raider", "Bruiser", 20, 400);
    Hit(facts, "Bruiser", "Raider", 21, 500);

    var index = new FightFactIndex(timeline);
    var rows = FightProjection.Build(facts, timeline, index.OnFact);
    var spell = rows.Single(r => r.Name == "Blood of Lherre Rk. III");
    var scope = CharmPetRows.WithHiddenPets([spell], rows);
    Assert.HasCount(1, scope);
    Assert.HasCount(0, Damage(scope, index, facts));
    var bru = rows.Where(r => r.Name == "Bruiser").ToList();
    Assert.HasCount(3, bru);
    Assert.AreEqual(DerivedFightEnd.Charmed, bru[0].EndReason);
    Assert.IsTrue(bru[1].RaidPet);
    Assert.AreEqual(DerivedFightEnd.Released, bru[1].EndReason);
    Assert.IsFalse(bru[1].Dead, "release is not a second kill");
    Assert.IsFalse(bru[2].RaidPet);
    Assert.IsFalse(bru[2].CharmedOwned);
    Assert.AreEqual(Start + 20, bru[2].BeginTime);
    Assert.AreEqual(400L, bru[2].DamageToOwner);
    Assert.AreEqual(500L, bru[2].DamageByOwner);
    Assert.AreEqual(300L, Damage(CharmPetRows.WithHiddenPets([bru[0]], rows), index, facts).Sum(r => r.Total));
    Assert.AreEqual(400L, Damage(CharmPetRows.WithHiddenPets([bru[2]], rows), index, facts).Sum(r => r.Total));
    var petOutput = Damage([rows.Single(r => r.Name == "Boss")], index, facts).Single();
    Assert.AreEqual("Bruiser", petOutput.Attacker);
    Assert.AreEqual("Raider", petOutput.AttackerOwner);
    Assert.AreEqual(300L, petOutput.Total, "normal pet output already belongs to the target encounter");
  }

  [TestMethod]
  public void ReleaseAndRecharmBetweenHitsCannotJoinSeparateCustodies()
  {
    var (facts, timeline) = Capture();
    timeline.AddAffiliation(AffiliationKind.Friendly, "Bruiser", Start + 25, Start + 40,
      RuleStrength.Certain, "R9-charm", "Raider");
    Hit(facts, "Raider", "Bruiser", 0, 100);
    Hit(facts, "Raider", "Bruiser", 11, 200);
    Hit(facts, "Raider", "Bruiser", 26, 300);
    var rows = FightProjection.Build(facts, timeline);
    Assert.HasCount(3, rows);
    Assert.AreEqual(DerivedFightEnd.Released, rows[1].EndReason);
    Assert.IsNull(rows[2].EncounterRow, "an old encounter link crossed a release and a new charm");
    Assert.HasCount(2, CharmPetRows.WithHiddenPets([rows[0]], rows));
  }

  [TestMethod]
  public void TouchingCharmWindowsDoNotInventARelease()
  {
    var (facts, timeline) = Capture();
    timeline.AddAffiliation(AffiliationKind.Friendly, "Bruiser", Start + 20, Start + 40,
      RuleStrength.Certain, "R9-charm", "Raider");
    Hit(facts, "Raider", "Bruiser", 11, 200);
    Hit(facts, "Raider", "Bruiser", 21, 300);
    var rows = FightProjection.Build(facts, timeline);
    Assert.HasCount(1, rows);
    Assert.AreEqual(500L, rows[0].DamageToOwner);
  }

  [TestMethod]
  public void AppendingAcrossReleaseMatchesAFullProjectionAndKeepsEveryOrdinalOnce()
  {
    var (facts, timeline) = Capture();
    var state = new FightProjection.ProjectionState();
    var index = new FightFactIndex(timeline);
    Hit(facts, "Raider", "Bruiser", 0, 100);
    Hit(facts, "Raider", "Bruiser", 11, 200);
    FightProjection.Continue(state, facts, timeline, index.OnFact);
    Hit(facts, "Raider", "Bruiser", 20, 400);
    Hit(facts, "Bruiser", "Raider", 21, 500);
    var continued = FightProjection.Continue(state, facts, timeline, index.OnFact);
    var replay = FightProjection.Build(facts, timeline);
    CollectionAssert.AreEqual(replay.Select(Key).ToArray(), continued.Select(Key).ToArray());
    Assert.AreEqual(3L, index.DamageFactCount);
    Assert.AreEqual(1L, index.TankingFactCount);
    FightProjection.Continue(state, facts, timeline, index.OnFact);
    Assert.AreEqual(3L, index.DamageFactCount);
  }

  [TestMethod]
  public void QuietCustodyEndsAtObservedReleaseWithoutPredictingAFutureCapOrClaimingALaterDeath()
  {
    var (facts, timeline) = Capture();
    Hit(facts, "Raider", "Bruiser", 11, 200);
    var state = new FightProjection.ProjectionState();
    var early = FightProjection.Continue(state, facts, timeline);
    Assert.AreEqual(DerivedFightEnd.Open, early.Single().EndReason);
    Hit(facts, "Raider", "Boss", 21, 300);
    facts.AddDeath(new DeathFact(facts.FactCount, Start + 22, facts.InternName("Bruiser"), -1));
    var later = FightProjection.Continue(state, facts, timeline);
    var pet = later.Single(r => r.Name == "Bruiser");
    Assert.AreEqual(DerivedFightEnd.Released, pet.EndReason);
    Assert.IsFalse(pet.Dead, "a later hostile life's death must not kill released custody");
    CollectionAssert.AreEqual(FightProjection.Build(facts, timeline).Select(Key).ToArray(),
      later.Select(Key).ToArray());
  }

  [TestMethod]
  public void ACharmedPlayerKeepsHerEncounterButNotFriendlyFireAfterRelease()
  {
    var (facts, timeline) = Capture();
    timeline.SetIdentity("Bithika", IdentityKind.Player, RuleStrength.Certain, "R1-target");
    timeline.AddAffiliation(AffiliationKind.Friendly, "Bithika", Start + 10, Start + 20,
      RuleStrength.Certain, "R9-charm", "Raider");
    Hit(facts, "Raider", "Bithika", 11, 200);
    Hit(facts, "Raider", "Bithika", 20, 900);
    Hit(facts, "Raider", "Boss", 21, 300);
    var rows = FightProjection.Build(facts, timeline);
    var player = rows.Single(r => r.Name == "Bithika");
    Assert.IsFalse(player.RaidPet);
    Assert.IsTrue(CharmPetRows.Visible(rows).Contains(player));
    Assert.AreEqual(DerivedFightEnd.Released, player.EndReason);
    Assert.IsFalse(player.Dead);
    Assert.AreEqual(200L, player.DamageToOwner);
  }

  [TestMethod]
  public void ReleasedAccountingExpiresAndDoesNotJoinTheNextPull()
  {
    var released = new DerivedFight
    {
      Name = "Bruiser", BeginTime = Start, LastTime = Start, DamageTotal = 100,
      FactEnd = 1, EndReason = DerivedFightEnd.Released
    };
    var meter = new DamageOverlayLifecycle();
    Assert.IsTrue(meter.TrySelect(1, [released], Start + 1, 0, new MeterArrival(1, Start), 1, out var held));
    CollectionAssert.AreEqual(new[] { released }, held.Rows.ToArray());
    Assert.IsTrue(meter.TrySelect(1, [released], Start + 9, 0, new MeterArrival(1, Start), 1, out var expired));
    Assert.HasCount(0, expired.Rows);
    var next = new DerivedFight
    {
      Name = "Next", BeginTime = Start + 10, LastTime = Start + 10, DamageTotal = 200, FactEnd = 2
    };
    Assert.IsTrue(meter.TrySelect(1, [released, next], Start + 11, 0,
      new MeterArrival(2, Start + 10), 2, out var live));
    CollectionAssert.AreEqual(new[] { next }, live.Rows.ToArray());
  }

  private static string Key(DerivedFight r)
    => $"{r.Name}|{r.BeginTime}|{r.LastTime}|{r.RaidPet}|{r.EndReason}|{r.DamageToOwner}|{r.DamageByOwner}";

  private static List<DamageRecord> Damage(List<DerivedFight> rows, FightFactIndex index, DamageFactTable facts)
    => FightSummarySource.Build(rows, index, facts).Fights
      .SelectMany(f => f.DamageBlocks.SelectMany(b => b.Actions)).OfType<DamageRecord>().ToList();

  private static (DamageFactTable, EntityTimeline) Capture()
  {
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Raider", IdentityKind.Player, RuleStrength.Certain, "R1-target");
    timeline.SetIdentity("Bruiser", IdentityKind.Npc, RuleStrength.Certain, "R1-target");
    timeline.SetIdentity("Boss", IdentityKind.Npc, RuleStrength.Certain, "R1-target");
    timeline.AddAffiliation(AffiliationKind.Friendly, "Bruiser", Start + 10, Start + 20,
      RuleStrength.Certain, "R9-charm", "Raider");
    return (new DamageFactTable(), timeline);
  }

  private static void Hit(DamageFactTable facts, string attacker, string defender, int seconds, long amount,
    bool effect = false)
    => facts.AddFact(new DamageFact(facts.FactCount, Start + seconds, facts.InternName(attacker),
      facts.InternName(defender), amount, effect ? LabelTypes.Dot : LabelTypes.Melee,
      effect ? DamageFact.FlagAttackerIsSpell : (byte)0, 0, DamageFactTable.NoSubtype));
}
