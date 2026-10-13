using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * R6-npcdb: what a row in data/npcs.txt buys a name, and how far
 * (docs/DesignNotes.md → "The NPC registry in `data/npcs.txt`").
 *
 * The operator's rule for this file, stated 2026-11: "if it's in npcs.txt it's certain an NPC — it just might not
 * be this time". Two halves, and they pull in opposite directions on purpose. A list hit is EVIDENCE, so it does
 * not belong on the same rung as a hint; and a list hit is about a STRING, never about the fighter standing in
 * front of us, so it must not outvote what this capture saw somebody doing.
 *
 * The 36 beta names added with this change are why the rung moved: 19 of them were already Npc from the article in
 * front of the name (R14) and 15 from the client's own target frame (R1), but the bare words — `Era`mi`,
 * `Sorshkah`, `Moruuk` — could only ever read Weak, one rung above nothing. Two of those three are also spelled
 * inside shipped spells.txt flavour text (`Sorshkah slashes and mangles you with tooth and claw.`,
 * `Moruuk's blow rattles you`), which is a second shipped file saying the same thing about the same name.
 */
[TestClass]
[DoNotParallelize]
public sealed class NpcDatabaseTierTest
{
    /*
     * One token, and a token a character could actually be named — which is the whole difficulty: `Alleza` is in
     * npcs.txt AND is four years of logs' proof that players answer to it (RosterMemoryClaimTest, GuildmateRuleTest
     * and SavedPetOwnerClaimTest each borrow it as "the registry knows this name"). The beta bare words this change
     * was written for (`Era`mi`, `Sorshkah`, `Moruuk`) cannot be character names at all — `'` is not legal in one —
     * so they settle the shape question but not the collision question.
     */
    private const string BareWord = "Alleza";

    // Two tokens at least: an EQ character name is one word, so this cannot be anybody's PC.
    private const string SpacedName = "A bramble sentinel";

    // RegistrySeed's own grades are 8 (verified) and 10 ("You"), documented as sitting below the rule tiers so
    // rule output overrides them. The top of that band is what the list has to clear on STRENGTH.
    private const int SeedGrade = 10;

    [TestInitialize]
    public void Setup() => PlayerRegistry.Instance.Clear();

    [TestCleanup]
    public void Cleanup() => PlayerRegistry.Instance.Clear();

    // ---- the ladder ----

    /*
     * The rung itself, pinned so it cannot be "tidied" back into Weak or up into Medium later. Spaced names keep
     * the corroboration rung they have always had (measured, and what R14 defers to); bare words get a rung of
     * their own between a hint and corroboration.
     */
    [TestMethod]
    public void TheListSitsAboveAHintAndBelowCorroboration()
    {
        Assert.AreEqual(RuleStrength.Listed, ClassificationRules.NpcDatabaseStrengthFor("Era`mi"));
        Assert.AreEqual(RuleStrength.Medium, ClassificationRules.NpcDatabaseStrengthFor(SpacedName));

        // Weak < Listed < Medium, strict: a shipped list is more than a hint kept for context, and it stays under
        // the rung that read this capture — at equal strength a tie is settled by write order, which is not a law
        // of the rule book. Read through a helper and compared as a collection, because two constants compared by
        // hand are an analyzer warning rather than a test.
        var ladder = Ladder();
        CollectionAssert.AreEqual(ladder.OrderBy(x => x).ToList(), ladder.ToList(),
            "a list hit must sit between a bare hint and the rung that read this capture, with no equal ties");
    }

    [TestMethod]
    public void ABareWordInTheListReadsNpcWithItsOwnReason()
    {
        var timeline = Cold(RunDerive($"[Mon May 04 19:00:05 2026] {BareWord} hits Betebeatz for 300 points of damage."));

        AssertIdentity(timeline, BareWord, IdentityKind.Npc, "R6-npcdb");
        Assert.AreEqual(RuleStrength.Listed, Held(timeline, BareWord));
    }

