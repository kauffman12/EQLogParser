using EQLogParser;

namespace EQLogParser;

/*
 * The census's answer to "which spell proved this one?". R4 and R20 claim a name from a cast line, but their reason
 * tag is the rule word ("R4-spell"), which tells an auditor nothing about what was actually cast — so the census walks
 * the evidence rows and carries the accepted cast on the row as `ReasonDetail`. The window shows it as a tooltip and
 * no column, because it is a nice-to-know.
 *
 * Three laws, one per test:
 *   - THE NAMED CAST IS ONE THE RULES ACTUALLY ACCEPTED. Walking the evidence rows is not enough — the fixture mixes
 *     ranks the spell DB knows with shapes it does not, and naming a cast that claimed nothing would put a lie in the
 *     tooltip (the same two gates the rules use are the ones applied here).
 *   - THE FIRST ACCEPTED CAST WINS, because that is the claim the timeline resolved to. Ferociousley casts two
 *     class-safe spells forty seconds apart; naming the second would describe a line that proved nothing new.
 *   - ONLY A SPELL VERDICT GETS ONE. A name placed by presence, chat or the roster has no cast behind it, and a prior
 *     (another log's conclusion, reason "Prior:R4-spell") is not this capture's cast either.
 */
[TestClass]
public class CensusCastProofTest
{
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", "rules-fixture.txt");

  // Inline captures, for shapes the shared fixture does not hold (one name casting two different families).
  private static PipelineHarness.DeriveRunResult RunDerive(params string[] lines)
  {
    var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "census-cast-" + Guid.NewGuid().ToString("N")));
    var log = Path.Combine(dir.FullName, "eqlog_Probeone_Eqgate.txt");   // filename seeds ConfigUtil.PlayerName
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

  [TestInitialize]
  public void Setup() => PlayerRegistry.Instance.Clear();

  [TestCleanup]
  public void Cleanup() => PlayerRegistry.Instance.Clear();

  /*
   * Cold, like IdentityRulesTest: a fresh timeline fed only by ClassificationRules over the captured evidence, so every
   * verdict here is attributable to a rule and no saved players.txt can place a name first. No overrides and no priors —
   * both are process singletons this census is asked to read empty.
   */
  private static ClassificationReport Census()
  {
    Assert.IsTrue(File.Exists(FixturePath), $"missing fixture: {FixturePath}");

    var run = PipelineHarness.RunFileDerived(FixturePath);
    var timeline = new EntityTimeline();
    var outcome = ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
    CollectionAssert.AreEqual(Array.Empty<string>(), outcome.Conflicts, "fixture target frames must not collide");

    return ClassificationReport.Build(timeline, run.Facts, run.HealFacts,
                                      IdentityOverrideStore.Instance, PlayerRegistry.Instance);
  }

  [TestMethod]
  public void ASpellVerdictNamesTheCastThatEarnedIt()
  {
    var report = Census();

    var bard = report.Find("Chantoya");
    Assert.IsNotNull(bard, "the fixture's Boastful Bellow caster is missing from the census");
    Assert.AreEqual("R4-spell", bard!.Reason);
    Assert.AreEqual("Boastful Bellow XLVII", bard.ReasonDetail,
        "a spell-based verdict has to name the cast the rule accepted, rank and all");

    // R20 proves a PET from a cast line too, and the same detail belongs on its row.
    var pet = report.Find("Snapclaw");
    Assert.IsNotNull(pet);
    Assert.AreEqual("R20-petspell", pet!.Reason);
    Assert.AreEqual("Hobble of Spirits Snare VI", pet.ReasonDetail);
  }

  [TestMethod]
  public void TheFirstAcceptedCastIsTheOneNamed()
  {
    var report = Census();

    /*
     * Ferociousley casts Focused Paragon of Spirit XXXIV (line 35) and Hobble of Spirits VI (line 36) forty seconds
     * apart. Both pass R4's gate, so either string would look right — but the verdict was earned by the first, and a
     * later restatement is what the timeline's own dedupe drops. Naming the last cast would answer a question nobody
     * asked ("what did this name cast most recently?") with the field that says what PROVED it.
     */
    var row = report.Find("Ferociousley");
    Assert.IsNotNull(row);
    Assert.AreEqual("R4-spell", row!.Reason);
    Assert.AreEqual("Focused Paragon of Spirit XXXIV", row.ReasonDetail);
  }

  /*
   * The cast that PROVED it is the one whose own gates were passed, not merely any spell the name ever said. The pet
   * form ("Hobble of Spirits Snare VI") and the player form ("Hobble of Spirits VI") share a prefix while being two
   * different spells with two different verdicts, so one flat substring test would let the snare satisfy a PLAYER row —
   * which is what happened first: the tooltip named the pet's spell beside "Player", because the accepted cast was
   * remembered per name instead of per gate. Same for the reverse: a class-safe rank must not be read as the pet claim.
   */
  [TestMethod]
  public void APlayerWhoAlsoCastsThePetFormNamesTheCastThatWon()
  {
    var run = RunDerive(
        // The pet's snare arrives first, and would be the "first accepted cast" under a name-keyed search.
        "[Mon May 04 18:50:05 2026] Boltshelt begins casting Hobble of Spirits Snare VI.",
        "[Mon May 04 18:50:20 2026] Boltshelt begins casting Boastful Bellow XLVII.");

    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
    var report = ClassificationReport.Build(timeline, run.Facts, run.HealFacts,
                                            IdentityOverrideStore.Instance, PlayerRegistry.Instance);

    var row = report.Find("Boltshelt");
    Assert.IsNotNull(row);
    Assert.AreEqual(IdentityKind.Player, row!.Kind);
    Assert.AreEqual("R4-spell", row.Reason);
    Assert.AreEqual("Boastful Bellow XLVII", row.ReasonDetail,
        "a Player row may only name a cast that claims a player; the snare is why this name also has a pet claim");
  }

  [TestMethod]
  public void AVerdictFromAnotherRuleNamesNoSpell()
  {
    var report = Census();

    // Placed by a raid-join line: no cast anywhere behind it, so the tooltip has nothing to say.
    var joined = report.Find("Raidos");
    Assert.IsNotNull(joined);
    Assert.AreEqual("R3-presence", joined!.Reason);
    Assert.IsNull(joined.ReasonDetail, "only spell-based verdicts carry a cast; naming one here would invent it");

    // Placed by the NPC database (article-shaped name, no lines): same answer.
    var npc = report.Find("Grisel Noshikun");
    Assert.IsNotNull(npc);
    Assert.IsFalse(npc!.Reason.StartsWith("R4", StringComparison.Ordinal), "the fixture's NPC must not read as spell-based");
    Assert.IsNull(npc.ReasonDetail);
  }
}
