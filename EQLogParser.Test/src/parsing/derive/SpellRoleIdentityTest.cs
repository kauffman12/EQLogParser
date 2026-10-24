namespace EQLogParser;

[TestClass]
public class SpellRoleIdentityTest
{
  [TestInitialize]
  public void Setup()
  {
    PipelineHarness.EnsureDataStore();
    PlayerRegistry.Instance.Clear();
  }

  [TestMethod]
  public void StrangleOwnsAPetAndKeepsBothDirectionsOfCombat()
  {
    var run = Parse(
      "Strangle begins casting Spear of Ro X Rk. III.",
      "Strangle`s pet slashes a corrupted egg for 9001 points of damage.",
      "Strangle hit a corrupted egg for 431687 points of magic damage by Hammer of Magic III. (Critical)",
      "A corrupted egg hits Strangle for 64913 points of damage.",
      "Strangle has taken 178193 damage from Infected Magic by .");
    var timeline = Classify(run);
    Assert.IsTrue(ClassificationRules.SpellNamed("Strangle"));
    Assert.AreEqual(IdentityKind.Player, timeline.IdentityWithSource("Strangle", out var source));
    Assert.AreEqual("R5-owner", source);
    Assert.IsFalse(timeline.ClaimsOf("Strangle").Any(c => c.Kind == IdentityKind.Spell));
    var census = ClassificationReport.Build(timeline, run.Facts, run.HealFacts, null, null);
    var person = census.Find("Strangle");
    Assert.IsNotNull(person);
    Assert.AreEqual(IdentityKind.Player, person.Kind);
    Assert.IsTrue(person.Overrulable);

    var index = new FightFactIndex(timeline);
    var rows = FightProjection.Build(run.Facts, timeline, index.OnFact);
    Assert.IsFalse(rows.Any(r => r.Name == "Strangle"));
    Assert.AreEqual(440688L, rows.Sum(r => r.DamageToOwner));
    Assert.AreEqual(243106L, rows.Sum(r => r.DamageByOwner));
    Assert.AreEqual(2L, index.TankingFactCount);
    var board = DerivedTotals.For(rows, index, run.Facts, run.HealFacts)?.CombinedStats;
    Assert.IsNotNull(board);
    Assert.AreEqual(440688L, board.RaidStats.Total);
    Assert.IsTrue(board.StatsList.Any(p => p.Name.StartsWith("Strangle", StringComparison.Ordinal)));
  }

  [TestMethod]
  [DataRow("A gnoll has taken 123 damage from Novel Effect XIX by .")]
  [DataRow("You have taken 123 damage from by Novel Effect XIX.")]
  [DataRow("You have taken 123 damage from Novel Effect XIX.")]
  [DataRow("A gnoll has taken 123 damage by Novel Effect XIX.")]
  public void EveryCasterlessShapeCarriesItsRoleWithoutADatabaseEntry(string action)
  {
    var run = Parse(action);
    Assert.AreEqual(1, run.Facts.FactCount);
    var fact = run.Facts.Facts[0];
    Assert.IsTrue(fact.AttackerIsSpell);
    Assert.AreEqual("Novel Effect XIX", run.Facts.NameOf(fact.AtkIdx));
    var timeline = Classify(run);
    Assert.AreEqual(IdentityKind.Spell,
      timeline.IdentityAt("Novel Effect XIX", double.PositiveInfinity, out var strength, out var source));
    Assert.AreEqual(RuleStrength.SpellShape, strength);
    Assert.IsTrue(strength < RuleStrength.Medium);
    Assert.AreEqual("R21-spellshape", source);
  }

  [TestMethod]
  [DataRow("A gnoll has taken 123 damage from Novel Effect XIX by Strangle.")]
  [DataRow("Strangle hit a gnoll for 123 points of magic damage by Novel Effect XIX.")]
  [DataRow("Strangle slashes a gnoll for 123 points of damage.")]
  public void NamedCombatantsAreNotEffects(string action)
  {
    var run = Parse(action, "Tester begins casting Strangle.");
    Assert.AreEqual(1, run.Facts.FactCount);
    Assert.IsFalse(run.Facts.Facts[0].AttackerIsSpell);
    var timeline = Classify(run);
    Assert.AreNotEqual(IdentityKind.Spell, timeline.Identity("Strangle"));
  }

  [TestMethod]
  public void OrdinaryMeleeEarnsGraphEvidenceDespiteTheSpellName()
  {
    var run = Parse(
      "Strangle slashes a gnoll for 100 points of damage.",
      "Strangle slashes a rat for 200 points of damage.",
      "Strangle slashes a bat for 300 points of damage.",
      "Strangle slashes a snake for 400 points of damage.");
    var timeline = Classify(run);
    Assert.AreEqual(IdentityKind.Player, timeline.IdentityWithSource("Strangle", out var source));
    Assert.AreEqual("R7-graph", source);
  }

