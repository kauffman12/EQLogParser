using EQLogParser;

namespace EQLogParser;

/*
 * The four identity rules that came out of reading six captures (2022-2026) cold, one after another, the way
 * a person would — and the negatives that keep each of them from being smarter than the log.
 *
 *   R5 vocabulary  the game writes ownership in five words, not two: pet, warder, ward, familiar, mount
 *                  (docs/DesignNotes.md → "Breadth of evidence, measured"). "ward" is what the newer logs call a summon;
 *                  a capture from 2025/2026 has ~10x more wards than warders.
 *   R14 name shape an article in front of a name is the client saying "this is a thing": player names never
 *                  take one, custom pet names never take one.
 *   R7 allowance   one unnamed defender used to veto a night of evidence. The guard becomes a share.
 *   R15 heal edge  half the raid keeps topping up an unclassified name, and nothing else in the file says who
 *                  it is (mercs and custom-named pets that never speak, join, own or cast a namable spell).
 *
 * Cold mode throughout: no registry seed, so every verdict below is attributable to one rule.
 */
[TestClass]
[DoNotParallelize]
public class IdentityRuleExtensionsTest
{
    private string? _playerName;

    [TestInitialize]
    public void Setup()
    {
        // Both are process globals the harness writes (the filename carries the operator's name).
        _playerName = ConfigUtil.PlayerName;
        PlayerRegistry.Instance.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
        ConfigUtil.PlayerName = _playerName;
        PlayerRegistry.Instance.Clear();
    }

    // ---- R5: the ownership vocabulary ----

    /*
     * The word list is closed and measured. These are the five words the client puts between a name and its
     * summon across six captures; everything else that shows up as "`s <word>" is an NPC's own possessive text
     * ("`s corpse", "`s Acolyte"), and claiming those would make pets out of corpses.
     */
    [TestMethod]
    public void TheOwnershipVocabularyIsFiveWords()
    {
        Assert.AreEqual("Bulgar", ClassificationRules.OwnerInName("Bulgar`s pet"));
        Assert.AreEqual("Tuona", ClassificationRules.OwnerInName("Tuona`s ward"));
        Assert.AreEqual("Sancus", ClassificationRules.OwnerInName("Sancus`s warder"));
        Assert.AreEqual("Necra", ClassificationRules.OwnerInName("Necra`s familiar"));
        Assert.AreEqual("Rider", ClassificationRules.OwnerInName("Rider`s Mount"));

        // The logs do print the possessive word capitalised; a case-sensitive list loses those lines.
        Assert.AreEqual("Sancus", ClassificationRules.OwnerInName("Sancus`s Warder"));

        // Possessives that are not ownership, and a name that merely contains the letters.
        Assert.IsNull(ClassificationRules.OwnerInName("The Eviscerator`s corpse"));
        Assert.IsNull(ClassificationRules.OwnerInName("Kazon Warlord`s Acolyte"));
        Assert.IsNull(ClassificationRules.OwnerInName("Petstore Charlie"));
    }

    /*
     * "Akini, Xanathan`s Warder" is one summon named after two masters. The pet claim survives (the line
     * proves ownership either way); the OWNER claim does not, because claiming a raid member named
     * "Akini, Xanathan" would invent a person with a comma in it.
     */
    [TestMethod]
    public void ACommaInFrontOfTheWordClaimsThePetNotAFakeRaidMember()
    {
        var run = RunDerive(
            "[Mon May 04 18:50:01 2026] Targeted (Player): Healerone",
            "[Mon May 04 18:50:02 2026] Akini, Xanathan`s Warder hits a frostbound sentinel for 900 points of damage.",
            "[Mon May 04 18:50:42 2026] Akini, Xanathan`s Warder hits a frostbound sentinel for 900 points of damage.");

        var timeline = Cold(run);
        AssertIdentity(timeline, "Akini, Xanathan`s Warder", IdentityKind.Pet, "R5-owner");
        Assert.AreEqual(IdentityKind.Unknown, timeline.IdentityWithSource("Akini, Xanathan", out _),
            "the text in front of the word became a raid member");
    }

    /*
     * A ward that never swings still belongs to somebody. Ownership is read from the NAME POOL, not from the
     * damage facts: on the real captures wards are overwhelmingly something the raid heals, never something
     * the raid watches attack, and a summon nobody owns is a stray row in the fight list forever.
     */
    [TestMethod]
    public void ASummonThatOnlyEverGetsHealedStillBelongsToItsOwner()
    {
        var run = RunDerive(
            "[Mon May 04 18:50:01 2026] Targeted (Player): Healerone",
            "[Mon May 04 18:50:02 2026] Healerone healed Tuona`s ward for 4200 hit points by Blessed Radiance Rk. II.",
            "[Mon May 04 18:51:02 2026] Healerone healed Tuona`s ward for 4200 hit points by Blessed Radiance Rk. II.");

        var timeline = Cold(run);
        AssertIdentity(timeline, "Tuona`s ward", IdentityKind.Pet, "R5-owner:Tuona");
        Assert.AreEqual(AffiliationKind.PetOfPlayer,
            timeline.AffiliationAt("Tuona`s ward", DateUtil.StandardDateToDotNetSeconds("[Mon May 04 18:52:00 2026] x"), out var affSrc),
            "the owner-keyed pet claim is missing (the pet metric is scored through it)");
        Assert.AreEqual("R5-owner:Tuona", affSrc);

        // The owner is corroborated, not proven: the line proves a summon, not a person.
        AssertIdentity(timeline, "Tuona", IdentityKind.Player, "R5-owner");
    }

