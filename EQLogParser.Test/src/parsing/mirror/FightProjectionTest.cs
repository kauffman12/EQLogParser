using EQLogParser.Mirror;

namespace EQLogParser;

// The displayed list is facts projected over the CURRENT classification: rows are not corrected
// in place, they are re-derived, so a row migrates to its true owner the moment evidence
// arrives. These tests drive that dynamic directly - build, stamp identity, rebuild - and pin the
// charm-window side flips (charmed raider owns her own row only while charmed; charmed mob's
// output counts toward its target's fight) plus the drops (friendly fire) and fallbacks
// (unclassified exchanges keep the legacy defender-key so nothing goes unaccounted).
[TestClass]
public class FightProjectionTest
{
    private const double T0 = 1_000;

    private static DamageFactTable BuildFacts(params (string Atk, string Def, uint Dmg, double T, byte Label)[] rows)
    {
        var facts = new DamageFactTable(64);
        var seq = 0;
        foreach (var (atk, def, dmg, t, label) in rows)
        {
            var a = facts.InternName(atk);
            var d = facts.InternName(def);
            facts.AddFact(new DamageFact(seq++, (long)(T0 + t), a, d, dmg, 0, label, 0, ushort.MaxValue));
        }
        return facts;
    }

    private static DerivedFight? Row(List<DerivedFight> rows, string name)
        => rows.FirstOrDefault(r => r.Name == name);

    // ---- row migration: the dynamic case the mirror exists for ----

    [TestMethod]
    public void PlayerEvidence_MigratesTheRowToTheNpcItWasFighting()
    {
        // "Echohead" hits Illuminai; with no classification the legacy tiebreak keys on defender.
        var facts = BuildFacts(("Echohead", "Illuminai", 50, 0, LabelTypes.Melee));
        var timeline = new EntityTimeline();

        var before = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, before.Count);
        Assert.AreEqual("Illuminai", before[0].Name, "unknown-vs-unknown must keep the legacy defender key");
        Assert.AreEqual(50, before[0].DamageToOwner);

