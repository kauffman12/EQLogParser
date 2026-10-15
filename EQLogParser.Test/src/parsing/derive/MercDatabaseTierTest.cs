using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * R27-mercdb: what a row in data/mercs.txt buys a name (docs/DesignNotes.md → "The bot table in `data/mercs.txt`").
 *
 * The operator's sentence for this file, stated 2026-11: these are bots that help the player, and being one of them means
 * one thing — the name never becomes a fight. Everything else follows from Merc being player-side already (`IsPlayerSide`,
 * `IsConfirmedRaidPersonAt`), so the rule's whole job is to answer BEFORE the shape rules and before the creature list do.
 *
 * The names are chosen for their shapes, which is exactly where they used to go wrong: `a hired bazu` takes the article
 * R14 reads as the game's own "this is a thing" marker, and `Candlefolk Mender` takes neither article nor owner word, so
 * nothing placed it at all. Both now read Mercenary, and neither carries an NPC claim underneath.
 */
[TestClass]
[DoNotParallelize]
public sealed class MercDatabaseTierTest
{
    // Article-shaped: R14-shape claimed this NPC at Medium before the bot table existed.
    private const string ArticleShaped = "a hired bazu";

    // Title-shaped with no article and no owner word: no rule placed it, so it sat Unknown.
    private const string BareShaped = "Candlefolk Mender";

    /*
     * Invented for this test and checked absent from BOTH shipped files (`grep -ic` answers 0 in each), which is what makes
     * it the control: a real creature name like `a skeleton` is in npcs.txt, so its answer comes from R6 and proves nothing
     * about which rule the bot sweep left alone. This one lands on the article rule, exactly where it landed before.
     */
    private const string NotABot = "a frostmaw sentinel";

    [TestInitialize]
    public void Setup() => PlayerRegistry.Instance.Clear();

    [TestCleanup]
    public void Cleanup() => PlayerRegistry.Instance.Clear();

    // ---- the rung ----

    /*
     * Strong, and that number is an argument, not a rounding. The shipped list knows a STRING and never met the fighter,
     * so it sits with what chat says about a name (R3-merc) rather than above it, and it stays under Certain, which is
     * what a line this capture printed keeps. It clears the shape rules (Medium) and both NPC-list rungs (Medium, Listed)
     * outright, which is the work the rule exists to do.
     */
    [TestMethod]
    public void TheBotTableSitsAboveTheShapeRulesAndBelowALinePrinted()
    {
        PipelineHarness.EnsureDataStore();

        /*
         * Ordered as a collection, because comparing the rule's own constant to `RuleStrength.Strong` by hand is two
         * constants and the analyzer calls that no test at all (MSTEST0032). The rung itself is pinned where it can actually
         * move — read off a derived capture, in AnArticleShapedBotIsOursAndCarriesNoEnemyClaim — and what this list holds is
         * the SHAPE of the ladder: a bot-table hit clears both NPC rungs and stays under line evidence.
         */
        var ladder = new[] { RuleStrength.Weak, RuleStrength.Listed, RuleStrength.Medium,
                             ClassificationRules.MercDatabaseStrength, RuleStrength.Certain };
        CollectionAssert.AreEqual(ladder.OrderBy(x => x).ToList(), ladder.ToList(),
            "a bot-table hit must clear the NPC list and the article rule while staying under line evidence");
        Assert.AreEqual(5, new HashSet<int>(ladder).Count, "no two of these share a rung; a tie settles on write order, not on the rule book");

        // One rung for every shape: this file lists whole spawn names, so a bare entry is not weaker than a spaced one.
        Assert.IsTrue(EQDataStore.Instance.IsKnownMerc(BareShaped), "a title-shaped entry needs no article to earn the rung");
        Assert.IsTrue(EQDataStore.Instance.IsKnownMerc(ArticleShaped));
    }

    // ---- what a hit buys ----

    /*
     * THE POINT. The article is the game's marker for a thing, and R14 is right often enough to stay in the book — so the
     * bot table has to speak first, and the shape rule has to yield rather than leave a second claim underneath. The
     * second half of that assertion is the one that matters: `HasIndependentIdentity` (the fight list's charm/pet door)
     * scans EVERY claim, not just the winning one, so an NPC sentence surviving under a Merc verdict is a row waiting to
     * be routed wrong three rules later.
     */
    [TestMethod]
    public void AnArticleShapedBotIsOursAndCarriesNoEnemyClaim()
    {
        PipelineHarness.EnsureDataStore();
        var timeline = Cold(RunDerive($"[Mon May 04 19:00:05 2026] {ArticleShaped} hits Betebeatz for 300 points of damage."));

        AssertIdentity(timeline, ArticleShaped, IdentityKind.Merc, "R27-mercdb");
        Assert.AreEqual(RuleStrength.Strong, Held(timeline, ArticleShaped));
        Assert.IsFalse(timeline.HasIndependentIdentity(ArticleShaped, IdentityKind.Npc, double.PositiveInfinity),
            "the article rule left its claim under the bot verdict, and the fight list reads every claim");
    }

