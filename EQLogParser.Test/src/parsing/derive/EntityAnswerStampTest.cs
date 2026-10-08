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

  /*
   * A window remembered by another rule, where "another rule" is NOT a charm sighting: an ownership interval written as a static
   * statement of allegiance. Same owner, same seconds, same kind — every board question reads the same word, so the answer stamp holds
   * while the evidence digest moves. (The charm case is the exception and lives in `ALedgerReplayOfACharmClaimMovesTheAnswerStamp`: three
   * predicates recognize a charm by its `R9-charm` prefix, so the ledger's spelling of one changes what they answer.)
   */
  [TestMethod]
  public void TheSameWindowReRecordedUnderANonCharmSourceLeavesTheAnswerStamp()
  {
    var t = new EntityTimeline();
    t.AddAffiliation(AffiliationKind.PetOfPlayer, "an imbued whipgrass", T0 + 30, double.PositiveInfinity,
                     RuleStrength.Strong, "R18-healed-pet", "Zomm");

    var evidenceBefore = t.StateStamp();
    var answerBefore = t.AnswerStamp();

    t.AddAffiliation(AffiliationKind.PetOfPlayer, "an imbued whipgrass", T0 + 30, double.PositiveInfinity,
                     RuleStrength.Certain, "Prior:R18-healed-pet", "Zomm");

    Assert.AreNotEqual(evidenceBefore, t.StateStamp(), "the ledger's spelling of the rule is new evidence");
    Assert.AreEqual(answerBefore, t.AnswerStamp(), "same kind, seconds and owner over a non-charm claim is the same routing");
  }

  /*
   * The hole this file exists to close. `IdentityPriorStore.RememberedRules` carries `"R9-"`, so a later pass replays a remembered charm as
   * `Prior:R9-charm` — and `IsCharmedAt`, `CharmStartAfter`, `IsConfirmedRaidPersonAt` and `HasIndependentIdentity` all test
   * `Source.StartsWith("R9-charm")`, so the replayed one answers FALSE. Same owner, same seconds, same kind as the claim already on file: a
   * digest that folded only kind/bounds/owner would call this "the same answer" and let the boards keep routing the mob's damage into a
   * hidden pet row after the rules stopped calling it a charm. The first assertion is the answer changing; the second ties that change to a
   * predicate, so this test cannot quietly become a hash-diff exercise.
   */
  [TestMethod]
  public void ALedgerReplayOfACharmClaimMovesTheAnswerStamp()
  {
    var t = new EntityTimeline();

    // What R9 actually writes: the target as an NPC at charm strength, plus the Friendly window that flips its side (CharmWindows.Apply).
    t.SetIdentity("an imbued whipgrass", IdentityKind.Npc, RuleStrength.Certain, "R9-charm");
    t.AddAffiliation(AffiliationKind.Friendly, "an imbued whipgrass", T0 + 30, T0 + 90, RuleStrength.Certain, "R9-charm", "Zomm");

    Assert.IsTrue(t.IsCharmedAt("an imbued whipgrass", T0 + 40), "setup: inside the window this name is ours (the side flip)");
    Assert.IsFalse(t.HasIndependentIdentity("an imbued whipgrass", IdentityKind.Npc, double.PositiveInfinity),
      "setup: its only NPC reason IS the charm line — the projection's hidden-pet case");

    var answerBefore = t.AnswerStamp();

    // The ledger's spelling of the same rule, replayed by a later pass (`RememberedRules` carries "R9-").
    t.SetIdentity("an imbued whipgrass", IdentityKind.Npc, RuleStrength.Strong, "Prior:R9-charm");

    Assert.AreNotEqual(answerBefore, t.AnswerStamp(),
      "a non-charm NPC reason for the same name is a routing answer (hidden pet row → listed mob row), not provenance bookkeeping");
    Assert.IsTrue(t.HasIndependentIdentity("an imbued whipgrass", IdentityKind.Npc, double.PositiveInfinity),
      "and that is what the predicate now answers, which is why the stamp had to move");

    // Same law on the affiliation store: a Friendly interval these predicates will not call a charm.
    var windowAnswerBefore = t.AnswerStamp();
    t.AddAffiliation(AffiliationKind.Friendly, "an imbued whipgrass", T0 + 200, T0 + 260, RuleStrength.Certain, "Prior:R9-charm", "Zomm");

    Assert.AreNotEqual(windowAnswerBefore, t.AnswerStamp(),
      "allegiance that is not a charm flip is a different answer to SideAt/CharmStartAfter, over the same owner and bounds");
  }

  /*
   * The identity-store half of the same law: a rule other than R9 giving the SAME kind at the SAME effective time. `IdentityAt` still
   * answers Npc — nothing about the winning word changed — while `HasIndependentIdentity` flips, and that predicate is what decides whether
   * the name's damage folds under a charmer or keys its own row.
   */
  [TestMethod]
  public void ASecondNonCharmClaimOfTheSameKindMovesTheAnswerStamp()
  {
    var t = new EntityTimeline();
    t.SetIdentity("Fenken", IdentityKind.Npc, RuleStrength.Certain, "R9-charm");

    Assert.IsFalse(t.HasIndependentIdentity("Fenken", IdentityKind.Npc, double.PositiveInfinity));
    var answerBefore = t.AnswerStamp();

    t.SetIdentity("Fenken", IdentityKind.Npc, RuleStrength.Weak, "R14-article");

    Assert.AreEqual(IdentityKind.Npc, t.IdentityAt("Fenken", double.PositiveInfinity), "the winning word never changed");
    Assert.AreNotEqual(answerBefore, t.AnswerStamp(), "but the projection's charm decision did");
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
/*
 * HOLE 1, and the reason a fold over stored tuples is not a fold over answers: the promoted claim's (name, kind, time) tuple was
 * ALREADY in the digest, so every bit the first version read stayed equal while `IdentityAt` changed its mind - the projection
 * would have carried rows across a verdict flip, which is the exact failure the answer stamp exists to prevent. Folding strength
 * generally is not the fix either (the memory lane re-records conclusions at Weak, and that must stay invisible);
 * what fixes it is folding the WINNER.
 */
[TestMethod]
public void AStrengthPromotionThatChangesTheWinnerMovesTheAnswerStamp()
{
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Vex", IdentityKind.Player, RuleStrength.Weak, "R15-healed");
    timeline.SetIdentity("Vex", IdentityKind.Npc, RuleStrength.Medium, "R1-targeted");
    Assert.AreEqual(IdentityKind.Npc, timeline.IdentityAt("Vex", 100), "Medium wins today");

    var before = timeline.AnswerStamp();
    timeline.SetIdentity("Vex", IdentityKind.Player, RuleStrength.Certain, "R2-who");

    Assert.AreEqual(IdentityKind.Player, timeline.IdentityAt("Vex", 100), "/who outranks the heal tally now");
    Assert.AreNotEqual(before, timeline.AnswerStamp(),
        "the answer moved, so every fact the projection routes by this name could route differently");
}

/*
 * HOLE 2: among equal strength at equal time, ARRIVAL ORDER decides, so these two timelines genuinely answer different questions.
 * The digest must not call them the same. This deliberately reverses the first version's "arrival order never moves it" law:
 * that was true only because the fold could not see precedence at all, which is the bug rather than the guarantee.
 */
[TestMethod]
public void TwoConflictingClaimsAtEqualStrengthMoveTheStampWhicheverOrderTheyArrive()
{
    var playerFirst = new EntityTimeline();
    playerFirst.SetIdentity("Vex", IdentityKind.Player, RuleStrength.Certain, "R2-who");
    playerFirst.SetIdentity("Vex", IdentityKind.Npc, RuleStrength.Certain, "R1-targeted");

    var npcFirst = new EntityTimeline();
    npcFirst.SetIdentity("Vex", IdentityKind.Npc, RuleStrength.Certain, "R1-targeted");
    npcFirst.SetIdentity("Vex", IdentityKind.Player, RuleStrength.Certain, "R2-who");

    Assert.AreNotEqual(playerFirst.IdentityAt("Vex", 100), npcFirst.IdentityAt("Vex", 100),
        "the tie is decided by who arrived last - that is how `IdentityAt` resolves it");
    Assert.AreNotEqual(playerFirst.AnswerStamp(), npcFirst.AnswerStamp(),
        "and a reuse gate must see the difference, not assume the two states are one");
}

/*
 * A reader that is not the winner: `IsOurPetAt` is an EXISTS over ownership intervals and ignores strength, so a pet claim
 * buried under a stronger charm window changes it while `AffiliationAt` answers the same. The fight list hides a row on
 * ownership, so a fold of the winner would keep drawing the old row shape.
 */
[TestMethod]
public void APetIntervalHiddenUnderACharmWindowStillMovesTheAnswerStamp()
{
    var timeline = new EntityTimeline();
    timeline.AddAffiliation(AffiliationKind.Friendly, "A whipgrass", 10, 20, RuleStrength.Certain, "R9-charm");
    var before = timeline.AnswerStamp();

    timeline.AddAffiliation(AffiliationKind.PetOfPlayer, "A whipgrass", 12, 14, RuleStrength.Strong, "R5-owner", "Frostmaw");

    Assert.AreEqual(AffiliationKind.Friendly, timeline.AffiliationAt("A whipgrass", 13, out _), "the charm still wins the interval table");
    Assert.IsTrue(timeline.IsOurPetAt("A whipgrass", 13), "while ownership went from no to yes");
    Assert.AreEqual("Frostmaw", timeline.OwnerOf("A whipgrass", 13));
    Assert.AreNotEqual(before, timeline.AnswerStamp(), "an answer a winner-only fold cannot see");
}

/*
 * Same for `CharmStartAfter`, which answers "did the raid take this mob off the enemy list after this moment" - the question that
 * ENDS an encounter row. A second window later in the capture changes that answer even where every interval's winner is unchanged.
 */
[TestMethod]
public void ASecondCharmWindowLaterInTheCaptureMovesTheAnswerStamp()
{
    var timeline = new EntityTimeline();
    timeline.AddAffiliation(AffiliationKind.Friendly, "A whipgrass", 10, 20, RuleStrength.Certain, "R9-charm");
    var before = timeline.AnswerStamp();

    timeline.AddAffiliation(AffiliationKind.Friendly, "A whipgrass", 40, 50, RuleStrength.Certain, "R9-charm");

    Assert.AreEqual(40, timeline.CharmStartAfter("A whipgrass", 30), "a second encounter the raid ended by charming");
    Assert.AreNotEqual(before, timeline.AnswerStamp());
}

/*
 * The direction that must NOT be lost with all this: an interval that changes no answer moves nothing. Folding every boundary
 * instead of every transition fails this - the extra probes are not new information. (The identity-side twin of this law is
 * `ReRecordingAConclusionUnderAnotherRuleMovesTheAnswerStampNotAtAll`.)
 */
[TestMethod]
public void AWeakerDuplicateIntervalChangesNoAnswerAndMovesNothing()
{
    var timeline = new EntityTimeline();
    timeline.AddAffiliation(AffiliationKind.Friendly, "A whipgrass", 10, 20, RuleStrength.Certain, "R9-charm");
    var before = timeline.AnswerStamp();

    timeline.AddAffiliation(AffiliationKind.Friendly, "A whipgrass", 12, 18, RuleStrength.Weak, "Prior:R9-charm-ledger");

    Assert.AreEqual(before, timeline.AnswerStamp(),
        "a weaker same-kind claim inside an existing window answers nothing differently, so a settle pass stays free");
}
}