        // R4-spell arrives (same pass structure MirrorSession runs): rebuild over the SAME facts.
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Medium, "R4-spell");
        var after = FightProjection.Build(facts, timeline);

        Assert.AreEqual(1, after.Count);
        Assert.AreEqual("Echohead", after[0].Name, "the exchange must belong to the NPC, not the raider");
        Assert.IsNull(Row(after, "Illuminai"));
        Assert.AreEqual(50, after[0].DamageByOwner);
    }

    [TestMethod]
    public void OneBrawl_OneRow_BothDirections_Merged()
    {
        var facts = BuildFacts(
            ("Echohead", "Illuminai", 50, 0, LabelTypes.Melee),
            ("Illuminai", "Echohead", 500, 1, LabelTypes.Melee));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Medium, "R4-spell");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count);
        var echo = rows[0];
        Assert.AreEqual("Echohead", echo.Name);
        Assert.AreEqual(550, echo.DamageTotal);
        Assert.AreEqual(500, echo.DamageToOwner);
        Assert.AreEqual(50, echo.DamageByOwner);
        Assert.IsTrue(echo.PlayerRollup.TryGetValue("Illuminai", out var agg) && agg.Damage == 500,
            "player credit survives the roll-up even though the registry never knew her");
    }

    // ---- drops ----

    [TestMethod]
    public void FriendlyFire_BetweenPlayerSideNames_CreatesNoRow()
    {
        var facts = BuildFacts(
            ("Aastrid", "Bbrennan", 99, 0, LabelTypes.Melee),      // player hits merc
            ("Ccxara`s pet", "Aastrid", 77, 1, LabelTypes.Melee)); // pet friendly-fires a raider
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Aastrid", IdentityKind.Player, RuleStrength.Certain, "R1-target");
        timeline.SetIdentity("Bbrennan", IdentityKind.Merc, RuleStrength.Certain, "R1-conflict");
        timeline.SetIdentity("Ccxara`s pet", IdentityKind.Pet, RuleStrength.Certain, "R5-owner:Aastrid");

        Assert.AreEqual(0, FightProjection.Build(facts, timeline).Count);
    }

    // ---- charm windows flip sides, never identities ----

    [TestMethod]
    public void CharmedRaider_OwnsHerOwnRow_OnlyWhileCharmed()
    {
        var facts = BuildFacts(
            ("Illuminai", "Raidman", 77, 150, LabelTypes.Melee),   // inside the window: the raid IS fighting her
            ("Illuminai", "Raidman", 88, 250, LabelTypes.Melee));  // after it: plain friendly fire, dropped
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Medium, "R4-spell");
        timeline.SetIdentity("Raidman", IdentityKind.Player, RuleStrength.Certain, "R3-presence");
        timeline.AddAffiliation(AffiliationKind.Friendly, "Illuminai", T0 + 100, T0 + 200, RuleStrength.Certain, "R9-charm");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("Illuminai", rows[0].Name);
        Assert.IsTrue(rows[0].CharmedOwned, "the list must say why a player name is here");
        Assert.AreEqual(77, rows[0].DamageTotal);
    }

    [TestMethod]
    public void CharmedMob_CountsAsPlayerOutput_TowardItsTargetsRow()
    {
        var facts = BuildFacts(("a skeleton", "BossX", 40, 150, LabelTypes.Melee));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("BossX", IdentityKind.Npc, RuleStrength.Strong, "R7-graph");
        timeline.SetIdentity("a skeleton", IdentityKind.Npc, RuleStrength.Strong, "R9-charm");
        timeline.AddAffiliation(AffiliationKind.Friendly, "a skeleton", T0 + 100, T0 + 200, RuleStrength.Certain, "R9-charm");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("BossX", rows[0].Name, "a charmed mob's swings are damage dealt TO its target's fight");
        Assert.AreEqual(40, rows[0].DamageToOwner);
        Assert.IsNull(Row(rows, "a skeleton"));
    }

    [TestMethod]
    public void StaticFriendlyStamp_DoesNotFlipSides()
    {
        // R5-called pets carry a (permanently open) Friendly interval: that is allegiance, not a
        // charm - the pet must stay player-side so its damage lands on the boss row, not on itself.
        var facts = BuildFacts(("Frobum", "BossX", 60, 10, LabelTypes.Melee));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Frobum", IdentityKind.Pet, RuleStrength.Certain, "R5-called");
        timeline.AddAffiliation(AffiliationKind.Friendly, "Frobum", T0, double.PositiveInfinity, RuleStrength.Certain, "R5-called");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("BossX", rows[0].Name);
    }

    // ---- anchoring and fallbacks ----

    [TestMethod]
    public void UnknownAttacker_BoundByItsPlayerVictim_BecomesTheRow()
    {
        var facts = BuildFacts(("Mystery", "Raidman", 33, 0, LabelTypes.Melee));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Raidman", IdentityKind.Player, RuleStrength.Certain, "R2-who");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("Mystery", rows[0].Name, "whatever beats a known player is the NPC of that exchange");
    }

    [TestMethod]
    public void UnknownAttacker_KnownNpcDefender_KeysTheBoss()
    {
        var facts = BuildFacts(("Mobpet", "BossX", 20, 0, LabelTypes.Melee));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("BossX", IdentityKind.Npc, RuleStrength.Strong, "R7-graph");

        // attacker unclassified, defender a known NPC: the exchange keys on the boss.
        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("BossX", rows[0].Name);
    }

    [TestMethod]
    public void MobVsMob_NoPlayerInvolved_StaysOutOfTheList()
    {
        var facts = BuildFacts(("BossA", "BossB", 20, 0, LabelTypes.Melee));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("BossA", IdentityKind.Npc, RuleStrength.Strong, "R7-graph");
        timeline.SetIdentity("BossB", IdentityKind.Npc, RuleStrength.Strong, "R7-graph");

        // Two NPC-side names with no player on either side is not a raid fight.
        Assert.AreEqual(0, FightProjection.Build(facts, timeline).Count);
    }

    [TestMethod]
    public void SameNameFarApartExchanges_OpenSeparateRows()
    {
        var facts = BuildFacts(
            ("Raidman", "Grul", 10, 0, LabelTypes.Melee),
            ("Raidman", "Grul", 10, FightProjection.EngagementGapS + 10, LabelTypes.Melee));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Raidman", IdentityKind.Player, RuleStrength.Certain, "R2-who");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(2, rows.Count, "re-engaging a boss hours later is a different fight");
        Assert.AreEqual(rows[0].Name, rows[1].Name);
    }

    [TestMethod]
    public void SlainLine_ClosesTheRowMarkedDead_AndRespawnOpensANewOne()
    {
        var facts = BuildFacts(
            ("Raidman", "Grul", 10, 0, LabelTypes.Melee),
            ("Raidman", "Grul", 12, 60, LabelTypes.Melee));   // same engagement (< gap)
        facts.AddDeath(new DeathFact(0, (long)(T0 + 5), facts.InternName("Grul"), -1));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Raidman", IdentityKind.Player, RuleStrength.Certain, "R2-who");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(2, rows.Count, "a respawn inside the time gap still splits at the slain line");
        Assert.IsTrue(rows[0].Dead);
        Assert.AreEqual(10, rows[0].DamageTotal, "damage after the death belongs to the next engagement");
        Assert.IsFalse(rows[1].Dead);
        Assert.AreEqual(12, rows[1].DamageTotal);
    }

    [TestMethod]
    public void DeathMarksTheProjectedRow()
    {
        var facts = BuildFacts(("Raidman", "Grul", 10, 0, LabelTypes.Melee));
        facts.AddDeath(new DeathFact(0, (long)(T0 + 5), facts.InternName("Grul"), -1));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Raidman", IdentityKind.Player, RuleStrength.Certain, "R2-who");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count);
        Assert.IsTrue(rows[0].Dead);
    }
}
