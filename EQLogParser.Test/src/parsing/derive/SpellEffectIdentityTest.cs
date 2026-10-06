using EQLogParser;

namespace EQLogParser;

/*
 * R21: a name that exists only because a SPELL stood in the attacker field is not a fighter, and must never be listed as a
 * person (ClassificationRules.ApplySpellEffects).
 *
 * The report came from the Names window on a real night's capture: a curse listed as "Player | Our side" next to rows for
 * Burning Glob Burst, Spiter Blood and a pile of chants. Two line shapes put a spell where a fighter's name belongs, both of
 * them the client's own way of writing "damage from a spell, no caster named":
 *
 *   Goratoar has taken 18724 damage from Slicing Energy by .          <- 706 lines in eqlog_Kizant_xegony-09-03-26.txt
 *   A gnoll has taken 108790 damage from your Mind Coil Rk. II.      <- 135 lines in the same file
 *
 * The parser turns the first into an attacker field holding the SPELL (DamageLineParser: attacker == "." becomes the spell,
 * AttackerIsSpell set, and CombatCapture carries it onto the fact). Over the first 250 MB of that capture R21 places **49**
 * such names out of a 227-name pool, and **471** of their facts are aimed at MOBS — the raid's own dots landing on its own
 * targets. That last number is the bug: a name that beats on mobs is, as far as the graph is concerned, one of ours, which is
 * how a curse reached the roster column. The other 717 facts hit the raid, where the graph already answered NPC; those rows
 * keep their side and gain the truth about what they are.
 *
 * All 49 names are also in the shipped spells.txt, so on this corpus the DICTIONARY does the work and the fact flag is the
 * guard for data this build does not carry — which is why both proofs are tested below rather than one.
 *
 * The self-target feedback shape stays in here too even though IdentityRulesTest already refuses it in R7: the guard has to
 * hold on BOTH paths, because "You have taken 16690 damage from Cloudburst Strike Feedback XII." is the operator's own spell
 * bouncing back and naming an enemy in that line would be a fabrication of a different kind.
 */
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
   * Self-target spell feedback stays UNCLASSIFIED — the one spell shape that gets no verdict at all. Calling it NPC is as
   * wrong as calling it Player: the damage is the local player's own spell bouncing back and there is no entity behind the
   * name. R7 refuses those edges (IdentityRulesTest pins that); this pins that R21 does not claim the name either.
   */
  [TestMethod]
  public void SelfTargetFeedbackStillGetsNoVerdict()
  {
    var run = RunOut(
      "[Mon May 04 18:50:00 2026] You have taken 16690 damage from Cloudburst Strike Feedback XII.",
      "[Mon May 04 18:50:20 2026] You have taken 16690 damage from Cloudburst Strike Feedback XII.");

    var timeline = Apply(run, out _);

    Assert.AreEqual(IdentityKind.Unknown, timeline.IdentityWithSource("Cloudburst Strike Feedback XII", out var source),
                    "the operator's own feedback must not be branded an enemy (source " + (source ?? "none") + ")");
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

  /*
   * The second recognizer: the CAST MESSAGE (`X begins casting Y.`). CastLineParser already resolves every cast/sing/activate
   * line into a SpellData (calling AddUnknownSpell when spells.txt has no row) and hands the resolved name to the fact table as
   * the aux of an EvCast evidence row, so the game's own act of naming is what this reads rather than a dictionary this build had
   * to ship — next expansion's ranks are known the day they print.
   *
   * Where it lands: NOT as a timeline verdict for a name no combat line ever used. Rows come from names the capture fought, and
   * eqlog_Kizant_xegony-09-20-25.txt measures 1,044 cast tokens of which ZERO appear as an attacker or defender — claiming them
   * anyway would add ~1,000 rows to a window about fighters and move the derive's state digest every time somebody tries a spell
   * for the first time, buying a full rebuild over verdicts no board reads. What the cast feed does instead is refuse to be
   * fooled by MEMORY, which is where a spell name actually shows up in a log that never fought it: see
   * ARememberedFighterThisLogWatchedBeingCastSaysAspell. A token whose name IS in the fact pool still claims the name here,
   * which is why the recognizer stays a rule and not just a lookup.
   */
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
    // The collision the silence gate exists for: one string, two roles — a raid member here, a spell token there. The alias is
    // one word because the pre-line parsers that read "X joined the raid." take a single-token subject; that is a fixture limit,
    // not the rule's — any claim at any strength from any earlier stage holds the name against this branch.
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

  /*
   * The grammar guard on the token feed. A tokenizer trusts its line, and one capture hands it a thousand tokens (measured:
   * 1,085 on eqlog_Kizant_xegony-09-20-25.txt), so a token wearing a CREATURE's shape claims nothing even when nothing else has
   * spoken: articles and possessives are how fighters are named in this log, and freezing them out with a Strong spell verdict
   * arriving first would leave the behaviour rules (R14's article shape, R5's ownership) unable to answer at equal strength.
   */
  [TestMethod]
  public void ACastTokenWearingACreatureShapeClaimsNothing()
  {
    const string thing = "A slowly dripping claw";

    // The token AND a fact carry the same article-shaped name: grammar says creature, the cast line says spell, and this log
    // has both for one name. The article wins because it is the shape EQ gives fighters — R14 places it as NPC at Medium and
    // keeps its own provenance rather than being frozen out by a Strong spell verdict that got there first.
    var run = RunOut(
      $"[Mon May 04 18:50:00 2026] {thing} hits Bithika for 500 points of crushing damage. (Critical)",
      "[Mon May 04 18:50:02 2026] Bithika begins casting " + thing + ".",
      "[Mon May 04 18:50:04 2026] Bithika hits " + thing + " for 900 points of piercing damage. (Critical)");

    var timeline = Apply(run, out _);

    Assert.IsFalse(IdentityVocabulary.IsSpellEffect(SourceOf(timeline, thing)),
                   $"the spell branch claimed a name whose own spelling says creature ({SourceOf(timeline, thing) ?? "none"})");
  }

  /*
   * Where a spell name really does turn up in a log that never fought it: the ledger. An older build (or a night whose rules were
   * weaker) can remember such a name as one of ours, and memory is consulted precisely when this capture says nothing — which is
   * how a player measured "Asphyxiating Grasp Rk. III" sitting on the list with a remembered Player verdict. Borrowing yesterday's
   * answer is right only while NOTHING fresher speaks, and this capture watched somebody cast the thing: the report answers
   * A Spell, says where that came from, and stops calling it somebody's memory.
   */
  [TestMethod]
  public void ARememberedFighterThisLogWatchedBeingCastSaysAspell()
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
      Assert.AreEqual(IdentityKind.Spell, row!.Kind, "last night's Player beat a cast line printed in this capture");
      Assert.AreEqual("R21-spellcast", row.Reason);
      Assert.IsFalse(row.IsPrior, "the answer is this file's, so the pane must not say 'in previous log'");
      Assert.AreEqual("Spell", IdentityVocabulary.WhyWord(row.Reason));
    }
    finally
    {
      IdentityPriorStore.Instance.Init("prior-cast-cleanup-" + Guid.NewGuid().ToString("N"));
      ConfigUtil.ConfigDir = savedDir;
      ConfigUtil.ServerName = savedServer;
    }
  }

  /*
   * The review's reproduction, shaped as its own fixture: a spell that prints FIRST as a cast token and only later reaches
   * a combat line. The pool gate is a read over the capture's interned names, and the evidence cursor walks each EvCast
   * row exactly once - so without the parked list, pass one's refusal was permanent: the new fact interns its names
   * without moving any verdict, StateStamp never moves, no full rebuild ever happens, and the carried pass answered
   * Unknown while a from-zero replay answered Spell. The fix is the promotion walk in ApplySpellEffects: a parked token is
   * re-asked every pass, and the pool only grows within one capture, so the verdict lands on the first full pass after the
   * name's first fact - the same bounded staleness every other carried verdict lives under.
   */
  [TestMethod]
  public void ACastTokenThatReachesCombatLaterIsClaimedByACarriedPass()
  {
    const string spell = "Slicing Energy";

    var facts = new DamageFactTable(64);
    var heals = new HealFactTable(facts);
    var state = new ClassificationState();
    var seq = 0;

    // Pass one: the cast line only. The token is outside the name pool, so the gate parks it.
    facts.AddEvidence(new EvidenceFact(seq++, 1_700_000_000L, facts.InternName("Spelleffect"), EvidenceFact.EvCast, facts.InternAux(spell)));

    var first = new EntityTimeline();
    RegistrySeed.Apply(first, facts, double.NaN, double.NaN);
    var outcome = ClassificationRules.Apply(facts, first, heals, state);
    Assert.IsTrue(outcome.FailedRules.Count == 0, "a rule stage threw: " + string.Join(", ", outcome.FailedRules));
    Assert.AreEqual(IdentityKind.Unknown, first.IdentityAt(spell, double.PositiveInfinity),
      "a spell merely cast must not become a row before the capture fights it");

    // Pass two, carried: the same capture continues and the spell lands in an attacker slot. Fresh store per pass - that is
    // the contract - with the SAME state, which is what makes this pass a carried one rather than a from-zero replay.
    facts.AddFact(new DamageFact(seq++, 1_700_000_010L, facts.InternName(spell), facts.InternName("A gnoll"),
                                 total: 18_724, typeId: 1, flags: 0, modMask: 0, subIdx: ushort.MaxValue));

    var carried = new EntityTimeline();
    RegistrySeed.Apply(carried, facts, double.NaN, double.NaN);
    var carriedOutcome = ClassificationRules.Apply(facts, carried, heals, state);
    Assert.IsTrue(carriedOutcome.FailedRules.Count == 0, "a rule stage threw: " + string.Join(", ", carriedOutcome.FailedRules));

    Assert.AreEqual(IdentityKind.Spell, carried.IdentityAt(spell, double.PositiveInfinity),
      "the parked token was never re-asked: the carried pass kept the pool-gate refusal from before the name had a row");
    Assert.AreEqual("R21-spellcast", SourceOf(carried, spell));

    // And the from-zero reference still agrees - the carried answer is the same verdict on the same source.
    var replay = new EntityTimeline();
    RegistrySeed.Apply(replay, facts, double.NaN, double.NaN);
    var replayOutcome = ClassificationRules.Apply(facts, replay, heals);
    Assert.IsTrue(replayOutcome.FailedRules.Count == 0, "replay stage threw: " + string.Join(", ", replayOutcome.FailedRules));
    Assert.AreEqual(IdentityKind.Spell, replay.IdentityAt(spell, double.PositiveInfinity));
  }

  private static string? SourceOf(EntityTimeline timeline, string name)
  {
    timeline.IdentityWithSource(name, out var source);
    return source;
  }
}