  [TestMethod]
  public void ASpellWithAPlayersNameCannotBorrowTheirDamageCreditOrLoseIncomingDamage()
  {
    var run = Parse(
      "Strangle`s pet slashes a gnoll for 10 points of damage.",
      "Strangle slashes a gnoll for 100 points of damage.",
      "A gnoll has taken 500 damage from Strangle by .",
      "You have taken 700 damage from Strangle by .",
      "Strangle has taken 900 damage from Strangle by .");
    var timeline = Classify(run);
    Assert.AreEqual(IdentityKind.Player, timeline.Identity("Strangle"));
    var index = new FightFactIndex(timeline);
    var rows = FightProjection.Build(run.Facts, timeline, index.OnFact);
    Assert.AreEqual(2210L, rows.Sum(r => r.DamageTotal));
    Assert.AreEqual(3L, index.DamageFactCount);
    Assert.AreEqual(2L, index.TankingFactCount);
    foreach (var ordinal in new[] { 3, 4 })
    {
      var incoming = run.Facts.Facts[ordinal];
      Assert.AreEqual(MeterPanels.Tanking, MeterActivityIndex.AcceptsPending(incoming, timeline,
        run.Facts.NameOf(incoming.AtkIdx), run.Facts.NameOf(incoming.DefIdx), MeterDamageFilter.Current));
    }
    var gnoll = rows.Single(r => r.Name == "A gnoll");
    Assert.AreEqual(100L, gnoll.PlayerRollup["Strangle"].Damage);
    var board = DerivedTotals.For([gnoll], index, run.Facts, run.HealFacts)?.CombinedStats;
    Assert.IsNotNull(board);
    Assert.AreEqual(610L, board.RaidStats.Total);
    Assert.AreEqual(110L, board.StatsList.Single(p => p.Name.StartsWith("Strangle", StringComparison.Ordinal)).Total);
  }

