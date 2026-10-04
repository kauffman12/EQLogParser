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
