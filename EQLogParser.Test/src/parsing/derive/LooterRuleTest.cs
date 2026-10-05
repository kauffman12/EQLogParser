using EQLogParser;

namespace EQLogParser;

/*
 * R23: a loot line names the person who took the item. "--Korlos has looted a Muramite Sleeve Armor.--", "wins roll",
 * corruption and currency splits — five parser branches that used to end in `AddVerifiedPlayer`, i.e. that filled a store
 * the classification rules never consult. They publish `EvidenceFact.EvLooter` now, so the claim enters the same verdict
 * chain as every other piece of evidence and carries the second it happened (docs/DesignNotes.md → "The checks that
 * decide who is a player").
 *
 * The census before the rule, because a claim about who is a person needs one: over the live corpus the shape is CLEAN and
 * SMALL — 11 distinct looters on eqlog_Incogitable_xegony.txt and 24 on eqlog_Kizant_xegony-09-20-25.txt, ZERO of them
 * article-shaped or possessive. Clean means it may claim; small is why it claims MEDIUM (30) and not Strong: enough to
 * place a silent raider who never casts where a rule is watching, never enough to outvote what the capture watched that
 * same name do.
 *
 * What is asserted here in both directions: the line reaches a verdict, and the store does NOT get the write any more —
 * one publisher per claim is the law this move exists to establish, and a re-added AddVerifiedPlayer would otherwise be
 * invisible (the window that used it reads the verdict chain now).
 */
[TestClass]
[DoNotParallelize]
public class LooterRuleTest
{
  [TestInitialize]
  public void Setup() => PlayerRegistry.Instance.Clear();

  [TestCleanup]
  public void Cleanup() => PlayerRegistry.Instance.Clear();

  [TestMethod]
  public void ALootLineNamesAPerson()
  {
    var timeline = Apply(Run(
      "[Thu Feb 06 20:00:01 2025] --Korlos has looted a Muramite Sleeve Armor from Velden Dragonbane's corpse.--",
      "[Thu Feb 06 20:00:09 2025] --Luclin has looted a Lesser Engraved Velium Rune.--"));

    /*
     * Kind AND provenance: an unnamed Player would be a row the Names pane cannot explain, which is the whole reason
     * every claim carries its rule code.
     */
    Assert.AreEqual(IdentityKind.Player, timeline.IdentityWithSource("Korlos", out var src),
                    "the loot line named a taker and no rule read it");
    Assert.AreEqual("R23-loot", src);
    Assert.AreEqual(IdentityKind.Player, timeline.Identity("Luclin"));
  }

  [TestMethod]
  public void TheLootLineNoLongerWritesTheStore()
  {
    Run("[Thu Feb 06 20:00:01 2025] --Vexlin has looted a Muramite Gauntlet.--");

    /*
     * One publisher. The claim belongs to the evidence stream now; putting it back into PlayerRegistry would recreate the
     * two-authority shape this retired — where a name could be Player in one pane and merely "remembered" in another.
     */
    Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer("Vexlin"),
                   "the loot branch claimed the store again, beside the evidence claim");
  }

  [TestMethod]
  public void ANameWearingACreatureShapeClaimsNothing()
  {
    /*
     * Measured: no loot line in the corpus names `a something`. If one ever does, the article rule (R14) and the NPC
     * database own that name — a Medium loot claim must not be able to relabel a mob as a raider.
     */
    var timeline = Apply(Run("[Thu Feb 06 20:00:01 2025] --a feral gnoll has looted a Bone Club.--"));

    Assert.AreNotEqual(IdentityKind.Player, timeline.Identity("a feral gnoll"),
                       "the loot claim adopted a name the game marked as a thing");
  }

  // ---- plumbing ----

  private static PipelineHarness.DeriveRunResult Run(params string[] lines)
  {
    var dir = Directory.CreateTempSubdirectory("loot-rule-");
    var log = Path.Combine(dir.FullName, "eqlog_Looter_Test.txt");
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

  private static EntityTimeline Apply(PipelineHarness.DeriveRunResult run)
  {
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
    return timeline;
  }
}
