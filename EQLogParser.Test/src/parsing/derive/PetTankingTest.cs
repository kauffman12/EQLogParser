namespace EQLogParser;

[TestClass]
public class PetTankingTest
{
  private const string Owner = "Tunky";
  private const string Pet = "Tunky`s pet";
  private const string Mob = "Tallongast, The Egg";
  private const double T0 = FixtureTime.Base;

  [TestInitialize]
  public void Initialize()
  {
    PipelineHarness.EnsureDataStore();
    PlayerRegistry.Instance.Clear();
    RecordsStore.Instance.Clear();
  }

  [TestMethod]
  public void ParsedPetAndMercIncomingDamageReachSeparateSummaryAndMeterRows()
  {
    var path = Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", "pet-tanking.txt");
    var run = PipelineHarness.RunFileDerived(path);
    ClassificationRules.Apply(run.Facts, run.Timeline);
    var cache = new FightProjection.FightProjectionCache();
    var rows = cache.Project(run.Facts, run.Timeline);
    Sectionizer.StampGroupIds(rows);
    Assert.AreEqual(IdentityKind.Pet, run.Timeline.Identity(Pet));
    Assert.AreEqual(IdentityKind.Merc, run.Timeline.Identity("A hired bazu"));
    Assert.HasCount(1, rows);
    Assert.AreEqual(Mob, rows[0].Name, "pets and mercs are not encounters in the fight list");
    var to = run.Facts.At(run.Facts.FactCount - 1).TimeS + 1d;
    var expected = DerivedTotals.ForOverlay(rows, cache.Index, run.Facts, double.NegativeInfinity, to);
    Assert.IsNotNull(expected?.TankStats);
    AssertTankRow(expected.TankStats, Pet, 48541);
    AssertTankRow(expected.TankStats, Owner, 900);
    AssertTankRow(expected.TankStats, "A hired bazu", 700);
    Assert.HasCount(3, expected.TankStats.StatsList);
    Assert.HasCount(0, expected.TankStats.Children);
    Assert.AreEqual(50141L, expected.TankStats.RaidStats.Total);
    Assert.AreEqual(52505L, expected.DamageStats.RaidStats.Total, "incoming damage must not become outgoing credit");

    var calculation = new DamageMeterCalculation();
    var input = cache.Index.CaptureMeterInput(rows, run.Facts, double.NegativeInfinity, to);
    DamageMeterAssert.Equal(expected, calculation.Update(input));
    DamageMeterAssert.Equal(expected, calculation.Update(input));

    run.Facts.AddFact(new DamageFact(run.Facts.FactCount, (long)to, run.Facts.InternName(Mob),
      run.Facts.InternName(Pet), 500, LabelTypes.Melee, 0, 0, ushort.MaxValue));
    ClassificationRules.Apply(run.Facts, run.Timeline);
    rows = cache.Project(run.Facts, run.Timeline);
    Sectionizer.StampGroupIds(rows);
    expected = DerivedTotals.ForOverlay(rows, cache.Index, run.Facts, double.NegativeInfinity, to);
    Assert.IsNotNull(expected?.TankStats);
    AssertTankRow(expected.TankStats, Pet, 49041);
    AssertTankRow(expected.TankStats, Owner, 900);
    input = cache.Index.CaptureMeterInput(rows, run.Facts, double.NegativeInfinity, to);
    DamageMeterAssert.Equal(expected, calculation.Update(input));
  }

  [TestMethod]
  [DataRow((int)IdentityKind.Player)]
  [DataRow((int)IdentityKind.Pet)]
  [DataRow((int)IdentityKind.Merc)]
  public void ADefenderNeedsNoOutgoingDamageToHaveATankingRow(int kind)
  {
    const string defender = "Companion";
    var facts = Facts((Mob, defender, 700));
    var timeline = Timeline();
    timeline.SetIdentity(defender, (IdentityKind)kind, RuleStrength.Manual, "manual");
    var (rows, index) = Project(facts, timeline);
    var result = DerivedTotals.ForOverlay(rows, index, facts, T0, T0 + 10);
    Assert.IsNotNull(result?.TankStats);
    AssertTankRow(result.TankStats, defender, 700);
    Assert.IsNull(result.DamageStats);
    Assert.AreEqual(1L, index.TankingFactCount);
    Assert.AreEqual(0L, index.UnroutedFactCount);
  }

