using EQLogParser;

namespace EQLogParser;

/*
 * R16 — the title comma, the other marker the client writes inside a name.
 *
 * `Teknaz, Bringer of Flames` is "Name, Title", and an EQ character name is ONE token: no spaces, no commas. So a
 * comma in a name field cannot be a raid member. R14 reads the article in front of a name; this reads the title
 * after it, and the two markers do not overlap — `Kratakel, Lord Misery` takes neither an article nor (until Sep
 * 2026) a registry entry, while carrying 71M of attack damage across our captures.
 *
 * Measured over the six captures 2022-2026 (docs/combat-mirror-design.md → "A comma means it is not a player"):
 * exactly 11 name fields carry a comma, seven of them also arrive with `Targeted (NPC)` (Certain), one is an owned
 * summon, and none — not once in four years — ever appears under `Targeted (Player)`. The three left over rest on
 * npcs.txt alone, which is the arrangement this rule backs up for content newer than any list we ship.
 *
 * Why the tiering in the registry reads the way it does, from the same count: 238 of its 38,423 distinct entries
 * carry a title comma and 19,026 take an article — shapes no character name can wear — while 3,211 (8 %) are one
 * bare word (`lethar`, `midnight`, `plann`), which is exactly what a character name looks like. Punctuation is
 * evidence; a bare word is not, and that is R6's Weak/Medium split measured rather than asserted.
 *
 * Cold mode throughout: no registry seed, so every verdict below is attributable to one rule.
 */
[TestClass]
[DoNotParallelize]
public class CommaTitleTest
{
    private string? _playerName;

    [TestInitialize]
    public void Setup()
    {
        _playerName = ConfigUtil.PlayerName;
        PlayerRegistry.Instance.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
        ConfigUtil.PlayerName = _playerName;
        PlayerRegistry.Instance.Clear();
    }

    /*
     * A boss with a title, in a capture whose npcs.txt has never heard of it and where nobody ever targets it:
     * the comma is the only evidence in the file. It arrives as Npc, at Medium — the strength a shape rule gets,
     * so that any piece of line evidence can still outvote it.
     */
    [TestMethod]
    public void ATitledNameIsAnNpcOnTheCommaAlone()
    {
        const string name = "Vhorvus, the Ashen Crown";
        Assert.IsFalse(EQDataStore.Instance.IsKnownNpc(name), $"{name} is in npcs.txt; pick a name the registry does not know");

        var timeline = Cold(RunDerive(
            "[Mon May 04 18:50:01 2026] Targeted (Player): Healerone",
            "[Mon May 04 18:50:02 2026] " + name + " hits Healerone for 900 points of damage.",
            "[Mon May 04 18:50:06 2026] " + name + " hits Healerone for 900 points of damage."));

        AssertIdentity(timeline, name, IdentityKind.Npc, "R16-comma");

        timeline.IdentityAt(name, double.PositiveInfinity, out var strength, out _);
        Assert.AreEqual(RuleStrength.Medium, strength, "a shape rule must stay below line evidence");
    }

    /*
     * When npcs.txt does know the name (238 comma entries in the shipped list), its word is the better provenance
     * — the report should say WHICH list says so. At equal strength the earlier writer keeps the reason, so R16
     * never repaints a registry hit.
     */
    [TestMethod]
    public void TheRegistryKeepsItsReasonForATitledName()
    {
        const string name = "Glarubaran, the Great Storm";
        Assert.IsTrue(EQDataStore.Instance.IsKnownNpc(name), $"{name} left npcs.txt; pick a comma entry the registry still has");

        var timeline = Cold(RunDerive(
            "[Mon May 04 18:50:01 2026] Targeted (Player): Healerone",
            "[Mon May 04 18:50:02 2026] " + name + " hits Healerone for 900 points of damage.",
            "[Mon May 04 18:50:07 2026] " + name + " hits Healerone for 900 points of damage."));

        AssertIdentity(timeline, name, IdentityKind.Npc, "R6-npcdb");
    }

    /*
     * ``Akini, Xanathan`s Warder`` is one summon named after two masters: the ownership word outranks the shape,
     * and R5's owner cut already refuses a comma owner rather than inventing a raid member called
     * "Akini, Xanathan" (ClassificationRules.UsableOwnerInName). So the summon stays a pet with no claimed master.
     */
    [TestMethod]
    public void AnOwnedSummonWithACommaStaysAPetAndClaimsNoMaster()
    {
        const string name = "Akini, Xanathan`s Warder";

        var timeline = Cold(RunDerive(
            "[Mon May 04 18:50:01 2026] Targeted (Player): Healerone",
            "[Mon May 04 18:50:02 2026] " + name + " hits Healerone for 900 points of damage."));

        AssertIdentity(timeline, name, IdentityKind.Pet, "R5-owner");
        Assert.AreEqual(IdentityKind.Unknown, timeline.IdentityWithSource("Akini, Xanathan", out var ownerSource),
            $"the comma in front of the ownership word is not a player ({ownerSource ?? "no assignment"})");
    }

