using EQLogParser;

namespace EQLogParser.Wpf.Test
{
  /*
   * The answer stamp reaching the surface that spends money on it. `FightTable.SelectionStamp` is what decides whether a derive pass
   * has to rebuild the boards under a selection — and a whole-capture selection re-materializes in seconds (measured in the field:
   * `boards.build 5,233 ms`, with the UI thread 3.7 s late behind it). Field report of 2026-10-08: select all right after a load, the
   * damage summary filled, then cleared and filled again ~9 s later over identical rows — because the stamp's identity term was the
   * EVIDENCE digest, and the pass after a load legitimately holds evidence the first pass wrote into this application's own memory.
   *
   * Same evidence recorded under another rule name is not the same question for the boards, so the term is now `AnswerStamp`. These
   * three laws are the seam: the pane must be blind to provenance, must NOT be blind to an answer, and a test-built snapshot (which
   * carries no stamp at all) must still compare something rather than matching zero to zero.
   */
  [TestClass]
  public sealed class ContentStampFollowsAnswersTest
  {
    private const double T0 = 1_000;

    private static DerivedSnapshot SnapshotOf(EntityTimeline timeline, long factCount = 4_000)
    {
      var fight = new DerivedFight { Name = "an imbued whipgrass", Id = 1, BeginTime = T0, LastTime = T0 + 20 };
      return DerivedFightRows.Build([fight], timeline, factCount, new DamageFactTable(8), new FightFactIndex());
    }

    private static EntityTimeline FirstPass()
    {
      var t = new EntityTimeline();
      t.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");
      t.SetIdentity("an imbued whipgrass", IdentityKind.Npc, RuleStrength.Medium, "R14-article");
      return t;
    }

    /*
     * The phantom rebuild, at the seam. Pass two reseeds from memory pass one wrote, so it records the same two verdicts again under
     * the ledger's spelling of the rule names. That IS new evidence and the old term moved (asserted, so the test cannot pass by
     * nothing having changed) — but no board routes a fact differently, so what the pane compares must stand still.
     */
    [TestMethod]
    public void ASecondPassThatOnlyReRememberedItsOwnConclusionsDoesNotAskForABuild()
    {
      var first = SnapshotOf(FirstPass());

      var secondTimeline = FirstPass();
      secondTimeline.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Strong, "Prior:R2-who");
      secondTimeline.SetIdentity("an imbued whipgrass", IdentityKind.Npc, RuleStrength.Weak, "Prior:R14-article");
      var second = SnapshotOf(secondTimeline);

      Assert.AreNotEqual(first.Timeline!.StateStamp(), second.Timeline.StateStamp(),
        "the evidence really did move — this is the situation that used to cost a full rebuild");
      Assert.AreEqual(FightTable.SelectionStamp(first), FightTable.SelectionStamp(second),
        "and the boards under a selection stay as they are, because every name still reads the same");

      // The stamp is on the snapshot, not recomputed per reader: a pane must be able to compare without walking the timeline.
      Assert.AreEqual(secondTimeline.AnswerStamp(), second.AnswerStamp, "DerivedFightRows stamps the pass it built");
    }

    [TestMethod]
    public void AnAnswerThatChangedAsksForTheBuild()
    {
      var first = SnapshotOf(FirstPass());

      var reclassified = FirstPass();
      reclassified.SetIdentity("an imbued whipgrass", IdentityKind.Pet, RuleStrength.Certain, "R24-petslot");
      var second = SnapshotOf(reclassified);

      Assert.AreNotEqual(FightTable.SelectionStamp(first), FightTable.SelectionStamp(second),
        "a name the boards route the other way now is exactly what the rebuild is for");
    }

    [TestMethod]
    public void ACharmWindowThatAppearedAsksForTheBuild()
    {
      var first = SnapshotOf(FirstPass());

      var charmed = FirstPass();
      charmed.AddAffiliation(AffiliationKind.PetOfPlayer, "an imbued whipgrass", T0 + 5, double.PositiveInfinity,
        RuleStrength.Strong, "R9-charm", "Zomm");
      var second = SnapshotOf(charmed);

      Assert.AreNotEqual(FightTable.SelectionStamp(first), FightTable.SelectionStamp(second),
        "the mob's damage folds under a charmer from this pass on — whose row it lands on is an answer");
    }

    /*
     * The test hatch, pinned so nobody tidies it away. Only `DerivedFightRows.Build` stamps a snapshot; a snapshot assembled by hand
     * carries 0. If 0 were compared as a value, every unstamped pass would match every other and the pane would stop rebuilding — the
     * exact opposite failure, and one no assertion on screen would notice. So an unstamped snapshot falls back to the evidence digest.
     */
    [TestMethod]
    public void AnUnstampedSnapshotComparesTheEvidenceRatherThanZero()
    {
      var unstampedFirst = SnapshotOf(FirstPass());
      unstampedFirst.AnswerStamp = 0;

      var secondTimeline = FirstPass();
      secondTimeline.SetIdentity("an imbued whipgrass", IdentityKind.Pet, RuleStrength.Certain, "R24-petslot");
      var unstampedSecond = SnapshotOf(secondTimeline);
      unstampedSecond.AnswerStamp = 0;

      Assert.AreNotEqual(FightTable.SelectionStamp(unstampedFirst), FightTable.SelectionStamp(unstampedSecond),
        "with no answer stamp to read, the old term still decides — two zeros never compare equal by accident");

      // …and the fallback is only for the hatch: a stamped snapshot of the same pass is a different (answer-based) number.
      Assert.AreNotEqual(FightTable.SelectionStamp(unstampedSecond), SnapshotOf(secondTimeline).AnswerStamp,
        "the two terms answer different questions");
    }

    [TestMethod]
    public void NewFactsStillAskForTheBuildWhateverTheIdentityDoes()
    {
      var first = SnapshotOf(FirstPass(), factCount: 4_000);
      var moreFacts = SnapshotOf(FirstPass(), factCount: 4_001);

      Assert.AreNotEqual(FightTable.SelectionStamp(first), FightTable.SelectionStamp(moreFacts),
        "the facts term is untouched by this change: a capture that grew always rebuilds");
    }
  }
}