  [TestMethod]
  public void OwnershipIntervalsAdmitIncomingDamageWithoutChangingAnNpcVerdict()
  {
    const string customPet = "Danglebait";
    var facts = Facts((Owner, Mob, 100), (Mob, customPet, 50), (Mob, Owner, 20));
    var timeline = Timeline();
    timeline.SetIdentity(customPet, IdentityKind.Npc, RuleStrength.Manual, "manual");
    timeline.AddAffiliation(AffiliationKind.PetOfPlayer, customPet, T0 - 1, T0 + 10,
      RuleStrength.Manual, "R18-healedpet", Owner);
    var (rows, index) = Project(facts, timeline);
    Assert.AreEqual(IdentityKind.Npc, timeline.Identity(customPet));
    var result = DerivedTotals.ForOverlay(rows, index, facts, T0, T0 + 10);
    Assert.IsNotNull(result?.TankStats);
    AssertTankRow(result.TankStats, customPet, 50);
    AssertTankRow(result.TankStats, Owner, 20);
    Assert.AreEqual(100L, result.DamageStats.RaidStats.Total);
  }

  [TestMethod]
  public void FriendlyFireOnAnOrdinaryPetStillEarnsNoDamageOrTankingCredit()
  {
    var facts = Facts((Owner, Mob, 100), (Owner, Pet, 999), (Mob, Pet, 50));
    var timeline = Timeline();
    timeline.SetIdentity(Pet, IdentityKind.Pet, RuleStrength.Manual, "manual");
    var (rows, index) = Project(facts, timeline);
    var result = DerivedTotals.ForOverlay(rows, index, facts, T0, T0 + 10);
    Assert.IsNotNull(result?.TankStats);
    AssertTankRow(result.TankStats, Pet, 50);
    Assert.AreEqual(50L, result.TankStats.RaidStats.Total);
    Assert.AreEqual(100L, result.DamageStats.RaidStats.Total);
    Assert.HasCount(1, rows);
  }

  [TestMethod]
  public void ACharmedEnemyKeepsRaidOutputOnItsAnchorAndIncomingOnTheAttackingEnemy()
  {
    const string charmed = "A borrowed skeleton";
    var facts = Facts((Owner, Mob, 100), (Owner, charmed, 30), (Mob, charmed, 40));
    var timeline = Timeline();
    timeline.SetIdentity(charmed, IdentityKind.Npc, RuleStrength.Manual, "manual");
    timeline.AddAffiliation(AffiliationKind.Friendly, charmed, T0 - 1, T0 + 10,
      RuleStrength.Manual, "R9-charm", Owner);
    var (rows, index) = Project(facts, timeline);
    var charmedRow = rows.Single(row => row.Name == charmed);
    Assert.HasCount(1, index.DamageOrdinalsFor(charmedRow));
    Assert.HasCount(0, index.TankingOrdinalsFor(charmedRow));
    var result = DerivedTotals.ForOverlay(rows, index, facts, T0, T0 + 10);
    Assert.IsNotNull(result?.TankStats);
    AssertTankRow(result.TankStats, charmed, 40);
    Assert.AreEqual(130L, result.DamageStats.RaidStats.Total);
  }

  private static DamageFactTable Facts(params (string Attacker, string Defender, uint Total)[] hits)
  {
    var facts = new DamageFactTable(64);
    foreach (var hit in hits)
      facts.AddFact(new DamageFact(facts.FactCount, (long)T0 + facts.FactCount,
        facts.InternName(hit.Attacker), facts.InternName(hit.Defender), hit.Total,
        LabelTypes.Melee, 0, 0, ushort.MaxValue));
    return facts;
  }

  private static EntityTimeline Timeline()
  {
    var timeline = new EntityTimeline();
    timeline.SetIdentity(Owner, IdentityKind.Player, RuleStrength.Manual, "manual");
    timeline.SetIdentity(Mob, IdentityKind.Npc, RuleStrength.Manual, "manual");
    return timeline;
  }

  private static (IReadOnlyList<DerivedFight> Rows, FightFactIndex Index) Project(
    DamageFactTable facts, EntityTimeline timeline)
  {
    ClassificationRules.Apply(facts, timeline);
    var cache = new FightProjection.FightProjectionCache();
    var rows = cache.Project(facts, timeline);
    Sectionizer.StampGroupIds(rows);
    return (rows, cache.Index);
  }

  private static void AssertTankRow(CombinedStats stats, string name, long total)
  {
    var row = stats.StatsList.SingleOrDefault(row => row.Name == name);
    Assert.IsNotNull(row, $"missing tanking row for {name}");
    Assert.AreEqual(name, row.OrigName);
    Assert.AreEqual(total, row.Total, name);
  }
}
