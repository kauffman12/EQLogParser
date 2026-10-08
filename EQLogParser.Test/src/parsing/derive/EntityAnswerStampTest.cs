namespace EQLogParser;

/*
 * The second question `EntityTimeline` answers: not "did the evidence change" (that is `StateStamp`, pinned by
 * `EntityTimelineDigestTest`) but "would a BOARD route a fact differently". Only the first one existed while the fight list used it to
 * decide whether content had moved under a selection, and the difference between them turned out to be a multi-second rebuild nobody
 * asked for: field report of 2026-10-08 (`EQLogParser.log`) shows select-all → boards built → cleared and rebuilt ~9 s later over
 * byte-identical inputs (708 rows both times, the same 2,647,774 heals materialized both times). The mechanism is that every Full pass
 * rebuilds its timeline from scratch and seeds it from this application's own memory — and the FIRST pass is what writes that memory. So
 * the second pass records claims the first never held: same kind, different `Prior:`-style source, and an evidence digest sees tuples.
 *
 * `AnswerStamp()` folds the same insertions without provenance — kind, effective time, interval bounds, owner; never `strength`, never
 * `source`. It is deliberately conservative in the direction that costs a rebuild rather than one stale figure: an extra claim of the same
 * kind on a different span still moves it even though that claim might never win. Provenance still reaches the surfaces that display it —
 * the identity pane rebuilds from every pass regardless of this stamp.
 */
[TestClass]
public class EntityAnswerStampTest
{
  private const double T0 = FixtureTime.Base;

  private static void Charming(EntityTimeline t, string mob, string charmer, double start,
    double end = double.PositiveInfinity, int strength = RuleStrength.Strong, string source = "R9-charm")
    => t.AddAffiliation(AffiliationKind.PetOfPlayer, mob, start, end, strength, source, charmer);

  /*
   * The phantom rebuild, in four lines. A pass that re-remembers a conclusion under the ledger's own spelling of the rule name adds
   * evidence and no answer: the evidence stamp moves (correctly — something WAS recorded) and the answer stamp does not (also correctly —
   * every board question reads the same word). This is the pair the fight list now compares.
   */
  [TestMethod]
  public void ReRecordingAConclusionUnderAnotherRuleMovesTheAnswerStampNotAtAll()
  {
    var t = new EntityTimeline();
    t.SetIdentity("Grul", IdentityKind.Npc, RuleStrength.Medium, "R14-article");

    var evidenceBefore = t.StateStamp();
    var answerBefore = t.AnswerStamp();

    t.SetIdentity("Grul", IdentityKind.Npc, RuleStrength.Weak, "Prior:R14-article");

    Assert.AreNotEqual(evidenceBefore, t.StateStamp(), "the evidence really did change — a new claim is recorded");
    Assert.AreEqual(answerBefore, t.AnswerStamp(),
      "no board question reads differently: same name, same kind, same effective time; only provenance moved");
  }

  [TestMethod]
  public void AnAnswerThatDiffersMovesBothStamps()
  {
    var t = new EntityTimeline();
    t.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");

    var answerBefore = t.AnswerStamp();
    t.SetIdentity("Zomm", IdentityKind.Npc, RuleStrength.Certain, "R1-target");

    Assert.AreNotEqual(t.StateStamp(), answerBefore);
    Assert.AreNotEqual(answerBefore, t.AnswerStamp(),
      "the kind a board routes on changed, so the answer stamp must not be able to hide it");
  }

  /*
   * A weaker extra claim that happens to lose everywhere still moves the ANSWER stamp. That is on purpose: this value ignores provenance,
   * not time. Reading a span it cannot see would need a walk over every fact's second (measured ~250 ms for 2,436 names, more than the fold
   * it would guard), so a claim on a new span is treated as possibly-winning rather than checked — one rebuild is the cheaper mistake.
   */
  [TestMethod]
  public void AWeakerClaimOnANewSpanStillMovesTheAnswerStamp()
  {
    var t = new EntityTimeline();
    t.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");
    var answerBefore = t.AnswerStamp();

    t.SetIdentity("Zomm", IdentityKind.Pet, RuleStrength.Weak, "RegistrySeed", T0 + 60);   // a seed-grade claim, as the ledger would hand it over

    Assert.AreNotEqual(answerBefore, t.AnswerStamp(), "a different span is not provenance, even when the stronger claim outranks it");
  }

