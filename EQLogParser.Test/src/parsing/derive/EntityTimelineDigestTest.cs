using System.Reflection;
using EQLogParser;

namespace EQLogParser;

/*
 * EntityTimeline.StateStamp is the hinge of the incremental derive pass: FightProjectionCache carries rows and damage
 * index from one pass to the next ONLY while this number is unchanged, on the argument that the projection's every
 * question (IdentityAt, IsCharmedAt, IsOurPetAt, CharmStartAfter, HasIndependentIdentity, OwnerOf) reads the timeline's
 * two stores and nothing else. Get this wrong in the direction of "unchanged" and a stale row survives a verdict that
 * should have moved it — on screen, with plausible numbers and no error anywhere. Get it wrong toward "changed" and the
 * cost is one extra full pass. So every test here pushes the stamp the safe way, and the last one refuses a THIRD store
 * appearing without this digest being told about it.
 *
 * The stamp is INCREMENTAL — each accepted insertion adds a term to a running value — because walking 2,436 names per pass
 * measured ~250 ms on Incogitable, more than the fold it was guarding. Two properties follow and both are pinned below:
 * replaying the same evidence must not move it (that is what makes a live refresh cheap), and any verdict that differs in
 * content must (that is what keeps a stale row off the screen).
 */
[TestClass]
public class EntityTimelineDigestTest
{
    private const double T0 = FixtureTime.Base;

    private static void Charming(EntityTimeline t, string mob, string charmer, double start, double end = double.PositiveInfinity)
      => t.AddAffiliation(AffiliationKind.PetOfPlayer, mob, start, end, RuleStrength.Strong, "R9-charm", charmer);

    [TestMethod]
    public void TwoBuildsOverTheSameVerdictsStampIdentically()
    {
        static long Build()
        {
            var t = new EntityTimeline();
            t.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");
            t.SetIdentity("Grul", IdentityKind.Npc, RuleStrength.Medium, "R14-article");
            Charming(t, "an imbued whipgrass", "Zomm", T0 + 30);
            return t.StateStamp();
        }

        Assert.AreEqual(Build(), Build());

        // …and that an empty timeline is not the same answer as one holding a name.
        Assert.AreNotEqual(new EntityTimeline().StateStamp(), Build());
    }

    [TestMethod]
    public void EveryVerdictThatArrivesMovesTheStamp()
    {
        var baseline = new EntityTimeline();
        baseline.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");
        var stamp = baseline.StateStamp();

        // A new name.
        var t = new EntityTimeline();
        t.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");
        t.SetIdentity("Grul", IdentityKind.Npc, RuleStrength.Medium, "R14-article");
        Assert.AreNotEqual(stamp, t.StateStamp(), "a name nobody had an opinion about before is a new answer");

        // A name strengthened — same verdict, better evidence. R13/R8 upgrade paths do exactly this, and R7's unknown
        // allowance reads the STRENGTH, so rows can move.
        t = new EntityTimeline();
        t.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");
        t.SetIdentity("Grul", IdentityKind.Npc, RuleStrength.Strong, "R13-targeted");
        Assert.AreNotEqual(stamp, t.StateStamp(), "a stronger opinion is a different answer");

        // A charm window opening (an interval, not an identity change — the projection asks IsCharmedAt/OwnerOf).
        t = new EntityTimeline();
        t.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");
        Charming(t, "an imbued whipgrass", "Zomm", T0 + 30);
        Assert.AreNotEqual(stamp, t.StateStamp(), "a window is a verdict the projection reads");

        // A different owner on that same window: OwnerOf is what folds a charmed mob's output under a raider.
        var otherOwner = new EntityTimeline();
        otherOwner.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");
        Charming(otherOwner, "an imbued whipgrass", "Voleep", T0 + 30);
        Assert.AreNotEqual(t.StateStamp(), otherOwner.StateStamp(), "the window belongs to somebody else now");
    }

    [TestMethod]
    public void AWindowClosingOrLosingItsEdgeMovesTheStamp()
    {
        var open = new EntityTimeline();
        Charming(open, "an imbued whipgrass", "Zomm", T0 + 30);          // open-ended: the death IS the close
        var stamp = open.StateStamp();

        // R9-break: a charm-wear line writes T1 = break time. Same name, same window start, materially shorter span —
        // every fact in the reclaimed stretch stops being pet-side credit.
        var broken = new EntityTimeline();
        Charming(broken, "an imbued whipgrass", "Zomm", T0 + 30, T0 + 60);
        Assert.AreNotEqual(stamp, broken.StateStamp(), "a window that stopped being a window is a different answer");

        // The projection asks CharmStartAfter(name, lastHit): a LATER window for the same name has to count too.
        var reopened = new EntityTimeline();
        Charming(reopened, "an imbued whipgrass", "Zomm", T0 + 30, T0 + 60);
        Charming(reopened, "an imbued whipgrass", "Zomm", T0 + 300);
        Assert.AreNotEqual(broken.StateStamp(), reopened.StateStamp(), "a second window is another verdict");
    }