    /*
     * The other half of the file: a name with no article, which no shape rule ever touched. It used to be Unknown — and
     * an Unknown name that swings at the raid is exactly what R7 scores as "our side" anyway, so this is not a new
     * answer so much as the same answer one stage earlier, with a reason attached.
     */
    [TestMethod]
    public void ANameTheShapeRulesNeverReadIsOursToo()
    {
        PipelineHarness.EnsureDataStore();
        var timeline = Cold(RunDerive($"[Mon May 04 19:00:05 2026] {BareShaped} hits Betebeatz for 300 points of damage."));

        AssertIdentity(timeline, BareShaped, IdentityKind.Merc, "R27-mercdb");
    }

    /*
     * A name NOT on the table keeps whatever the rules already said about it — the sweep claims nothing it cannot point
     * at, and a bot list that widened by association would be worse than no bot list.
     */
    [TestMethod]
    public void ANameNotOnTheTableIsLeftToTheOtherRules()
    {
        PipelineHarness.EnsureDataStore();
        Assert.IsFalse(EQDataStore.Instance.IsKnownMerc(NotABot));

        var timeline = Cold(RunDerive($"[Mon May 04 19:00:05 2026] {NotABot} hits Betebeatz for 300 points of damage."));
        AssertIdentity(timeline, NotABot, IdentityKind.Npc, "R14-shape");
    }

    /*
     * The rung's other half: a list never outranks the capture. One target frame at Certain answers over the file for the
     * rest of the log, which is the exit for a WRONG row in mercs.txt (the other two are the operator's verdict at Manual,
     * and the take-back icon that returns the name to what these files say).
     */
    [TestMethod]
    public void ATargetFrameOutranksTheBotTable()
    {
        PipelineHarness.EnsureDataStore();
        var timeline = Cold(RunDerive(
            $"[Mon May 04 19:00:01 2026] Targeted (Player): {ArticleShaped}",
            $"[Mon May 04 19:00:05 2026] {ArticleShaped} hits Betebeatz for 300 points of damage."));

        AssertIdentity(timeline, ArticleShaped, IdentityKind.Player, "R1-target");
    }

    // ---- the file itself ----

    /*
     * Case-insensitive at every character, for the same reason npcs.txt is: `_allMercs` is keyed OrdinalIgnoreCase and the
     * interned key goes through a CapitalizeFirst cache on the way in, so leading-case-only matching would LOOK like this
     * working. The rows are written lower-case where the client prints an article; `Candlefolk Mender` is spelled as typed.
     */
    [TestMethod]
    public void TheTableIsMatchedWithoutCaseAnywhereInTheName()
    {
        PipelineHarness.EnsureDataStore();

        Assert.IsTrue(EQDataStore.Instance.IsKnownMerc("a hired bazu"));
        Assert.IsTrue(EQDataStore.Instance.IsKnownMerc("A Hired Bazu"), "the interned shape (CapitalizedFirst) must match");
        Assert.IsTrue(EQDataStore.Instance.IsKnownMerc("CANDLEFOLK MENDER"));
        Assert.IsTrue(EQDataStore.Instance.IsKnownMerc("A Clockwork Soother DCCXII"));
    }

    /*
     * The sweep walks `facts.InternedNames`, which is the ONE name pool both fact tables share — so a healer bot that
     * never appears in a damage line is in it exactly as an attacker is. Asserted structurally, because the alternative
     * is trusting that a second table "obviously" shares the pool: it does, by holding the damage table as its own
     * namer (HealFactTable._names).
     */
    [TestMethod]
    public void AHealerThatOnlyAppearsInAHealLineIsInThePoolTheRuleSweeps()
    {
        var facts = new DamageFactTable(16);
        var heals = new HealFactTable(facts);

        const string healer = "a gingerbread soother";
        var idx = heals.InternName(healer);

        CollectionAssert.Contains(facts.InternedNames.ToList(), facts.NameOf(idx),
            "a heal-only name is not in the pool R27 sweeps, so a healing bot would never be claimed");
        Assert.IsTrue(EQDataStore.Instance.IsKnownMerc(facts.NameOf(idx)));
    }

    /*
     * The ledger refuses this claim, and that refusal is a decision rather than an omission: identity-priors.txt exists to
     * remember what a CAPTURE witnessed, and a shipped table restates itself every time the program starts. Same law that
     * keeps R6-npcdb out of it (docs/DesignNotes.md → "A load is not a sighting").
     */
    [TestMethod]
    public void TheLedgerDoesNotRememberATableRestatement()
        => Assert.IsFalse(IdentityPriorStore.WorthRemembering("R27-mercdb"),
            "the bot table reached the memory ledger, so a name would stay a mercenary after the row left the file");

    // ---- helpers ----

    private static EntityTimeline Cold(PipelineHarness.DeriveRunResult run)
    {
        var timeline = new EntityTimeline();
        ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
        return timeline;
    }

    private static int Held(EntityTimeline timeline, string name)
    {
        timeline.IdentityAt(name, double.PositiveInfinity, out var strength, out _);
        return strength;
    }

    private static PipelineHarness.DeriveRunResult RunDerive(params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "derive-merctier-" + Guid.NewGuid().ToString("N")));
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

    private static void AssertIdentity(EntityTimeline timeline, string name, IdentityKind expected, string expectedSource)
    {
        var kind = timeline.IdentityWithSource(name, out var source);
        Assert.AreEqual(expected, kind, $"{name}: expected {expected}, got {kind} ({source ?? "no assignment"})");
        Assert.AreEqual(expectedSource, source, $"{name}: wrong rule got there first");
    }
}
