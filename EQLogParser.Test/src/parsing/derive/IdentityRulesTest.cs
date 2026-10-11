using EQLogParser;

namespace EQLogParser;

// Phase 2 identity rules (docs/DesignNotes.md → "The identity rule book" R-catalog), cold mode: a fresh EntityTimeline
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

        /*
         * R3 presence and speech. Presence is FIVE sources now, one per kind of sighting (joined raid, left raid,
         * joined group, left group, leader) — they used to share "R3-presence", which is why the Names window could only
         * say "Raid" about all of them and an operator asked what that meant.
         */
        AssertIdentity(timeline, "Raidos", IdentityKind.Player, "R3-joinraid");
        AssertIdentity(timeline, "Chiefoc", IdentityKind.Player, "R3-leader");
        AssertIdentity(timeline, "Chatterbox", IdentityKind.Player, "R3-chat");
        AssertIdentity(timeline, "Raiderone", IdentityKind.Player, "R3-chat");

        // R3 negative: hostile say-channel speech is not player evidence. The speaker also does
        // not appear in npcs.txt on this fixture, so it must stay Unknown entirely.
        Assert.AreEqual(IdentityKind.Unknown, timeline.IdentityWithSource("Mobboss", out _), "say channel leaked into R3");

        // R2: /who roster line proves player outright
        AssertIdentity(timeline, "Whoguy", IdentityKind.Player, "R2-who");

        /*
         * R5's two shapes point at DIFFERENT names. `Sirmr`s pet` states its owner inside the name, so the PET is the
         * claim and the owner only corroborates; "Frobum is called to it owner." describes where a summon arrived, and
         * the name in it is the SUMMONER — measured on 48/48 occurrences across six captures, every one within three
         * lines of that same name's own `begins casting Summon …` (docs/DesignNotes.md → "The companion line names the
         * summoner"). A build that read this backwards put raid members on the sheet as Pets.
         */
        AssertIdentity(timeline, "Frobum", IdentityKind.Player, "R5-companion");
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
    public void VersionedFamilyListsAreNineteenAndOneNoMore()
    {
        // Closed vocabulary, house law: a new family arrives with a census over several eras AND
        // servers (mob-shaped caster count must stay zero), one settings-free table row, and tests.
        // Finishing Blow was refused entry on measurement: every attacker already held a stronger
        // claim, so the modifier mask stays stats-only (docs/DesignNotes.md → "A versioned class-spell family claims its caster").
        Assert.AreEqual(22, EQDataStore.ClassSafeSpellFamilies.Length);
        Assert.AreEqual(1, EQDataStore.PetCastSpellFamilies.Length);

        // The shipped vocabulary spelled out, so an addition has to be written down on purpose.
        CollectionAssert.AreEquivalent(new[]
        {
            "Boastful Bellow", "Boastful Conclusion", "Frenzy of Spirit", "Paragon of Spirit",
            "Focused Paragon of Spirit", "Hobble of Spirits", "Tireless Sprint",
            "Celestial Regeneration", "Focused Celestial Regeneration", "Spirit of the Wood",
            "Nature's Boon", "Gather Mana", "Eldritch Rune", "Battle Leap Warcry", "Battle Leap",
            "Guardian of the Forest", "Group Guardian of the Forest", "Lay on Hands", "Life Burn",
            "Area Taunt", "Cannibalization", "Leech Touch",
        }, EQDataStore.ClassSafeSpellFamilies.Select(f => f.Family).ToArray());
    }

    /*
     * The second AA trio (user-named, data-confirmed single-bit): Warrior's Area Taunt, Shaman's Cannibalization,
     * Shadow Knight's Leech Touch. Asserted the same way as every family before them - the printed rank resolves to
     * the class the user named, and a rank past the file is silence (Leech Touch has TWO ranks in the data, so its
     * III is the nearest gate check anywhere in the table).
     */
    [TestMethod]
    public void AreaTauntCannibalizationAndLeechTouch_ResolveToTheClassNamed()
    {
        PipelineHarness.EnsureDataStore();

        Assert.IsTrue(ClassificationRules.IsClassSafeCast("Area Taunt IX"));
        Assert.AreEqual("Warrior", EQDataStore.Instance.GetSpellClass("Area Taunt IX"));

        Assert.IsTrue(ClassificationRules.IsClassSafeCast("Cannibalization XXVII"));
        Assert.AreEqual("Shaman", EQDataStore.Instance.GetSpellClass("Cannibalization XXVII"));

        Assert.IsTrue(ClassificationRules.IsClassSafeCast("Leech Touch II"));
        Assert.AreEqual("Shadow Knight", EQDataStore.Instance.GetSpellClass("Leech Touch II"));

        Assert.IsFalse(ClassificationRules.IsClassSafeCast("Leech Touch III"));
        Assert.IsNull(EQDataStore.Instance.GetSpellClass("Leech Touch III"));
        Assert.IsFalse(ClassificationRules.IsClassSafeCast("Area Taunt X"));
    }

    /*
     * The AA trio added 2026-11, asserted at the gate exactly like the families before them: the rank resolves,
     * the class label says what the DATA says (Ranger / Paladin), and a rank beyond the shipped file is silence.
     */
    [TestMethod]
    public void GuardianLayOnHands_RankedCastsResolve_AndAnUnshippedRankIsSilent()
    {
        PipelineHarness.EnsureDataStore();

        foreach (var spell in new[] { "Guardian of the Forest XXXVIII", "Group Guardian of the Forest XVIII",
                                      "Guardian of the Forest XLI", "Lay on Hands XXXVI" })
        {
            Assert.IsTrue(ClassificationRules.IsClassSafeCast(spell), $"{spell} must claim its caster");
        }

        // The two Guardian forms are one ability behind two buttons, and two rows: the group form claims,
        // and neither prefix swallows the other's rank tail.
        Assert.IsTrue(EQDataStore.IsClassSafeSpellName("Group Guardian of the Forest XXI"));
        Assert.AreEqual("Ranger", EQDataStore.Instance.GetSpellClass("Guardian of the Forest XXXVIII"));
        Assert.AreEqual("Ranger", EQDataStore.Instance.GetSpellClass("Group Guardian of the Forest XVIII"));
        Assert.AreEqual("Paladin", EQDataStore.Instance.GetSpellClass("Lay on Hands XXXVI"));

        // Beyond the file: 42 ranks of Guardian, 36 of Lay on Hands. A rank past those is new content, and new
        // content claims NOTHING rather than being guessed at.
        Assert.IsFalse(ClassificationRules.IsClassSafeCast("Guardian of the Forest XLII"));
        Assert.IsNull(EQDataStore.Instance.GetSpellClass("Guardian of the Forest XLII"));
        Assert.IsFalse(ClassificationRules.IsClassSafeCast("Lay on Hands XXXVII"));

        // The versionless form really does print (`Lay on Hands.`, 8 lines) and really does claim nothing:
        // no rank, no family match - the same anchor that stops a bare `Boastful Bellow`.
        Assert.IsFalse(EQDataStore.IsClassSafeSpellName("Lay on Hands"));
    }

    /*
     * Life Burn: one name, two class lines IN THE DATA (`I..V` Nec, `VI..X` Shaman), and the operator's word is a
     * third (shadow knight). So it enters MULTI-BIT, which is the lane that says "a rank proves a PLAYER and nothing
     * else". Assert BOTH directions like every other ambiguous family: the identity gate OPEN (`IsClassSafeCast`
     * true) and the class map SHUT (`GetSpellClass` null on both halves of the split). Assert only the first half
     * and this seeds a Shaman label onto a necromancer's own AA.
     */
    [TestMethod]
    public void LifeBurn_IsPlayerEvidenceAndWritesNoClass_OnEitherHalfOfTheDataSplit()
    {
        PipelineHarness.EnsureDataStore();

        // The ranks the corpus actually prints are the Shaman-masked half, cast by necromancers.
        Assert.IsTrue(ClassificationRules.IsClassSafeCast("Life Burn VIII"));
        Assert.IsTrue(ClassificationRules.IsClassSafeCast("Life Burn X"));
        Assert.IsTrue(ClassificationRules.IsClassSafeCast("Life Burn I"));
        Assert.IsTrue(EQDataStore.Instance.IsClassAmbiguousFamilyRank("Life Burn VIII"));

        Assert.IsNull(EQDataStore.Instance.GetSpellClass("Life Burn VIII"), "the data's Shm reading may not reach the class column");
        Assert.IsNull(EQDataStore.Instance.GetSpellClass("Life Burn I"), "nor the Nec reading");

        // The rank gate holds this family like every other: XI does not exist in the shipped file.
        Assert.IsFalse(ClassificationRules.IsClassSafeCast("Life Burn XI"));
        Assert.IsFalse(EQDataStore.Instance.IsClassAmbiguousFamilyRank("Life Burn XI"));
    }

    /*
     * The same four names from the LOG side, in the shapes eleven captures actually print: one versioned cast each
     * (the group Guardian is its own button), and one bare `Lay on Hands.` which must leave its caster exactly as
     * it found it. This is the yield of the three family rows: a raid member who says nothing else - no /who, no
     * Targeted frame, no NPC-attacking swing - is still a PLAYER because the client watched him burn his own AA rank.
     */
    [TestMethod]
    public void AVersionedAaCastClaimsItsCaster_GuardianLayOnHandsAndLifeBurn()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "derive-aacast-" + Guid.NewGuid().ToString("N")));
        var log = Path.Combine(dir.FullName, "eqlog_Guardianone_Eqgate.txt");
        try
        {
            File.WriteAllLines(log, new[]
            {
                "[Sun Apr 26 18:00:00 2026] Praxxis begins casting Guardian of the Forest XXXVIII.",
                "[Sun Apr 26 18:00:30 2026] Lanedoraa begins casting Group Guardian of the Forest XVIII.",
                "[Sun Apr 26 18:01:00 2026] Shmid begins casting Lay on Hands XXXVI.",
                "[Sun Apr 26 18:01:30 2026] Coas begins casting Life Burn IX.",
                // The versionless form of the same AA (8 lines over the corpus): no rank, no claim.
                "[Sun Apr 26 18:02:00 2026] Barehands begins casting Lay on Hands.",
            });

            var run = PipelineHarness.RunFileDerived(log);
            var timeline = new EntityTimeline();
            ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

            AssertIdentity(timeline, "Praxxis", IdentityKind.Player, "R4-spell");
            AssertIdentity(timeline, "Lanedoraa", IdentityKind.Player, "R4-spell");
            AssertIdentity(timeline, "Shmid", IdentityKind.Player, "R4-spell");
            AssertIdentity(timeline, "Coas", IdentityKind.Player, "R4-spell");

            Assert.AreEqual(IdentityKind.Unknown,
                timeline.IdentityWithSource("Barehands", out var bareSrc),
                $"a versionless AA cast claimed a combatant (source {bareSrc ?? "none"})");
        }
        finally
        {
            try { Directory.Delete(dir.FullName, true); } catch (IOException) { }
        }
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

    /*
     * Provenance assertions are exact, INCLUDING the tail a rule carries: a spell verdict names its cast inside the source
     * ("R4-spell") because that is the detail which makes the verdict checkable — and it is the same
     * string this server remembers about the name, so an older log's answer can still say what it read
     * (IdentityVocabulary.ProofText turns it back into "Cast Boastful Bellow XLVII in previous log").
     */
    private static void AssertIdentity(EntityTimeline timeline, string name, IdentityKind expected, string expectedSource)
    {
        var kind = timeline.IdentityWithSource(name, out var source);
        Assert.AreEqual(expected, kind, $"identity for {name} (source {source ?? "none"})");
        Assert.AreEqual(expectedSource, source, $"provenance for {name}");
    }
}