  [TestMethod]
  [DataRow((int)IdentityKind.Player, false, true)]
  [DataRow((int)IdentityKind.Pet, false, true)]
  [DataRow((int)IdentityKind.Merc, false, true)]
  [DataRow((int)IdentityKind.Unknown, false, true)]
  [DataRow((int)IdentityKind.Npc, true, true)]
  [DataRow((int)IdentityKind.Npc, false, false)]
  public void AnEffectMatchingItsVictimKeepsTheVictimsCombatDirection(int kind, bool owned, bool incoming)
  {
    var run = Parse("Mojo has taken 500 damage from Mojo by .");
    var fact = run.Facts.Facts[0];
    Assert.IsTrue(fact.AttackerIsSpell);
    Assert.AreEqual(fact.AtkIdx, fact.DefIdx);
    var timeline = new EntityTimeline();
    if ((IdentityKind)kind != IdentityKind.Unknown)
      timeline.SetIdentity("Mojo", (IdentityKind)kind, RuleStrength.Manual, "Manual");
    if (owned)
      timeline.AddAffiliation(AffiliationKind.PetOfPlayer, "Mojo", double.NegativeInfinity,
        double.PositiveInfinity, RuleStrength.Manual, "Manual", "Tester");

    var index = new FightFactIndex(timeline);
    var rows = FightProjection.Build(run.Facts, timeline, index.OnFact);
    Assert.HasCount(1, rows);
    Assert.AreEqual(incoming ? 1L : 0L, index.TankingFactCount);
    Assert.AreEqual(incoming ? 0L : 1L, index.DamageFactCount);
    Assert.AreEqual(incoming ? 500L : 0L, rows[0].DamageByOwner);
    Assert.AreEqual(incoming ? 0L : 500L, rows[0].DamageToOwner);
    Assert.AreEqual(incoming, double.IsNaN(rows[0].BeginDamageTime));
    Assert.AreEqual(!incoming, double.IsNaN(rows[0].BeginTankingTime));
    Assert.AreEqual(incoming ? MeterPanels.Tanking : MeterPanels.Damage,
      MeterActivityIndex.AcceptsPending(fact, timeline, "Mojo", "Mojo", MeterDamageFilter.Current));

    var board = DerivedTotals.ForOverlay(rows, index, run.Facts, fact.TimeS, fact.TimeS + 1);
    var stats = incoming ? board?.TankStats : board?.DamageStats;
    Assert.IsNotNull(stats);
    Assert.AreEqual(500L, stats.RaidStats.Total);
    Assert.IsNull(incoming ? board?.DamageStats : board?.TankStats);
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void ACarriedSpellClaimRetiresWhenTheNameActsAsACombatant(bool casts)
  {
    var facts = new DamageFactTable(16);
    var state = new ClassificationState();
    var effect = facts.InternName("Strangle");
    var mob = facts.InternName("A gnoll");
    facts.AddFact(new DamageFact(0, FixtureTime.BaseL, effect, mob, 123, LabelTypes.Dot,
      DamageFact.FlagAttackerIsSpell, 0, ushort.MaxValue));
    var first = new EntityTimeline();
    ClassificationRules.Apply(facts, first, state: state);
    Assert.AreEqual(IdentityKind.Spell, first.Identity("Strangle"));
    var stamp = first.StateStamp();
    if (casts)
      facts.AddEvidence(new EvidenceFact(1, FixtureTime.BaseL + 1, effect,
        EvidenceFact.EvCast, facts.InternAux("Unknown Future Cast")));
    else
      facts.AddFact(new DamageFact(1, FixtureTime.BaseL + 1, effect, mob, 456, LabelTypes.Melee,
        0, 0, ushort.MaxValue));
    var carried = new EntityTimeline();
    ClassificationRules.Apply(facts, carried, state: state);
    var replay = new EntityTimeline();
    ClassificationRules.Apply(facts, replay);
    Assert.AreNotEqual(IdentityKind.Spell, carried.Identity("Strangle"));
    Assert.AreNotEqual(stamp, carried.StateStamp());
    Assert.AreEqual(replay.StateStamp(), carried.StateStamp());
    state.Reset();
    var reset = new EntityTimeline();
    ClassificationRules.Apply(facts, reset, state: state);
    Assert.AreEqual(replay.StateStamp(), reset.StateStamp());
  }

  [TestMethod]
  public void AnEffectSharingAHealedNamesSpellingCannotVetoTheHealEvidence()
  {
    var lines = new List<string>
    {
      "Firsthealer joined the raid.",
      "Secondhealer joined the raid.",
      "You have taken 100 damage from Strangle by ."
    };
    for (var i = 0; i < 12; i++)
    {
      var caster = i % 2 == 0 ? "Firsthealer" : "Secondhealer";
      lines.Add($"{caster} healed Strangle for 100 hit points by Spiritual Shower Rk. III.");
    }
    var run = Parse(lines.ToArray());
    var timeline = Classify(run);
    Assert.AreEqual(IdentityKind.Player, timeline.IdentityWithSource("Strangle", out var source));
    Assert.AreEqual("R15-healed", source);
  }

  [TestMethod]
  public void ADefenderWithASpellsNameIsACombatantWithoutAnInventedDefenderFlag()
  {
    var run = Parse("A gnoll hits Strangle for 100 points of damage.",
      "A gnoll has taken 500 damage from Strangle by .");
    var timeline = Classify(run);
    Assert.AreNotEqual(IdentityKind.Spell, timeline.Identity("Strangle"));
    Assert.AreEqual("Strangle", run.Facts.NameOf(run.Facts.Facts[0].DefIdx));
  }

  [TestMethod]
  public void AnUnknownCombatantMatchingASelfTargetSpellDoesNotLoseItsMelee()
  {
    var run = Parse("Cloudburst Strike Feedback XII hits a gnoll for 100 points of damage.");
    var timeline = Classify(run);
    Assert.IsTrue(ClassificationRules.IsSelfTargetDamageSpell("Cloudburst Strike Feedback XII"));
    Assert.IsFalse(run.Facts.Facts[0].AttackerIsSpell);
    var rows = FightProjection.Build(run.Facts, timeline);
    Assert.AreEqual(100L, rows.Sum(r => r.DamageTotal));
  }

  [TestMethod]
  [DataRow("A nameless effect")]
  [DataRow("Rend, Then Tear")]
  [DataRow("Penance")]
  public void CreatureNameHeuristicsDoNotOverrideAnExplicitEffectSlot(string name)
  {
    var run = Parse($"You have taken 123 damage from {name} by .");
    var timeline = Classify(run);
    Assert.AreEqual(IdentityKind.Spell, timeline.Identity(name));
    Assert.IsFalse(timeline.ClaimsOf(name).Any(c => c.Kind == IdentityKind.Npc));
  }

  [TestMethod]
  public void EffectsBeatSeedsButNotMediumCombatantClaims()
  {
    var run = Parse("A gnoll has taken 123 damage from Novel Effect XIX by .");
    foreach (var kind in new[] { IdentityKind.Player, IdentityKind.Pet, IdentityKind.Merc, IdentityKind.Npc })
    {
      var timeline = new EntityTimeline();
      timeline.SetIdentity("Novel Effect XIX", kind, RuleStrength.Medium, "test-evidence");
      ClassificationRules.Apply(run.Facts, timeline);
      Assert.AreEqual(kind, timeline.Identity("Novel Effect XIX"));
    }
    var seeded = new EntityTimeline();
    seeded.SetIdentity("Novel Effect XIX", IdentityKind.Player, RuleStrength.Weak, "RegistrySeed");
    ClassificationRules.Apply(run.Facts, seeded);
    Assert.AreEqual(IdentityKind.Spell, seeded.Identity("Novel Effect XIX"));
  }

  private static EntityTimeline Classify(PipelineHarness.DeriveRunResult run)
  {
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
    return timeline;
  }

  private static PipelineHarness.DeriveRunResult Parse(params string[] actions)
  {
    var dir = Directory.CreateTempSubdirectory("spell-role-");
    var path = Path.Combine(dir.FullName, "eqlog_Tester_Test.txt");
    try
    {
      var start = new DateTime(2026, 10, 23, 21, 54, 0);
      File.WriteAllLines(path, actions.Select((s, i) =>
        $"[{start.AddSeconds(i * 20).ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {s}"));
      return PipelineHarness.RunFileDerived(path);
    }
    finally
    {
      dir.Delete(true);
    }
  }
}