    /*
     * THE POINT OF THE LIFT. `RegistrySeed` replays this application's warm memory of its raid at 8-10, and a
     * bare-word list hit used to beat that claim only because it happened to be written second — an equal-strength
     * tie, decided by which stage ran when. Same answer, wrong mechanism: a precedence that depends on write order
     * is one stage reorder away from flipping. Run the two orders and require the same result.
     */
    [TestMethod]
    public void ABareWordListHitBeatsARememberedPlayerOnStrengthNotArrivalOrder()
    {
        var run = RunDerive($"[Mon May 04 19:00:05 2026] {BareWord} hits Betebeatz for 300 points of damage.");

        // Seed first, the way a warm derive pass does it.
        var memoryFirst = new EntityTimeline();
        memoryFirst.SetIdentity(BareWord, IdentityKind.Player, SeedGrade, "RegistrySeed");
        ClassificationRules.Apply(run.Facts, memoryFirst, run.HealFacts);
        Assert.AreEqual(IdentityKind.Npc, memoryFirst.Identity(BareWord));

        // And the same two claims written the other way round.
        var rulesFirst = new EntityTimeline();
        ClassificationRules.Apply(run.Facts, rulesFirst, run.HealFacts);
        rulesFirst.SetIdentity(BareWord, IdentityKind.Player, SeedGrade, "RegistrySeed");
        Assert.AreEqual(IdentityKind.Npc, rulesFirst.Identity(BareWord),
            "the list only beat a remembered raider because it arrived second — the rung is doing no work");
    }

    /*
     * The other half of the sentence: this time may not be that NPC. Everything the evidence stage reads off the
     * lines themselves runs before R6 and keeps its answer — the target frame at Certain, a drink line at Strong.
     * (A WRONG list hit has two other exits too: the operator's own verdict sits at Manual, and one
     * `Targeted (Player):` line on the name outranks it for the rest of the capture.)
     */
    [TestMethod]
    public void ALinedFrameAndADrinkLineOutrankTheList()
    {
        var targeted = Cold(RunDerive(
            $"[Mon May 04 19:00:01 2026] Targeted (Player): {BareWord}",
            $"[Mon May 04 19:00:05 2026] {BareWord} hits Betebeatz for 300 points of damage."));
        AssertIdentity(targeted, BareWord, IdentityKind.Player, "R1-target");

        var drinking = Cold(RunDerive(
            $"[Mon May 04 19:00:06 2026] Glug, glug, glug...  {BareWord} takes a drink from their Water Flask.",
            $"[Mon May 04 19:00:08 2026] {BareWord} hits Betebeatz for 300 points of damage."));
        AssertIdentity(drinking, BareWord, IdentityKind.Player, "R17-selffeed");
    }

    // ---- the file itself ----

    /*
     * A spaced name keeps both halves it has always had: Npc at Medium, and R6 rather than R14 as its reason —
     * the shape rule yields at equal strength so the report says WHY (the list names it) rather than that the name
     * merely looks like a mob.
     */
    [TestMethod]
    public void ASpacedNameKeepsTheCorroborationRungAndItsOwnWord()
    {
        var timeline = Cold(RunDerive($"[Mon May 04 19:00:05 2026] {SpacedName} hits Betebeatz for 300 points of damage."));

        AssertIdentity(timeline, SpacedName, IdentityKind.Npc, "R6-npcdb");
        Assert.AreEqual(RuleStrength.Medium, Held(timeline, SpacedName));
    }

    /*
     * Matching ignores case at every character, not just the first. The rows are written lowercase as a house
     * convention while the client capitalises mid-name (`Vazaelle Kaleine The Devout`), and `EQDataStore` keys
     * `_allNpcs` with StringComparer.OrdinalIgnoreCase — so the case an editor happens to type is never the reason
     * a name goes unplaced. Worth pinning, because the interned key goes through a CapitalizeFirst cache on the way
     * in and that alone would look like leading-case-only matching.
     */
    [TestMethod]
    public void TheListIsMatchedWithoutCaseAnywhereInTheName()
    {
        PipelineHarness.EnsureDataStore();

        Assert.IsTrue(EQDataStore.Instance.IsKnownNpc("Vazaelle Kaleine The Devout"),
            "the beta entry is missing from npcs.txt");
        Assert.IsTrue(EQDataStore.Instance.IsKnownNpc("vazaelle kaleine the devout"),
            "a row written in the file's lowercase convention must still meet the client's capitalisation");
        Assert.IsTrue(EQDataStore.Instance.IsKnownNpc("VAZAELLE KALEINE THE DEVOUT"));
        Assert.IsTrue(EQDataStore.Instance.IsKnownNpc("era`mi"), "inner case is free too, not just the first letter");
    }

    // ---- helpers ----

    private static int[] Ladder() => [RuleStrength.Weak, RuleStrength.Listed, RuleStrength.Medium];

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
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "derive-npctier-" + Guid.NewGuid().ToString("N")));
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
