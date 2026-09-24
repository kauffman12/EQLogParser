using EQLogParser.Mirror;

namespace EQLogParser;

// Phase 2 identity rules (docs/batch-parsing-plan.md R-catalog), cold mode: a fresh EntityTimeline
// fed only by ClassificationRules over the evidence facts — no registry seed, so every verdict is
// attributable to a rule. The fixture exercises one case per implemented rule plus the negatives
// that keep each rule honest (say-channel speaker stays Unknown, buff wear-off on a player name
// does not open a charm window, etc).
[TestClass]
public class MirrorRulesTest
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "mini-data", "mirror", "rules-fixture.txt");

    [TestMethod]
    public void ColdRules_EveryImplementedRuleFires_OnFixture()
    {
        Assert.IsTrue(File.Exists(FixturePath), $"missing fixture: {FixturePath}");

        var run = PipelineHarness.RunFileWithMirror(FixturePath);
        var facts = run.Facts;
        Assert.IsTrue(facts.EvidenceCount >= 12, $"expected identity evidence, got {facts.EvidenceCount} — tap not wired?");

        var timeline = new EntityTimeline();
        var outcome = ClassificationRules.Apply(facts, timeline);
        CollectionAssert.AreEqual(Array.Empty<string>(), outcome.Conflicts, "target-frame kinds must not collide on the fixture");

        // R1: targeted verdicts, both kinds
        AssertIdentity(timeline, "Verifyguy", IdentityKind.Player, "R1-target");
        AssertIdentity(timeline, "Grisel Noshikun", IdentityKind.Npc, "R1-target");

        // R3 presence (raid join, raid leader) and speech (group tell, raid tell)
        AssertIdentity(timeline, "Raidos", IdentityKind.Player, "R3-presence");
        AssertIdentity(timeline, "Chiefoc", IdentityKind.Player, "R3-presence");
        AssertIdentity(timeline, "Chatterbox", IdentityKind.Player, "R3-chat");
        AssertIdentity(timeline, "Raiderone", IdentityKind.Player, "R3-chat");

        // R3 negative: hostile say-channel speech is not player evidence. The speaker also does
        // not appear in npcs.txt on this fixture, so it must stay Unknown entirely.
        Assert.AreEqual(IdentityKind.Unknown, timeline.IdentityWithSource("Mobboss", out _), "say channel leaked into R3");

        // R2: /who roster line proves player outright
        AssertIdentity(timeline, "Whoguy", IdentityKind.Player, "R2-who");

        // R5: called-to-owner and owner-in-line (Sirmr`s pet). The named owner only corroborates.
        AssertIdentity(timeline, "Frobum", IdentityKind.Pet, "R5-called");
        AssertIdentity(timeline, "Sirmr`s pet", IdentityKind.Pet, "R5-owner:Sirmr");
        Assert.AreEqual(IdentityKind.Player, timeline.IdentityWithSource("Sirmr", out var ownerSrc), "owner corroboration missing");
        Assert.AreEqual("R5-owner", ownerSrc);

        // R4 tier 1: single-bit spire (structural seed) and zero-mask spire rank (curated seed)
        AssertIdentity(timeline, "Spireina", IdentityKind.Player, "R4-spell");
        AssertIdentity(timeline, "Epica", IdentityKind.Player, "R4-spell");

        // R6: multi-word NPC-database name asserts Npc at Medium. The damage parser stores the
        // attacker exactly as the log spells it ("A bixie commander"), so resolve by case match.
        var bixie = facts.InternedNames.First(n => n.Contains("bixie commander", StringComparison.OrdinalIgnoreCase));
        AssertIdentity(timeline, bixie, IdentityKind.Npc, "R6-npcdb");

        // R9: charm window is time-scoped; identity itself stays Npc
        AssertIdentity(timeline, "a toughened horror", IdentityKind.Npc, "R9-charm");
        var charmT0 = DateUtil.StandardDateToDotNetSeconds("[Sun Apr 26 18:40:10 2026] x");
        var charmT1 = DateUtil.StandardDateToDotNetSeconds("[Sun Apr 26 18:40:11 2026] x");
        Assert.AreEqual(AffiliationKind.Friendly, timeline.AffiliationAt("a toughened horror", charmT0 + 0.5, out var charmSrc));
        Assert.AreEqual("R9-charm", charmSrc);
        // half-open on the end side: wear-off second is already Enemy again
        Assert.AreEqual(AffiliationKind.Enemy, timeline.AffiliationAt("a toughened horror", charmT1, out _));
        Assert.IsTrue(charmT1 > charmT0);

        // R13: Targeted (NPC) plus player-shaped behavior (join + group chat) is the merc
        // signature — neither Player (behavior lies for hirelings) nor plain Npc.
        AssertIdentity(timeline, "Hirelina", IdentityKind.Merc, "R13-merc");

        // R7: instance-counting inference recovers names with zero other evidence. Zorchmaw's
        // five hydra hits are THREE instances (two combat-gap splits) over 110 s -> player-side.
        AssertIdentity(timeline, "Zorchmaw", IdentityKind.Player, "R7-graph");

        // R7 mirror direction: attacking exactly three player-side names across 65 s is a mob.
        AssertIdentity(timeline, "Witherfang", IdentityKind.Npc, "R7-side");

        // R9 until-death: no wear-off line, the slain event closes the charm window
        var tCharm = DateUtil.StandardDateToDotNetSeconds("[Sun Apr 26 18:45:30 2026] x");
        var tDeath = DateUtil.StandardDateToDotNetSeconds("[Sun Apr 26 18:46:10 2026] x");
        Assert.AreEqual(AffiliationKind.Friendly, timeline.AffiliationAt("a grimtooth matriarch", tCharm + 5, out var untilSrc));
        Assert.AreEqual("R9-charm", untilSrc);
        // death second itself is Enemy again (half-open like wear-off), despite "A" vs "a" casing
        Assert.AreEqual(AffiliationKind.Enemy, timeline.AffiliationAt("a grimtooth matriarch", tDeath + 0.5, out _));
    }

    [TestMethod]
    public void ManualOverride_BeatsEveryRule()
    {
        // R10 seed for the future UI action "set as merc" — Manual strength outranks Certain.
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Someguy", IdentityKind.Player, RuleStrength.Certain, "R1-target");
        ClassificationRules.ApplyManualOverride(timeline, "Someguy", IdentityKind.Merc);

        AssertIdentity(timeline, "Someguy", IdentityKind.Merc, "R10-manual");
    }

    [TestMethod]
    public void SpellSeed_ClassSafeFamilies_ResolveThroughEngineMap()
    {
        PipelineHarness.EnsureDataStore();

        // structural: single-bit spire ranks; curated: the zero-mask Minstrels I-III rows.
        Assert.IsNotNull(EQDataStore.Instance.GetSpellClass("Spire of the Juggernaut XII"));
        Assert.IsNotNull(EQDataStore.Instance.GetSpellClass("Spire of the Minstrels I"));
        Assert.IsTrue(EQDataStore.IsClassSafeSpellName("Spire of the Savage Lord V"));

        // A generic multi-mask (0) proc spell must NOT become class evidence.
        Assert.IsFalse(EQDataStore.IsClassSafeSpellName("Savage Bloodlust Effect"));
        Assert.IsNull(EQDataStore.Instance.GetSpellClass("Savage Bloodlust Effect"));
    }

    private static void AssertIdentity(EntityTimeline timeline, string name, IdentityKind expected, string expectedSource)
    {
        var kind = timeline.IdentityWithSource(name, out var source);
        Assert.AreEqual(expected, kind, $"identity for {name} (source {source ?? "none"})");
        Assert.AreEqual(expectedSource, source, $"provenance for {name}");
    }
}