    [TestMethod]
    public void AReplayedLogAndARepairedOneStampDifferently()
    {
        /*
         * R16 (reparse) drops a name's evidence and re-runs the rules; R18 (manual side) is classification INPUT rather
         * than a side-channel. Both therefore land here as different assignments and intervals — which is the shape
         * DeriveEngine produces after RepairAsync: the same facts, a different reading of them.
         */
        var asDerived = new EntityTimeline();
        asDerived.SetIdentity("Grul", IdentityKind.Npc, RuleStrength.Medium, "R14-article");
        Charming(asDerived, "Grul", "Zomm", T0 + 30);

        var afterRepair = new EntityTimeline();
        afterRepair.SetIdentity("Grul", IdentityKind.Npc, RuleStrength.Medium, "R14-article");

        Assert.AreNotEqual(asDerived.StateStamp(), afterRepair.StateStamp(),
            "the night the operator corrected is not the night the facts described");

        // …and putting a name on our side, which is what the override store feeds back into classification.
        var flipped = new EntityTimeline();
        flipped.SetIdentity("Grul", IdentityKind.Npc, RuleStrength.Medium, "R14-article");
        flipped.AddAffiliation(AffiliationKind.Friendly, "Grul", 0, double.PositiveInfinity,
                               RuleStrength.Certain, "R18-override");
        Assert.AreNotEqual(afterRepair.StateStamp(), flipped.StateStamp(),
            "the operator said otherwise, so the rows have to change");
    }

    /*
     * THE property the cheap pass stands on: classification is replayed from scratch every derive, so a night whose
     * evidence has not changed must arrive at the same stamp even though hundreds of SetIdentity/AddAffiliation calls ran
     * again. If dedupe were removed upstream, or a rule started writing something slightly different each run (a
     * DateTime.Now somewhere in a source string), every pass would rebuild and the feature would be silently off while
     * still looking correct.
     */
    [TestMethod]
    public void ReplayingTheSameEvidenceLeavesTheStampAlone()
    {
        var t = new EntityTimeline();
        t.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");
        Charming(t, "an imbued whipgrass", "Zomm", T0 + 30);
        var stamp = t.StateStamp();

        // The whole rule book running a second time over lines it has already seen.
        t.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");
        Charming(t, "an imbued whipgrass", "Zomm", T0 + 30);
        Assert.AreEqual(stamp, t.StateStamp(), "a replay of evidence already recorded is not a new verdict");

        // …and one real arrival past the replay still moves it.
        t.SetIdentity("Grul", IdentityKind.Npc, RuleStrength.Medium, "R14-article");
        Assert.AreNotEqual(stamp, t.StateStamp(), "the pass after a new name has to re-project");
    }

    [TestMethod]
    public void ARebuiltTimelineOverTheSameFactsStampsTheSameEvenInADifferentInsertionOrder()
    {
        /*
         * Insertion order is not a verdict, and here it genuinely varies: RegistrySeed walks PlayerRegistry, whose
         * enumeration order is unspecified and shifts as the registry grows. The stamp therefore sums a per-insertion term
         * (commutative) instead of chaining one hash into the next — a chained digest would rebuild every time the
         * registry happened to reorder, for no change in classification. Content differences still have to show up, which
         * is what EveryVerdictThatArrivesMovesTheStamp holds.
         */
        static long Build(bool reverseNames)
        {
            var t = new EntityTimeline();
            var names = reverseNames ? new[] { "Zomm", "Grul", "Bithika" } : new[] { "Bithika", "Grul", "Zomm" };
            foreach (var n in names) t.SetIdentity(n, IdentityKind.Player, RuleStrength.Certain, "R2-who");
            return t.StateStamp();
        }

        Assert.AreEqual(Build(false), Build(true), "the order names happen to arrive in is not a verdict");
    }

    /*
     * THE GUARD. The digest covers _identity and _affiliation because those are the whole state; a third collection added
     * here (say a per-name cooldown table, or an override map kept separately) would be invisible to StateStamp, and the
     * incremental pass would keep displaying rows projected over the old answer. This test exists so that adding one is a
     * deliberate edit rather than a detail noticed in production: update StateStamp to cover it, then this list.
     */
    /*
     * Same number of verdicts, different verdicts. A stamp built from insertion COUNTS would read "unchanged" here and
     * carry rows across a reclassification; the digest adds a term per insert that hashes WHAT was inserted.
     */
    [TestMethod]
    public void TheSameNumberOfVerdictsCanBeDifferentVerdicts()
    {
        var medium = new EntityTimeline();
        medium.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");
        medium.SetIdentity("Grul", IdentityKind.Npc, RuleStrength.Medium, "R14-article");

        var strong = new EntityTimeline();
        strong.SetIdentity("Zomm", IdentityKind.Player, RuleStrength.Certain, "R2-who");
        strong.SetIdentity("Grul", IdentityKind.Npc, RuleStrength.Strong, "R13-targeted");

        Assert.AreNotEqual(medium.StateStamp(), strong.StateStamp(),
            "two names each way, but the projection reads the STRENGTH, so rows would move");
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

    [TestMethod]
    public void TheTimelineHoldsExactlyTheTwoStoresTheDigestCovers()
    {
        var stores = typeof(EntityTimeline)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(static f => f.DeclaringType == typeof(EntityTimeline) &&
                               (f.FieldType.IsGenericType ? f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                                                          : f.FieldType.Name.StartsWith("Dictionary")))
            .Select(static f => f.Name)
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToList();

        /*
         * The two EVIDENCE stores, plus the two answer-view maps. A view map is not new state to digest: it holds one hashed term
         * per name recomputed from the evidence stores through the public predicates on every accepted insertion, so it cannot lag
         * an answer. Anything else added here (a per-name cooldown table, a separately kept override map) is evidence and has to be
         * folded into StateStamp/AnswerStamp before this list grows, or the incremental pass carries rows over a verdict it cannot see.
         */
        CollectionAssert.AreEqual(new[] { "_affiliation", "_affiliationAnswers", "_identity", "_identityAnswers" }, stores,
            $"EntityTimeline holds {string.Join(", ", stores)}: EntityTimeline.StateStamp digests identity + affiliation " +
            "because that is what the projection reads, and AnswerStamp folds the answers those two hand out. New evidence state " +
            "has to be digested too, or the incremental pass carries rows over a verdict it cannot see");
    }
}
