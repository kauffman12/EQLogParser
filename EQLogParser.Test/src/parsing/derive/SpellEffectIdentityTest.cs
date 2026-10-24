namespace EQLogParser;

// An effect substituted for an unnamed caster is not a fighter. Its damage still reaches the correct board.
// SpellRoleIdentityTest covers collisions with real combatants and the parser's role flags.
[TestClass]
public class SpellEffectIdentityTest
{
  [TestInitialize]
  public void Setup()
  {
    PlayerRegistry.Instance.Clear();

    // An empty override store of its own: this suite never writes a verdict, and pointing at a throwaway server name is how
    // the other census tests keep hand-written rows on disk from leaking into theirs (ClassificationReportTest's idiom).
    IdentityOverrideStore.Instance.Init("Spell Effect Test");
  }

  [TestCleanup]
  public void Cleanup() => PlayerRegistry.Instance.Clear();

  private static PipelineHarness.DeriveRunResult RunOut(params string[] lines)
  {
    var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "spell-effect-" + Guid.NewGuid().ToString("N")));
    var log = Path.Combine(dir.FullName, "eqlog_Spelleffect_Eqgate.txt");   // filename seeds ConfigUtil.PlayerName
    try
    {
      File.WriteAllLines(log, lines);
      return PipelineHarness.RunFileDerived(log);
    }
    finally
    {
      try { Directory.Delete(dir.FullName, true); } catch (IOException) { }
    }
  }

  private static EntityTimeline Apply(PipelineHarness.DeriveRunResult run, out ClassificationReport report)
  {
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
    report = ClassificationReport.Build(timeline, run.Facts, run.HealFacts,
                                        IdentityOverrideStore.Instance, PlayerRegistry.Instance);
    return timeline;
  }

  /*
   * The reported case. The raid's own curse lands on a mob, so the only evidence this name has is "it damages enemies" —
   * which is exactly what makes the graph call an attacker one of ours. R21 runs before the graph and settles it as what it
   * is, so the direction the graph would have taken is refused rather than argued with.
   */
  [TestMethod]
  public void ASpellsDamageOnAMobDoesNotMakeTheSpellAPlayer()
  {
    var run = RunOut(
      "[Mon May 04 18:50:00 2026] A gnoll bites Spelleffect for 100 damage.",
      "[Mon May 04 18:50:02 2026] A gnoll has taken 335500 damage from Curse XVII Rk. III by .",
      "[Mon May 04 18:50:04 2026] A gnoll has taken 290000 damage from Fire Trap Rk. II by .",
      "[Mon May 04 18:50:06 2026] A gnoll has taken 41000 damage from Ball of Fire by .");

    var timeline = Apply(run, out var report);

    foreach (var spell in new[] { "Curse XVII Rk. III", "Fire Trap Rk. II", "Ball of Fire" })
    {
      var kind = timeline.IdentityWithSource(spell, out var source);
      // Spell is the kind this shape earns: not a fighter of either side. It used to be NPC, which put an effect's name in the
      // same column as the gnoll and made the raid's own DoT read like something the raid failed to fight.
      Assert.AreEqual(IdentityKind.Spell, kind, $"{spell} should be a spell name, not a fighter (source {source ?? "none"})");
      Assert.IsTrue(IdentityVocabulary.IsSpellEffect(source), $"a spell effect reads {source}");
      Assert.AreEqual("Spell", IdentityVocabulary.TypeWord(kind),
                      "the Type cell says WHAT it is; the Why cell says how we know, and that word stays 'A Spell'");
      Assert.AreEqual("Spell", IdentityVocabulary.WhyWord(IdentityVocabulary.CodeOf(source)));

      var row = report.Find(spell);
      Assert.IsNotNull(row, $"{spell} is missing from the census");
      Assert.IsFalse(row!.Overrulable, "a spell typed as a Player would put it on the roster — no pencil, no wrong answer");
    }
  }

  /*
   * R21 is a rule about what a NAME is; it may not change what a FACT did. Both endpoints of these lines read NPC-side - the
   * spell because R21 says it is not a person, the mob because a placed name hits a placed mob - and the branch that drops
   * mob-on-mob noise was deleting the raid's own dot damage along with them. Measured on eqlog_Incogitable_xegony.txt: 382
   * facts / 32,932,003 damage in exactly this condition; this fixture, one fact, produced no fight row at all. Legacy counted
   * it (FightManager's spell re-decision), so the fight list and the meter simply read low and nothing said so.
   *
   * The row keys on the TARGET and credits nobody: the line names no caster, and inventing one would hand somebody an evening
   * of damage they never dealt. Labels.Unk is legacy's own word for that column and is asserted rather than eyeballed - a spell
   * name promoted to a damage-dealer row would read as an unidentified raid member on every board in the application.
   */
  [TestMethod]
  public void ASpellsDamageOnAMobStillCountsAsDamageToThatMob()
  {
    var run = RunOut(
      "[Mon May 04 18:50:00 2026] Bithika slams a gnoll for 200 points of damage.",
      "[Mon May 04 18:50:02 2026] A gnoll has taken 335500 damage from Curse XVII Rk. III by .");

    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

    // The index carries the classification like FightProjectionCache does, so the materialized record can ask whether a
    // caster-less line hit one of ours (FightSummarySource.RecordFrom).
    var index = new FightFactIndex(timeline);
    var rows = FightProjection.Build(run.Facts, timeline, index.OnFact);

    var gnoll = rows.SingleOrDefault(r => r.Name == "A gnoll");
    Assert.IsNotNull(gnoll, "damage a line could not attribute still belongs to the mob it landed on");
    Assert.AreEqual(335700L, gnoll!.DamageToOwner, "the dot shares its row with the swing beside it");

    var board = DerivedTotals.For([gnoll], index, run.Facts, new HealFactTable(run.Facts))?.CombinedStats;
    Assert.IsNotNull(board);
    Assert.AreEqual(335700L, (long)board.RaidStats.Total, "the raid total keeps damage the log never attributed");

    var bit = board.StatsList.FirstOrDefault(p => p.Name == "Bithika");
    Assert.IsNotNull(bit, "the one raider this capture names is on the board");
    Assert.AreEqual(200L, (long)bit.Total, "unattributed damage does not move onto the nearest player");

    Assert.IsNull(board.StatsList.FirstOrDefault(p => p.Name == "Curse XVII Rk. III"),
      "a spell must not become a damage-dealer row - legacy writes Labels.Unk for this exact shape");
  }

  /*
   * The other half of the capture, which was already answered correctly and must not move: a spell beating on the raid reads
   * hostile. Provenance now says "a spell" instead of "it attacks the raid", because that is the truer of the two — but the
   * side is unchanged, so a boss dot's damage still lands on the enemy column of every board.
   */
  [TestMethod]
  public void ASpellHittingTheRaidIsStillHostile_AndSaysWhatItIs()
  {
    var run = RunOut(
      "[Mon May 04 18:50:00 2026] A gnoll bites Spelleffect for 100 damage.",
      "[Mon May 04 18:50:02 2026] Spelleffect has taken 335500 damage from Curse XVII Rk. III by .",
      "[Mon May 04 18:50:04 2026] Spelleffect has taken 12000 damage from Sonic Bang by .",
      "[Mon May 04 18:50:06 2026] Spelleffect has taken 900 damage from Gluttering Decay IX by .");

    var timeline = Apply(run, out _);

    Assert.AreEqual(IdentityKind.Spell, timeline.IdentityWithSource("Sonic Bang", out var sonic),
                    "a boss dot beating on the raid is a spell name, and it must not read as one of ours either");
    Assert.AreEqual("R21-spellshape", sonic,
               "the no-caster damage line is the proof, ahead of the spell list and the casting message");
    Assert.AreEqual("Spell", IdentityVocabulary.WhyWord(IdentityVocabulary.CodeOf(sonic)),
                    "the cell has to say this is not a fighter");

    /*
     * The second proof, tested apart from the first: a spell rank this build's spells.txt does not carry. New expansion
     * content arrives silent and is never guessed at (the law R14/R16 follow for names), but the LINE still says "by .", and
     * that shape needs no dictionary — so an unknown spell becomes "Spell" rather than falling through to the graph,
     * where raid damage on a mob would have made it a raid member.
     */
    Assert.IsFalse(ClassificationRules.SpellNamed("Gluttering Decay IX"), "the fixture invents a rank the data does not ship");
    Assert.AreEqual(IdentityKind.Spell, timeline.IdentityWithSource("Gluttering Decay IX", out var unknown),
                    "the line's own shape has to carry the verdict when the dictionary cannot");
    Assert.AreEqual("R21-spellshape", unknown,
               "a spell nobody shipped data for is still recognised from its line shape — no dictionary involved");
  }

  /*
   * The Spell kind claims NEITHER side, and the two predicates that decide that are asserted here because both would have
   * widened a board silently on the day the kind was added. IdentityLookup answers "kind != Unknown → kind == Player", so a
   * spell is never one of ours (the operator's original complaint was caster-less spells reading Player / our side). Worse was
   * EntityTimeline.IsRaidVictimAt, whose exclusion list is `not Npc and not Pet`: an effect's name standing in a DEFENDER slot
   * would have become "one of us being beaten on" and joined the tanking board - which is exactly the silent number-movement
   * every rule on that board is written against. SideAt keeps the NPC arm for Spell too, so nothing this capture already counted
   * moved at all.
   */
  [TestMethod]
  public void ASpellNameBelongsToNeitherSide()
  {
    var run = RunOut(
      "[Mon May 04 18:50:00 2026] A gnoll bites Spelleffect for 100 damage.",
      "[Mon May 04 18:50:02 2026] A gnoll has taken 335500 damage from Curse XVII Rk. III by .",
      "[Mon May 04 18:50:04 2026] Spelleffect has taken 12000 damage from Sonic Bang by .");

    var timeline = Apply(run, out _);

    foreach (var spell in new[] { "Curse XVII Rk. III", "Sonic Bang" })
    {
      Assert.AreEqual(IdentityKind.Spell, timeline.IdentityAt(spell, double.PositiveInfinity), spell);
      Assert.IsFalse(timeline.IsRaidVictimAt(spell, double.PositiveInfinity),
                     $"{spell} standing in a defender slot is not one of us taking damage — the tank board would grow without a sound");
    }

    // The mob keeps its own reading: the spell did not colour what it attacked.
    Assert.AreEqual(IdentityKind.Npc, timeline.IdentityAt("A gnoll", double.PositiveInfinity));
  }

  /*
   * Self-target spell feedback is an EFFECT and is named as one — `You have taken 16690 damage from Cloudburst Strike
   * Feedback XII.` (44 lines on eqlog_Kizant_xegony-2.txt) puts a spell where a caster belongs, so the name reads Spell, never
   * Unknown and never a side. What self-targeting decides is ROUTING: the sentence has no attacker, so its damage earns no fight
   * row and no board credit, which is what the two assertions below hold. IdentityRulesTest pins the same shape from the other
   * side (R7 scores no edge off it).
   */
  [TestMethod]
  public void SelfTargetFeedbackIsNamedAnEffectAndFightsNobody()
  {
    const string feedback = "Cloudburst Strike Feedback XII";
    Assert.IsTrue(ClassificationRules.IsSelfTargetDamageSpell(feedback),
                  "the fixture needs a spell the shipped data targets on its own caster");

    var run = RunOut(
      "[Mon May 04 18:50:00 2026] You have taken 16690 damage from " + feedback + ".",
      "[Mon May 04 18:50:20 2026] You have taken 16690 damage from " + feedback + ".");

    var timeline = Apply(run, out _);

    Assert.AreEqual(IdentityKind.Spell, timeline.IdentityWithSource(feedback, out var source),
                    "the operator's own feedback lost its effect word (source " + (source ?? "none") + ")");
    Assert.AreEqual("R21-spellshape", source);

    var rows = FightProjection.Build(run.Facts, timeline);
    Assert.HasCount(0, rows, "feedback is not an encounter");
    var index = new FightFactIndex(timeline);
    FightProjection.Build(run.Facts, timeline, index.OnFact);
    Assert.AreEqual(0L, index.DamageFactCount + index.TankingFactCount, "feedback credits no board");
  }

  /*
   * A real creature keeps its own verdict when the two proofs collide: R21 runs early, so it must YIELD to anything with
   * better provenance rather than paint every dictionary match NPC at Certain. The fixture's called pet is the sharpest case
   * available without inventing a mob whose name is also a spell — the line that calls it is Certain-class evidence about a
   * creature, and grammar never outranks that.
   */
  [TestMethod]
  public void BetterEvidenceOutranksTheSpellDictionary()
  {
    var path = Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", "rules-fixture.txt");
    Assert.IsTrue(File.Exists(path), $"missing fixture: {path}");

    var run = PipelineHarness.RunFileDerived(path);
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

    // Every presence/chat/who/called claim in the fixture keeps its kind: R21 may only place names nothing else touched.
    Assert.AreEqual(IdentityKind.Player, timeline.IdentityWithSource("Frobum", out var called),
                    "the summoner named by the companion line moved off Player");
    Assert.AreEqual("R5-companion", called);

    foreach (var speaker in new[] { "Chatterbox", "Raiderone" })
    {
      timeline.IdentityWithSource(speaker, out var src);
      Assert.IsFalse(IdentityVocabulary.IsSpellEffect(src), $"{speaker} was relabelled a spell");
    }
  }

  // The spell operand of a cast line supplies no identity for an identically named combatant.
  [TestMethod]
  public void ACastTokenWithNoCombatFactBehindItClaimsNothing()
  {
    const string spell = "Gnawing Void Rk. IV";

    var run = RunOut(
      "[Mon May 04 18:50:00 2026] A gnoll bites Bithika for 100 damage.",
      $"[Mon May 04 18:50:02 2026] Controla begins casting {spell}.");

    var timeline = Apply(run, out _);

    Assert.AreEqual(IdentityKind.Unknown, timeline.Identity(spell),
                    "a name this capture only ever saw being cast is not entered among the fighters");
    timeline.IdentityWithSource(spell, out var source);
    Assert.IsFalse(IdentityVocabulary.IsSpellEffect(source), "the spell branch took a name no combat line used");
  }

  [TestMethod]
  public void ANameSomebodyElseClaimedIsNotRelabelledBecauseACastSaidSo()
  {
    // One string, two roles: a raid member here, somebody else's spell operand there.
    const string bothRoles = "Grimjaw";

    var run = RunOut(
      "[Mon May 04 18:50:00 2026] A gnoll bites Bithika for 100 damage.",
      $"[Mon May 04 18:50:02 2026] {bothRoles} joined the raid.",
      "[Mon May 04 18:50:04 2026] Bithika begins casting " + bothRoles + ".");

    var timeline = Apply(run, out _);

    Assert.AreEqual("R3-joinraid", SourceOf(timeline, bothRoles),
                   $"a cast message spelling the same words overwrote {SourceOf(timeline, bothRoles) ?? "none"}");
    Assert.IsFalse(IdentityVocabulary.IsSpellEffect(SourceOf(timeline, bothRoles)), "the spell branch took a claimed name");
  }

  // A cast operand cannot suppress creature-name rules for a separately observed combatant.
  [TestMethod]
  public void ACastTokenWearingACreatureShapeClaimsNothing()
  {
    const string thing = "A slowly dripping claw";

    // Both roles occur, but the damage record names an ordinary attacker.
    var run = RunOut(
      $"[Mon May 04 18:50:00 2026] {thing} hits Bithika for 500 points of crushing damage. (Critical)",
      "[Mon May 04 18:50:02 2026] Bithika begins casting " + thing + ".",
      "[Mon May 04 18:50:04 2026] Bithika hits " + thing + " for 900 points of piercing damage. (Critical)");

    var timeline = Apply(run, out _);

    Assert.IsFalse(IdentityVocabulary.IsSpellEffect(SourceOf(timeline, thing)),
                   $"the spell branch claimed a name whose own spelling says creature ({SourceOf(timeline, thing) ?? "none"})");
  }

  // The report cannot manufacture a spell identity by joining a cast token to somebody's remembered name.
  [TestMethod]
  public void ACastTokenCannotRelabelARememberedFighter()
  {
    const string spell = "Gnawing Void Rk. IV";
    var savedDir = ConfigUtil.ConfigDir;
    var savedServer = ConfigUtil.ServerName;

    try
    {
      ConfigUtil.ConfigDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "prior-cast-" + Guid.NewGuid().ToString("N"))).FullName;
      ConfigUtil.ServerName = "prior-cast-server";
      var priors = IdentityPriorStore.Instance;
      priors.Init(ConfigUtil.ServerName);

      // Last night's conclusion. R7-graph is on the store's allowlist because a graph verdict has to be re-earned every log —
      // which is the whole point: it is the kind of thing that can be wrong in exactly this direction.
      var lastNight = new EntityTimeline();
      lastNight.SetIdentity(spell, IdentityKind.Player, 60, "R7-graph");
      priors.Record(lastNight, [spell], captureEndS: 1_700_000_000);

      // Tonight the same name is only ever named as what somebody cast.
      var run = RunOut(
        "[Mon May 04 18:50:00 2026] A gnoll bites Bithika for 100 damage.",
        $"[Mon May 04 18:50:02 2026] Controla begins casting {spell}.");
      var timeline = new EntityTimeline();
      ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

      var report = ClassificationReport.Build(timeline, run.Facts, run.HealFacts, null, null, priors);
      var row = report.Find(spell);

      Assert.IsNotNull(row, "a remembered name belongs on the list whether or not this log fought it");
      Assert.AreEqual(IdentityKind.Player, row.Kind);
      Assert.AreEqual("Prior:R7-graph", row.Reason);
      Assert.IsTrue(row.IsPrior, "the report must disclose that this answer is remembered, not parsed");
    }
    finally
    {
      IdentityPriorStore.Instance.Init("prior-cast-cleanup-" + Guid.NewGuid().ToString("N"));
      ConfigUtil.ConfigDir = savedDir;
      ConfigUtil.ServerName = savedServer;
    }
  }

  // Interning a matching combatant name later must not turn a cast token into identity evidence.
  [TestMethod]
  public void ACastTokenThatReachesCombatLaterStillNeedsAParsedSpellRole()
  {
    const string spell = "Slicing Energy";

    var facts = new DamageFactTable(64);
    var heals = new HealFactTable(facts);
    var state = new ClassificationState();
    var seq = 0;

    // Pass one: the cast line only. No effect has appeared in an attacker slot.
    facts.AddEvidence(new EvidenceFact(seq++, 1_700_000_000L, facts.InternName("Spelleffect"), EvidenceFact.EvCast, facts.InternAux(spell)));

    var first = new EntityTimeline();
    RegistrySeed.Apply(first, facts, double.NaN, double.NaN);
    var outcome = ClassificationRules.Apply(facts, first, heals, state);
    Assert.IsTrue(outcome.FailedRules.Count == 0, "a rule stage threw: " + string.Join(", ", outcome.FailedRules));
    Assert.AreEqual(IdentityKind.Unknown, first.IdentityAt(spell, double.PositiveInfinity),
      "a spell merely cast must not become a row before the capture fights it");

    // Pass two, carried: the same capture continues and the spell lands in an attacker slot. Fresh store per pass - that is
    // the contract - with the SAME state, which is what makes this pass a carried one rather than a from-zero replay.
    facts.AddFact(new DamageFact(seq++, FixtureTime.BaseL + 10, facts.InternName(spell), facts.InternName("A gnoll"),
                                 total: 18_724, typeId: 1, flags: 0, modMask: 0, subIdx: ushort.MaxValue));

    var carried = new EntityTimeline();
    RegistrySeed.Apply(carried, facts, double.NaN, double.NaN);
    var carriedOutcome = ClassificationRules.Apply(facts, carried, heals, state);
    Assert.IsTrue(carriedOutcome.FailedRules.Count == 0, "a rule stage threw: " + string.Join(", ", carriedOutcome.FailedRules));

    Assert.AreEqual(IdentityKind.Unknown, carried.IdentityAt(spell, double.PositiveInfinity),
      "an unflagged combatant cannot become a spell merely because somebody cast the same word");

    // And the from-zero reference still agrees - the carried answer is the same verdict on the same source.
    var replay = new EntityTimeline();
    RegistrySeed.Apply(replay, facts, double.NaN, double.NaN);
    var replayOutcome = ClassificationRules.Apply(facts, replay, heals);
    Assert.IsTrue(replayOutcome.FailedRules.Count == 0, "replay stage threw: " + string.Join(", ", replayOutcome.FailedRules));
    Assert.AreEqual(IdentityKind.Unknown, replay.IdentityAt(spell, double.PositiveInfinity));
  }

  private static string? SourceOf(EntityTimeline timeline, string name)
  {
    timeline.IdentityWithSource(name, out var source);
    return source;
  }
}