  [TestMethod]
  public void ACharmWindowMovesTheAnswerStampBecauseItsDamageFoldsUnderTheCharmer()
  {
    var t = new EntityTimeline();
    t.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");
    var answerBefore = t.AnswerStamp();

    Charming(t, "an imbued whipgrass", "Zomm", T0 + 30);

    Assert.AreNotEqual(answerBefore, t.AnswerStamp(),
      "an owned window changes whose row the mob's damage lands on; that is an answer, not a footnote");
  }

  [TestMethod]
  public void TheSameWindowReRecordedUnderAnotherSourceLeavesTheAnswerStamp()
  {
    var t = new EntityTimeline();
    Charming(t, "an imbued whipgrass", "Zomm", T0 + 30);

    var evidenceBefore = t.StateStamp();
    var answerBefore = t.AnswerStamp();

    Charming(t, "an imbued whipgrass", "Zomm", T0 + 30, strength: RuleStrength.Certain, source: "Prior:R9-charm");

    Assert.AreNotEqual(evidenceBefore, t.StateStamp(), "the ledger's spelling of the rule is new evidence");
    Assert.AreEqual(answerBefore, t.AnswerStamp(), "and the same owner over the same seconds is the same routing");
  }

  /*
   * The other side of ignoring provenance: an interval's BOUNDS are answers. A remembered window that reaches further folds more facts under
   * its owner, so it has to move the stamp even when the pair (mob, charmer) already existed — this is also what refuses an implementation that
   * hashed only (name, kind, owner).
   */
  [TestMethod]
  public void AWindowThatReachesFurtherMovesTheAnswerStamp()
  {
    var t = new EntityTimeline();
    Charming(t, "an imbued whipgrass", "Zomm", T0 + 30, end: T0 + 90);
    var answerBefore = t.AnswerStamp();

    Charming(t, "an imbued whipgrass", "Zomm", T0 + 30, end: T0 + 120);

    Assert.AreNotEqual(answerBefore, t.AnswerStamp(), "seconds that became somebody's pet are seconds of damage that moved rows");
  }

  [TestMethod]
  public void AnotherOwnerOverTheSameSecondsMovesTheAnswerStamp()
  {
    var t = new EntityTimeline();
    Charming(t, "an imbued whipgrass", "Zomm", T0 + 30);
    var answerBefore = t.AnswerStamp();

    Charming(t, "an imbued whipgrass", "Romance", T0 + 45, end: T0 + 60);

    Assert.AreNotEqual(answerBefore, t.AnswerStamp(), "whose pet it is belongs to the answer, not to the provenance");
  }

  /*
   * Both digests must be reproducible: two identical builds of a timeline produce both stamps unchanged, or every live refresh pays for a
   * rebuild it did not need — which is the property `EntityTimelineDigestTest` holds for the evidence stamp and this pins for the other one.
   */
  [TestMethod]
  public void TwoBuildsOverTheSameEvidenceStampBothValuesIdentically()
  {
    static (long Evidence, long Answer) Build()
    {
      var t = new EntityTimeline();
      t.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");
      t.SetIdentity("Grul", IdentityKind.Npc, RuleStrength.Medium, "R14-article");
      Charming(t, "an imbued whipgrass", "Zomm", T0 + 30);
      return (t.StateStamp(), t.AnswerStamp());
    }

    var first = Build();
    var second = Build();

    Assert.AreEqual(first.Evidence, second.Evidence);
    Assert.AreEqual(first.Answer, second.Answer);

    // And the two values answer different questions: equal-to-equal is fine, but one digest must not be a copy of the other's arithmetic.
    Assert.AreNotEqual(first.Evidence, first.Answer, "if these ever print the same number, one of them stopped being a separate question");
  }

  /*
   * The near-miss this file exists to remember: while adding the answer digest, the affiliation term for the EVIDENCE digest was rewritten
   * without its `owner` argument — which would have made an ownership change invisible to the incremental projection carry (a stale row with
   * plausible damage, exactly the failure that hurts). Both stores are asserted to see owners from both directions here.
   */
  [TestMethod]
  public void BothDigestsSeeTheOwnerOfAnInterval()
  {
    var withOwner = new EntityTimeline();
    Charming(withOwner, "an imbued whipgrass", "Zomm", T0 + 30);

    var noOwner = new EntityTimeline();
    noOwner.AddAffiliation(AffiliationKind.PetOfPlayer, "an imbued whipgrass", T0 + 30, double.PositiveInfinity,
      RuleStrength.Strong, "R9-charm", null);

    Assert.AreNotEqual(withOwner.StateStamp(), noOwner.StateStamp(), "the evidence digest reads owners");
    Assert.AreNotEqual(withOwner.AnswerStamp(), noOwner.AnswerStamp(), "and so does the answer digest");
  }
}
