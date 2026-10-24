namespace EQLogParser;

// Capture facts can exist outside every encounter; routed, unrouted and unrowed are distinct buckets.
[TestClass]
public class UnrowedFactsTest
{
  private const double T0 = FixtureTime.Base;
  private const string SelfSpellName = "Cloudburst Strike Feedback XII";

  [TestMethod]
  public void AFactNoEngagementOpensIsCountedRatherThanVanishing()
  {
    var facts = BuildFacts(
      ("Illuminai", "Bithika", 250, 0, LabelTypes.Melee),
      ("Bithika", "Illuminai", 175, 1, LabelTypes.Melee));
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
    timeline.SetIdentity("Bithika", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
    var (rows, index, claimed) = Derive(facts, timeline);

    Assert.HasCount(0, rows);
    Assert.HasCount(0, claimed);
    Assert.AreEqual(facts.FactCount, UnrowedCount(facts, claimed));
    Assert.AreEqual(0L, index.DamageFactCount + index.TankingFactCount + index.UnroutedFactCount);
  }

  [TestMethod]
  public void AnOrdinaryPullLeavesNoGapAtAll()
  {
    var facts = BuildFacts(
      ("Illuminai", "Grimling", 500, 0, LabelTypes.Melee),
      ("Bithika", "Grimling", 300, 1, LabelTypes.Dd),
      ("Grimling", "Illuminai", 120, 2, LabelTypes.Melee),
      ("Grimling", "Bithika", 90, 3, LabelTypes.Melee));
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
    timeline.SetIdentity("Bithika", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
    var (rows, index, claimed) = Derive(facts, timeline);

    Assert.HasCount(1, rows);
    Assert.AreEqual(0, UnrowedCount(facts, claimed));
    Assert.AreEqual(facts.FactCount, index.DamageFactCount + index.TankingFactCount + index.UnroutedFactCount,
      "The three routing counters account for every fact in an ordinary pull.");
  }

  [TestMethod]
  public void ARaidSideNameThatIsAlsoASelfTargetSpellStillGetsItsRows()
  {
    Assert.IsTrue(ClassificationRules.IsSelfTargetDamageSpell(SelfSpellName), "The fixture needs the shipped spell.");
    var facts = BuildFacts(
      ("Illuminai", "Grimling", 500, 0, LabelTypes.Melee),
      (SelfSpellName, "Grimling", 4_000, 1, LabelTypes.Melee),
      (SelfSpellName, "Grimling", 3_500, 2, LabelTypes.Melee));
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
    timeline.SetIdentity(SelfSpellName, IdentityKind.Pet, RuleStrength.Certain, "seed-petmap");
    timeline.AddAffiliation(AffiliationKind.PetOfPlayer, SelfSpellName, T0, T0 + 2_000,
      RuleStrength.Certain, "seed-petmap", "Bithika");
    var (rows, index, claimed) = Derive(facts, timeline);
    var row = rows.First(r => r.Name == "Grimling");

    Assert.AreEqual(facts.FactCount, claimed.Count);
    var stats = DerivedTotals.For([row], index, facts, new HealFactTable(facts))?.CombinedStats;
    Assert.IsNotNull(stats);
    Assert.AreEqual(8_000L, stats.RaidStats.Total);
    Assert.IsNull(stats.StatsList.FirstOrDefault(p => p.Name == SelfSpellName));
    var owner = stats.StatsList.FirstOrDefault(p => p.Name != "Illuminai");
    Assert.IsNotNull(owner);
    StringAssert.Contains(owner.Name, "Bithika");
    Assert.AreEqual(7_500L, owner.Total, "A spell-shaped pet's damage still belongs to its owner.");
  }

  [TestMethod]
  public void ANameThatIsOnlyASpellStillOpensNoFight()
  {
    Assert.IsTrue(ClassificationRules.IsSelfTargetDamageSpell(SelfSpellName), "The fixture needs the shipped spell.");
    var facts = new DamageFactTable(16);
    facts.AddFact(new DamageFact(0, (long)T0, facts.InternName(SelfSpellName), facts.InternName("Illuminai"),
      16_690, LabelTypes.Dd, DamageFact.FlagAttackerIsSpell, 0, ushort.MaxValue));
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
    var (rows, _, claimed) = Derive(facts, timeline);

    Assert.HasCount(0, rows);
    Assert.HasCount(0, claimed);
  }

  private static DamageFactTable BuildFacts(params (string Atk, string Def, long Dmg, double T, byte Label)[] rows)
  {
    var facts = new DamageFactTable(64);
    var seq = 0;
    foreach (var (atk, def, dmg, time, label) in rows)
    {
      facts.AddFact(new DamageFact(seq++, (long)(T0 + time), facts.InternName(atk), facts.InternName(def),
        dmg, label, 0, 0, ushort.MaxValue));
    }
    return facts;
  }

  private static (List<DerivedFight> Rows, FightFactIndex Index, HashSet<int> Claimed) Derive(
    DamageFactTable facts, EntityTimeline timeline)
  {
    ClassificationRules.Apply(facts, timeline);
    var index = new FightFactIndex(timeline);
    var claimed = new HashSet<int>();
    var rows = FightProjection.Build(facts, timeline, (fact, ordinal, owner, target) =>
    {
      claimed.Add(ordinal);
      index.OnFact(fact, ordinal, owner, target);
    });
    return (rows, index, claimed);
  }

  private static long UnrowedCount(DamageFactTable facts, HashSet<int> claimed) => facts.FactCount - claimed.Count;
}
