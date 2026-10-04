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
      Assert.AreEqual(IdentityKind.Npc, kind, $"{spell} reads as a fighter (source {source ?? "none"})");
      Assert.AreEqual("R21-spelleffect", source, "the verdict has to name the spell, not a generic NPC reason");

      var row = report.Find(spell);
      Assert.IsNotNull(row, $"{spell} is missing from the census");
      Assert.IsFalse(row!.Overrulable, "a spell typed as a Player would put it on the roster — no pencil, no wrong answer");
    }
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

    Assert.AreEqual(IdentityKind.Npc, timeline.IdentityWithSource("Sonic Bang", out var sonic),
                    "a boss dot beating on the raid must not read as one of ours");
    Assert.AreEqual("R21-spelleffect", sonic);
    Assert.AreEqual("A Spell", IdentityVocabulary.WhyWord(IdentityVocabulary.CodeOf(sonic)),
                    "the cell has to say this is not a fighter");

    /*
     * The second proof, tested apart from the first: a spell rank this build's spells.txt does not carry. New expansion
     * content arrives silent and is never guessed at (the law R14/R16 follow for names), but the LINE still says "by .", and
     * that shape needs no dictionary — so an unknown spell becomes "A Spell" rather than falling through to the graph,
     * where raid damage on a mob would have made it a raid member.
     */
    Assert.IsFalse(ClassificationRules.SpellNamed("Gluttering Decay IX"), "the fixture invents a rank the data does not ship");
    Assert.AreEqual(IdentityKind.Npc, timeline.IdentityWithSource("Gluttering Decay IX", out var unknown),
                    "the line's own shape has to carry the verdict when the dictionary cannot");
    Assert.AreEqual("R21-spelleffect", unknown);
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
    Assert.AreEqual(IdentityKind.Pet, timeline.IdentityWithSource("Frobum", out var called), "R5's called pet moved");
    Assert.AreEqual("R5-called", called);

    foreach (var speaker in new[] { "Chatterbox", "Raiderone" })
    {
      timeline.IdentityWithSource(speaker, out var src);
      Assert.AreNotEqual("R21-spelleffect", src, $"{speaker} was relabelled a spell");
    }
  }
}
