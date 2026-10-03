using EQLogParser;

namespace EQLogParser;

// Phase 2 identity rules (docs/batch-parsing-plan.md R-catalog), cold mode: a fresh EntityTimeline
// fed only by ClassificationRules over the evidence facts — no registry seed, so every verdict is
// attributable to a rule. The fixture exercises one case per implemented rule plus the negatives
// that keep each rule honest (say-channel speaker stays Unknown, buff wear-off on a player name
// does not open a charm window, etc).
[TestClass]
public class IdentityRulesTest
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", "rules-fixture.txt");

    [TestMethod]
    public void ColdRules_EveryImplementedRuleFires_OnFixture()
    {
        Assert.IsTrue(File.Exists(FixturePath), $"missing fixture: {FixturePath}");

        var run = PipelineHarness.RunFileDerived(FixturePath);
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

        // R4 tier 1, versioned families: the roman-rank suffix must not break the match, and the
        // two Hobbles must split - `Hobble of Spirits VI` is the beastlord's own snare (Chantoya
        // and Ferociousley are players), `Hobble of Spirits Snare VI` is the pet's.
        AssertIdentity(timeline, "Chantoya", IdentityKind.Player, "R4-spell");
        AssertIdentity(timeline, "Ferociousley", IdentityKind.Player, "R4-spell");

        // R20: the exact pet-cast rank claims the caster as Pet with no owner. Snapclaw gets no
        // Player claim and no owner is invented from the spellbook owner above it.
        AssertIdentity(timeline, "Snapclaw", IdentityKind.Pet, "R20-petspell");

        // R4 tier 1, berserker family: a name nothing else claims, proven by one sprint cast.
        AssertIdentity(timeline, "Krietz", IdentityKind.Player, "R4-spell");

        // R4 tier 1, class-AMBIGUOUS rank (War|Ber): the caster is a player for certain, the class
        // is one of two and nothing writes one. And the cleric's healing discipline.
        AssertIdentity(timeline, "Brakka", IdentityKind.Player, "R4-spell");
        AssertIdentity(timeline, "Relaraa", IdentityKind.Player, "R4-spell");

        // R4 tier 1, druid family (rank XXVII of the complete I-XLVI run).
        AssertIdentity(timeline, "Faenwyn", IdentityKind.Player, "R4-spell");

        // R4 tier 1: the enchanter pair through the DB gate, and the USER-ASSERTED family (whose
        // ranks the shipped spells.txt does not contain at all) through its own gate.
        AssertIdentity(timeline, "Isilwynn", IdentityKind.Player, "R4-spell");
        AssertIdentity(timeline, "Willowmere", IdentityKind.Player, "R4-spell");
        AssertIdentity(timeline, "Bramblechord", IdentityKind.Player, "R4-spell");

        // R6: multi-word NPC-database name asserts Npc at Medium. The damage parser stores the
        // attacker exactly as the log spells it ("A bixie commander"), so resolve by case match.
        var bixie = facts.InternedNames.First(n => n.Contains("bixie commander", StringComparison.OrdinalIgnoreCase));
        AssertIdentity(timeline, bixie, IdentityKind.Npc, "R6-npcdb");

        // R17: the actor on a consume line. Only a player character carries a flask or a loaf; the vessel is not
        // part of the match (Water Flask vs Ironbone Mead) and both sound effect and verb match without case.
        AssertIdentity(timeline, "Guzzleway", IdentityKind.Player, "R17-selffeed");
        AssertIdentity(timeline, "Nibblenosh", IdentityKind.Player, "R17-selffeed");
        AssertIdentity(timeline, "Slurpmania", IdentityKind.Player, "R17-selffeed");

        // R9: charm window is time-scoped; identity itself stays Npc
        AssertIdentity(timeline, "a toughened horror", IdentityKind.Npc, "R9-charm");
        var charmT0 = DateUtil.StandardDateToDotNetSeconds("[Sun Apr 26 18:40:10 2026] x");
        var charmT1 = DateUtil.StandardDateToDotNetSeconds("[Sun Apr 26 18:40:11 2026] x");
        Assert.AreEqual(AffiliationKind.Friendly, timeline.AffiliationAt("a toughened horror", charmT0 + 0.5, out var charmSrc));
        Assert.AreEqual("R9-charm", charmSrc);
        // half-open on the end side: wear-off second is already Enemy again
        Assert.AreEqual(AffiliationKind.Enemy, timeline.AffiliationAt("a toughened horror", charmT1, out _));
        Assert.IsTrue(charmT1 > charmT0);

        // R0: the log's author — "You" is player by construction (fixture filename carries no
        // name, so ConfigUtil.PlayerName stays empty and the literal key is the identity target).
        AssertIdentity(timeline, ChatType.You, IdentityKind.Player, "R0-local");

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

    [TestMethod]
    public void VersionedFamilies_RankMatch_Split_Hobble_And_UnknownRankIsSilent()
    {
        PipelineHarness.EnsureDataStore();

        // Every rank printed by the corpus resolves to a class (all were verified present in
        // data/spells.txt) so the second gate on both cast rules passes for real captures.
        foreach (var spell in new[] { "Boastful Bellow XLVII", "Boastful Conclusion LIII", "Frenzy of Spirit XIII",
                                      "Paragon of Spirit XLI", "Focused Paragon of Spirit XXXIV", "Hobble of Spirits VI",
                                      "Hobble of Spirits Snare VI", "Tireless Sprint VIII", "Celestial Regeneration XLII",
                                      "Focused Celestial Regeneration XXVII", "Spirit of the Wood XLVI",
                                      "Gather Mana IV", "Eldritch Rune XI", "Nature's Boon XXXII" })
        {
            Assert.IsNotNull(EQDataStore.Instance.GetSpellClass(spell), $"{spell} must resolve through the family seed");
        }

        // Class-AMBIGUOUS ranks (multi-bit War|Ber families): the identity gate opens, the class
        // map deliberately stays shut - the caster is a player, the class is one of two, and a
        // coin flip in the registry's class column is worse than silence. Assert BOTH directions;
        // "the cast claims nothing" and "the class resolves" would each also pass a broken seed.
        Assert.IsTrue(ClassificationRules.IsClassSafeCast("Battle Leap Warcry II"));
        Assert.IsTrue(ClassificationRules.IsClassSafeCast("Battle Leap VII"));
        Assert.IsTrue(EQDataStore.Instance.IsClassAmbiguousFamilyRank("Battle Leap Warcry II"));
        Assert.IsNull(EQDataStore.Instance.GetSpellClass("Battle Leap Warcry II"));
        Assert.IsNull(EQDataStore.Instance.GetSpellClass("Battle Leap VII"));

        // The rank gate holds the ambiguous families exactly like the single-class ones. Warcry has
        // ranks I-III in the data and plain Battle Leap I-XIII - "Warcry IX" is the trap where one
        // sibling's rank exists and the other's does not.
        Assert.IsFalse(ClassificationRules.IsClassSafeCast("Battle Leap Warcry IX"));
        Assert.IsFalse(ClassificationRules.IsClassSafeCast("Battle Leap XIV"));
        Assert.IsFalse(EQDataStore.Instance.IsClassAmbiguousFamilyRank("Battle Leap Warcry IX"));

        // Nature's Boon (druid AA): a user-named spell whose ranks turned out to be IN the shipped
        // data all along (33 of them, single-bit Dru), so it rides the DB gate like every other
        // family - and rank XXXIV, beyond the data, is the silence law for this family specifically.
        Assert.IsTrue(ClassificationRules.IsClassSafeCast("Nature's Boon XXXII"));
        Assert.AreEqual("Druid", EQDataStore.Instance.GetSpellClass("Nature's Boon XXXII"));
        Assert.IsFalse(ClassificationRules.IsClassSafeCast("Nature's Boon XXXIV"));
        Assert.IsNull(EQDataStore.Instance.GetSpellClass("Nature's Boon XXXIV"));

        // Roman-rank anchor: the bare family never matches (no capture prints it versionless).
        Assert.IsFalse(EQDataStore.IsClassSafeSpellName("Boastful Bellow"));

        // The two Hobbles split: the player's rank is class-safe, the pet's exact name claims Pet
        // only - and the pet form must NOT match the player family (tail "Snare VI" is not a rank).
        Assert.IsTrue(EQDataStore.IsClassSafeSpellName("Hobble of Spirits VI"));
        Assert.IsFalse(EQDataStore.IsClassSafeSpellName("Hobble of Spirits Snare VI"));
        Assert.IsTrue(EQDataStore.IsPetCastSpellName("Hobble of Spirits Snare VI"));
        Assert.IsFalse(EQDataStore.IsPetCastSpellName("Hobble of Spirits VI"));

        // Gate: a rank the data files do not know claims nothing on either lane (new expansion
        // content degrades to silence, never to a guess).
        Assert.IsTrue(EQDataStore.IsClassSafeSpellName("Boastful Bellow LXI")); // text matches...
        Assert.IsNull(EQDataStore.Instance.GetSpellClass("Boastful Bellow LXI")); // ...but the data does not know it
        Assert.IsFalse(ClassificationRules.IsPetCastSpell("Hobble of Spirits Snare XLII"));
    }

    [TestMethod]
    public void VersionedFamilyListsAreFifteenAndOneNoMore()
    {
        // Closed vocabulary, house law: a new family arrives with a census over several eras AND
        // servers (mob-shaped caster count must stay zero), one settings-free table row, and tests.
        // Finishing Blow was refused entry on measurement: every attacker already held a stronger
        // claim, so the modifier mask stays stats-only (docs/combat-mirror-design.md).
        Assert.AreEqual(15, EQDataStore.ClassSafeSpellFamilies.Length);
        Assert.AreEqual(1, EQDataStore.PetCastSpellFamilies.Length);

        // The shipped vocabulary spelled out, so an addition has to be written down on purpose.
        CollectionAssert.AreEquivalent(new[]
        {
            "Boastful Bellow", "Boastful Conclusion", "Frenzy of Spirit", "Paragon of Spirit",
            "Focused Paragon of Spirit", "Hobble of Spirits", "Tireless Sprint",
            "Celestial Regeneration", "Focused Celestial Regeneration", "Spirit of the Wood",
            "Nature's Boon", "Gather Mana", "Eldritch Rune", "Battle Leap Warcry", "Battle Leap",
        }, EQDataStore.ClassSafeSpellFamilies.Select(f => f.Family).ToArray());
    }

    [TestMethod]
    public void SelfFeedback_SpellNameAttackerIsNeverClassified()
    {
        // R7 negative: "You have taken N damage from X." puts a SPELL NAME in the attacker field.
        // When the spell targets Self in the spell DB - spell feedback such as "Cloudburst Strike
        // Feedback XII" - the damage is the local player hitting themselves. Four instances over
        // three minutes, every defender player-side, must NOT produce an R7-side NPC: without the
        // guard this exact shape is what would brand the operator's own spell an enemy.
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "derive-feedback-" + Guid.NewGuid().ToString("N")));
        var log = Path.Combine(dir.FullName, "eqlog_Feedbackone_Eqgate.txt"); // filename seeds ConfigUtil.PlayerName via the harness
        var previousPlayerName = ConfigUtil.PlayerName;
        try
        {
            File.WriteAllLines(log, new[]
            {
                "[Sun Apr 26 18:48:00 2026] You have taken 16690 damage from Cloudburst Strike Feedback XII.",
                "[Sun Apr 26 18:49:05 2026] You have taken 16690 damage from Cloudburst Strike Feedback XII.",
                "[Sun Apr 26 18:50:10 2026] You have taken 16690 damage from Cloudburst Strike Feedback XII.",
                "[Sun Apr 26 18:51:15 2026] You have taken 16690 damage from Cloudburst Strike Feedback XII.",
            });

            var facts = PipelineHarness.RunFileDerived(log).Facts;
            Assert.AreEqual(4, facts.Facts.Length, "feedback lines produced no damage facts?");

            var timeline = new EntityTimeline();
            ClassificationRules.Apply(facts, timeline);

            // the defender must be player-side for the negative to mean anything
            AssertIdentity(timeline, "Feedbackone", IdentityKind.Player, "R0-local");
            Assert.AreEqual(IdentityKind.Unknown, timeline.IdentityWithSource("Cloudburst Strike Feedback XII", out var spellSrc),
                $"own spell feedback classified as a combatant (source {spellSrc ?? "none"})");
        }
        finally
        {
            ConfigUtil.PlayerName = previousPlayerName;
            try { Directory.Delete(dir.FullName, true); } catch (IOException) { }
        }
    }

    private static void AssertIdentity(EntityTimeline timeline, string name, IdentityKind expected, string expectedSource)
    {
        var kind = timeline.IdentityWithSource(name, out var source);
        Assert.AreEqual(expected, kind, $"identity for {name} (source {source ?? "none"})");
        Assert.AreEqual(expectedSource, source, $"provenance for {name}");
    }
}