    /*
     * A shape loses to a line, always. This fixture is illegal in a real client — the game would not print a
     * comma in a character name — and it stays illegal on purpose; what it pins is the ladder underneath, that
     * Certain target-frame evidence beats Medium shape evidence no matter which order the two arrive in.
     */
    [TestMethod]
    public void LineEvidenceStillOutranksTheCommaShape()
    {
        const string name = "Vhorvus, the Ashen Crown";

        var timeline = Cold(RunDerive(
            "[Mon May 04 18:50:01 2026] Targeted (Player): " + name,
            "[Mon May 04 18:50:02 2026] " + name + " hits a frostbound sentinel for 900 points of damage."));

        AssertIdentity(timeline, name, IdentityKind.Player, "R1-target");
    }

    /*
     * Spell names carry commas too (`First Create, Then Destroy`, `Teleport: Doomfire, the Burning Lands`), and a
     * dot line's attacker field is one of them: reading that as a combatant would hand the graph a friendly target
     * to score against. Same guard R14 uses — anything the spell DB answers for stays unclassified.
     */
    [TestMethod]
    public void ASpellNameWithACommaIsNotACombatant()
    {
        const string name = "First Create, Then Destroy";
        Assert.IsNotNull(EQDataStore.Instance.GetDamagingSpellByName(name), $"{name} is no longer a damaging spell; pick another");

        var timeline = Cold(RunDerive(
            "[Mon May 04 18:50:01 2026] Targeted (Player): Healerone",
            "[Mon May 04 18:50:02 2026] " + name + " hits Healerone for 900 points of damage."));

        timeline.IdentityWithSource(name, out var source);
        Assert.AreNotEqual("R16-comma", source, "the comma rule fired on a spell name");
    }

    // A bare name with no comma behind it is not a title: `Bidils` and `Bidils,` say nothing.
    [TestMethod]
    public void ATrailingCommaIsNotATitle()
    {
        Assert.IsTrue(ClassificationRules.HasTitleComma("Kratakel, Lord Misery"));
        Assert.IsFalse(ClassificationRules.HasTitleComma("Kratakel,"));
        Assert.IsFalse(ClassificationRules.HasTitleComma("Kratakel"));
        Assert.IsFalse(ClassificationRules.HasTitleComma(""));
    }

    /*
     * The one counterexample class this shape has: summon names are free text, so a pet may be called
     * "Feroun, come back", and it prints with no ownership word for R5 to read. It stays our side because R16 runs
     * after R15 — which only considers names still Unknown — so the ordering decides in favour of the heal lines.
     * Same lesson as `A good egg` (docs/combat-mirror-design.md → "Fourth audit"): a shape may say a name is not
     * a person, never which side it fights on.
     */
    [TestMethod]
    public void ARaidTendedSummonWithACommaStaysOurSide()
    {
        const string name = "Feroun, come back";

        var timeline = Cold(RunDerive(HealLines(name, 12).ToArray()));

        AssertIdentity(timeline, name, IdentityKind.Player, "R15-healed");
    }

    // ---- helpers (same shape as IdentityRuleExtensionsTest) ----

    // Two verified players healing `target` `count` times over six minutes.
    private static IEnumerable<string> HealLines(string target, int count)
    {
        yield return "[Mon May 04 18:50:01 2026] Targeted (Player): Healerone";
        yield return "[Mon May 04 18:50:01 2026] Targeted (Player): Healertwo";
        for (var i = 0; i < count; i++)
        {
            var caster = i % 2 == 0 ? "Healerone" : "Healertwo";
            yield return $"[Mon May 04 19:{i / 60:00}:{i % 60:00} 2026] {caster} healed {target} for 5000 (9000) hit points by Blessed Radiance Rk. II.";
        }
    }

    private static EntityTimeline Cold(PipelineHarness.DeriveRunResult run)
    {
        var timeline = new EntityTimeline();
        ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
        return timeline;
    }

    private static void AssertIdentity(EntityTimeline timeline, string name, IdentityKind expected, string expectedSource)
    {
        var kind = timeline.IdentityWithSource(name, out var source);
        Assert.AreEqual(expected, kind, $"{name}: expected {expected}, got {kind} ({source ?? "no assignment"})");
        Assert.AreEqual(expectedSource, source, $"{name}: wrong rule got there first");
    }

    private static PipelineHarness.DeriveRunResult RunDerive(params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mirror-comma-" + Guid.NewGuid().ToString("N")));
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
}