    // ---- R14: the article as the game's own NPC marker ----

    [TestMethod]
    public void AnArticleIsTheGamesOwnNpcMarker()
    {
        var run = RunDerive(
            "[Mon May 04 18:50:01 2026] Targeted (Player): Healerone",
            "[Mon May 04 18:50:01 2026] Targeted (Player): The Probechosen",
            "[Mon May 04 18:50:02 2026] A nonsense probe beast hits Healerone for 900 points of damage.",
            "[Mon May 04 18:50:42 2026] An unused probe horror hits Healerone for 900 points of damage.",
            "[Mon May 04 18:51:22 2026] The Probechosen hits Healerone for 900 points of damage.");

        var timeline = Cold(run);
        AssertIdentity(timeline, "A nonsense probe beast", IdentityKind.Npc, "R14-shape");
        AssertIdentity(timeline, "An unused probe horror", IdentityKind.Npc, "R14-shape");

        // Certain evidence outranks the shape: a target frame saying (Player) on an article-shaped name wins.
        Assert.AreEqual(IdentityKind.Player, timeline.IdentityWithSource("The Probechosen", out var src),
            $"line evidence lost to a name shape (source {src ?? "none"})");

        // The rule stays Medium, so it is corroboration and never the last word over behaviour.
        Assert.AreEqual(RuleStrength.Medium, Held(timeline, "A nonsense probe beast"));
    }

    /*
     * Where npcs.txt already names the creature, the database is the better provenance for the report even
     * though both rules answer Npc — equal strength is resolved by "last writer wins", so R14 yields instead
     * of overwriting. This test is the only thing standing between this rule and silently relabelling every
     * database hit as a guess about grammar.
     */
    [TestMethod]
    public void ADatabaseNameKeepsItsOwnReason()
    {
        PipelineHarness.EnsureDataStore();

        var run = RunDerive(
            "[Mon May 04 18:50:01 2026] Targeted (Player): Healerone",
            "[Mon May 04 18:50:02 2026] A bixie commander hits Healerone for 900 points of damage.");

        var name = run.Facts.InternedNames.FirstOrDefault(n => n.Contains("bixie commander", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(string.IsNullOrEmpty(name), "fixture log produced no bixie commander?");
        Assert.IsTrue(EQDataStore.Instance.IsKnownNpc(name!), $"npcs.txt does not know {name} — pick a different name for this test");

        AssertIdentity(Cold(run), name!, IdentityKind.Npc, "R6-npcdb");
    }

    // ---- R7: the proportional guard ----

    /*
     * Fifty hits on a target-frame NPC plus exactly one hit on an unnamed something is a raid-side melee main,
     * not an ambiguous pile. Before this change those 50 edges were vetoed by the one thing the rules had not
     * named — on the real captures that is how `Squirticus` (8,909 attack edges, a handful of them aimed at
     * names nothing had classified yet) stayed unclassified while
     * everything next to it got classified. Two unnamed edges out of 52 is over the allowance and stays
     * Unknown: a melee pile that is mostly unnamed proves nothing either way.
     */
    [TestMethod]
    public void OneUnnamedDefenderInFiftyNoLongerVetoesTheInference()
    {
        var lines = new List<string> { "[Mon May 04 18:50:01 2026] Targeted (NPC): Grisel Noshikun" };

        for (var i = 0; i < 60; i++)
        {
            var t = Timestamp(18, 51 + i, 0);       // a minute apart: each wave is its own instance
            lines.Add($"[{t}] Grumblechin hits Grisel Noshikun for 900 points of damage.");
            lines.Add($"[{t}] Mudflap hits Grisel Noshikun for 900 points of damage.");
        }

        // The allowance is a share of this attacker's own edges: 1/61 for Grumblechin, 2/62 for Mudflap.
        lines.Add($"[{Timestamp(19, 0, 0)}] Grumblechin hits Nobodyatall for 900 points of damage.");
        lines.Add($"[{Timestamp(19, 0, 30)}] Mudflap hits Nobodyatall for 900 points of damage.");
        lines.Add($"[{Timestamp(19, 0, 40)}] Mudflap hits Nobodyelseatall for 900 points of damage.");

        var timeline = Cold(RunDerive(lines.ToArray()));
        AssertIdentity(timeline, "Grumblechin", IdentityKind.Player, "R7-graph");
        Assert.AreEqual(IdentityKind.Unknown, timeline.IdentityWithSource("Mudflap", out var mudSrc),
            $"a mostly-unclassified pile was read as a side (source {mudSrc ?? "none"})");
    }

    // ---- R15: the heal edge ----

    [TestMethod]
    public void ANameTheRaidKeepsHealingIsOnOurSide()
    {
        var run = RunDerive(HealLines("Mercless", 12).ToArray());
        var timeline = Cold(run);

        AssertIdentity(timeline, "Mercless", IdentityKind.Player, "R15-healed");
    }

    /*
     * Everything that makes the edge evidence rather than a coincidence: enough lines, more than one caster,
     * and a target that does not hit back. The third case is the trap this rule was nearly written without —
     * raid AoE waters the mob stack too ("Yokii healed an arcborn wraith for 2 hit points"), and a boss does
     * answer for itself by swinging at the raid.
     */
    [TestMethod]
    public void AHealEdgeNeedsVolumeTwoCastersAndNoSwingsBack()
    {
        // Too few heal lines.
        var timeline = Cold(RunDerive(HealLines("Fewlines", 6).ToArray()));
        Assert.AreEqual(IdentityKind.Unknown, timeline.Identity("Fewlines"), "6 heal lines were enough");

        // One caster healing twelve times is one player's habit, not the raid's picture of a mate.
        timeline = Cold(RunDerive(OneCasterLines("Solohealee", 12).ToArray()));
        Assert.AreEqual(IdentityKind.Unknown, timeline.Identity("Solohealee"), "a single caster was enough");

        /*
         * Healed by the raid AND hitting the raid: hostile. Ten swings in among twelve heal lines is well over
         * the friendly-fire allowance, and this is exactly what a nameplate tanked boss looks like from below.
         */
        var lines = HealLines("Gloomfang", 12).ToList();
        for (var i = 0; i < 10; i++)
        {
            lines.Add($"[{Timestamp(19, 30, i * 6)}] Gloomfang hits Healerone for 4400 points of damage.");
        }
        timeline = Cold(RunDerive(lines.ToArray()));
        Assert.AreNotEqual(IdentityKind.Player, timeline.Identity("Gloomfang"), "the boss that got rained on joined our side");

        // A healer who is only a MEDIUM name (an owner named solely by its pet's line) cannot launder anyone.
        timeline = Cold(RunDerive(MediumCasterLines("Laundered").ToArray()));
        Assert.AreEqual(IdentityKind.Unknown, timeline.Identity("Laundered"), "a Medium healer put a name on our side");
    }

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

    // Two verified players healing `target` `count` times over six minutes.
    private static IEnumerable<string> HealLines(string target, int count)
    {
        yield return "[Mon May 04 18:50:01 2026] Targeted (Player): Healerone";
        yield return "[Mon May 04 18:50:01 2026] Targeted (Player): Healertwo";
        for (var i = 0; i < count; i++)
        {
            var caster = i % 2 == 0 ? "Healerone" : "Healertwo";
            yield return $"[{Timestamp(19, 0, i * 30)}] {caster} healed {target} for 5000 (9000) hit points by Blessed Radiance Rk. II.";
        }
    }

    private static IEnumerable<string> OneCasterLines(string target, int count)
    {
        yield return "[Mon May 04 18:50:01 2026] Targeted (Player): Healerone";
        for (var i = 0; i < count; i++)
        {
            yield return $"[{Timestamp(19, 0, i * 30)}] Healerone healed {target} for 5000 (9000) hit points by Blessed Radiance Rk. II.";
        }
    }

    // Both casters are only owners-of-a-pet (Medium corroboration), never line-verified themselves.
    private static IEnumerable<string> MediumCasterLines(string target)
    {
        yield return "[Mon May 04 18:50:01 2026] Ownerone`s pet hits a frostbound sentinel for 900 points of damage.";
        yield return "[Mon May 04 18:50:02 2026] Ownertoo`s pet hits a frostbound sentinel for 900 points of damage.";
        for (var i = 0; i < 12; i++)
        {
            var caster = i % 2 == 0 ? "Ownerone" : "Ownertoo";
            yield return $"[{Timestamp(19, 0, i * 30)}] {caster} healed {target} for 5000 (9000) hit points by Blessed Radiance Rk. II.";
        }
    }

    private static string Timestamp(int hour, int minute, int secondOffset)
    {
        var t = new DateTime(2026, 5, 4, hour, 0, 0, DateTimeKind.Utc).AddMinutes(minute).AddSeconds(secondOffset);
        return $"Mon May {t.Day:00} {t.Hour:00}:{t.Minute:00}:{t.Second:00} 2026";
    }

    private static PipelineHarness.DeriveRunResult RunDerive(params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "derive-ext-" + Guid.NewGuid().ToString("N")));
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
