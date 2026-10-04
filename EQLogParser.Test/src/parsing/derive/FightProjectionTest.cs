using EQLogParser;

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
            facts.AddFact(new DamageFact(seq++, (long)(T0 + t), a, d, total: dmg, typeId: label,
              flags: 0, modMask: 0, subIdx: ushort.MaxValue));
        }
        return facts;
    }

    private static DerivedFight? Row(List<DerivedFight> rows, string name)
        => rows.FirstOrDefault(r => r.Name == name);

    // ---- row migration: the dynamic case the engine exists for ----

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

        // R4-spell arrives (same pass structure DeriveEngine runs): rebuild over the SAME facts.
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

    /*
     * A mob beating a name no rule placed is NOT mob-on-mob noise. Measured on eqlog_Incogitable_xegony.txt the names
     * in that bucket are raiders — `Worthless` takes 159 facts / 1,479,310 while spending 322 of its own swings on
     * `A cunning scrykin` and `A crazed flesh horror`, `Boner` 186 / 1,389,581, `Morris` 68 / 1,360,995 — and
     * `Ddread`, who the roster knows, lost 80 of her 85 incoming facts there because her registry verification
     * replays from mid-log. The assertion that matters is WHICH SIDE of the row the fact lands on: this damage was
     * not dealt by the raid, so it must reach the tanking half rather than the mob's own output.
     */
    [TestMethod]
    public void AMobBeatingAnUnplacedName_CountsAsDamageTaken_OnTheMobsRow()
    {
        var facts = BuildFacts(("A crazed flesh horror", "Worthless", 900, 0, LabelTypes.Melee));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("A crazed flesh horror", IdentityKind.Npc, RuleStrength.Medium, "R14-article");

        var index = new FightFactIndex(timeline);
        var rows = FightProjection.Build(facts, timeline, index.OnFact);

        Assert.AreEqual(1, rows.Count, "the raid's encounter with that mob is the row this belongs to");
        var mob = rows[0];
        Assert.AreEqual("A crazed flesh horror", mob.Name);
        Assert.AreEqual(0, mob.DamageToOwner, "nobody on our side dealt this");
        Assert.AreEqual(900, mob.DamageByOwner, "the mob's own swing");

        var tanking = index.TankingOrdinalsFor(mob);
        Assert.AreEqual(1, tanking.Count, "it has to be reachable as damage somebody received");
        Assert.AreEqual(0, index.DamageOrdinalsFor(mob).Count, "and never on the raid's output side");
        Assert.AreEqual("Worthless", facts.NameOf(facts.Facts[tanking[0]].DefIdx));
        Assert.AreEqual(0, index.UnroutedFactCount, "IsRaidVictimAt admits an unplaced victim by exclusion");
    }

    // The other half of that split: a defender the rules DO call an NPC is mob-on-mob noise and stays out.
    [TestMethod]
    public void AMobBeatingAnotherMob_StillAnnouncesNothing()
    {
        var facts = BuildFacts(("A crazed flesh horror", "A bone walker", 900, 0, LabelTypes.Melee));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("A crazed flesh horror", IdentityKind.Npc, RuleStrength.Medium, "R14-article");
        timeline.SetIdentity("A bone walker", IdentityKind.Npc, RuleStrength.Medium, "R14-article");

        var index = new FightFactIndex(timeline);
        Assert.AreEqual(0, FightProjection.Build(facts, timeline, index.OnFact).Count,
            "two mobs on each other is not a raid fight; admitting unplaced victims must not widen this");
        Assert.AreEqual(0, index.UnroutedFactCount);
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
        timeline.SetIdentity("Raidman", IdentityKind.Player, RuleStrength.Certain, "R3-joinraid");
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

    /*
     * A death may only close a row that was open when the death happened. The slain queue is keyed by NAME, and one
     * name carries many mobs in a night: on `eqlog_Kizant_xegony.txt` `A corrupted egg` is slain at 18:52:52, then
     * 18:52:56, then again at 18:52:56 while the raid is already swinging at the next egg that answers to that name.
     * Applying a queued death without asking whether its mob was ever alive inside the row killed a newborn row at its
     * own first second, and the grid printed two `A corrupted egg` rows both beginning 18:52:58 - one of them living
     * 0 seconds, holding the killing blow's damage while the row above it lost it (3.74M + 2.57M where legacy has one
     * row of 6.32M). 19 such rows on this capture.
     */
    [TestMethod]
    public void ADeathOlderThanARowCannotCloseIt()
    {
        var facts = BuildFacts(
            ("Raidman", "an egg", 10, 0, LabelTypes.Melee),
            ("Raidman", "an egg", 11, 5, LabelTypes.Melee),      // first holder of the name
            ("Raidman", "an egg", 12, 8, LabelTypes.Melee),      // the raid is already on the next one
            ("Raidman", "an egg", 13, 9, LabelTypes.Melee),
            ("Raidman", "an egg", 14, 12, LabelTypes.Melee));
        // Two of this name die in the same second; only the first of those can belong to a row that existed.
        facts.AddDeath(new DeathFact(0, (long)(T0 + 6), facts.InternName("an egg"), -1));
        facts.AddDeath(new DeathFact(0, (long)(T0 + 6), facts.InternName("an egg"), -1));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Raidman", IdentityKind.Player, RuleStrength.Certain, "R2-who");

        var rows = FightProjection.Build(facts, timeline);

        Assert.AreEqual(2, rows.Count, "two mobs of this name, two rows - not a third born dead");
        Assert.IsTrue(rows.All(r => r.EndTime > r.BeginTime),
                      "the stale line used to kill the new row at its own first second: 18:52:58 to 18:52:58");
        Assert.AreEqual(21, rows[0].DamageTotal, "the first egg: 10 + 11");
        Assert.AreEqual(12 + 13 + 14, rows[1].DamageTotal,
                        "the killing blow's damage was being handed to the row born behind it");
        Assert.IsTrue(rows[0].Dead, "the death that belonged to this row closed it");
        Assert.IsFalse(rows[1].Dead,
                       "and no death marker for the next holder: the only slain line for this name predates it, and "
                     + "manufacturing a kill out of that line is exactly what wrote the zero-length row");
    }

    [TestMethod]
    public void ANameReusedBySuccessiveMobs_ClosesOneRowPerDeath()
    {
        // The healthy shape of the same situation: each death arrives while its own mob's row is open.
        var facts = BuildFacts(
            ("Raidman", "an egg", 10, 0, LabelTypes.Melee),
            ("Raidman", "an egg", 11, 3, LabelTypes.Melee),
            ("Raidman", "an egg", 12, 5, LabelTypes.Melee),
            ("Raidman", "an egg", 13, 7, LabelTypes.Melee),
            ("Raidman", "an egg", 14, 9, LabelTypes.Melee),
            ("Raidman", "an egg", 15, 11, LabelTypes.Melee));
        facts.AddDeath(new DeathFact(0, (long)(T0 + 4), facts.InternName("an egg"), -1));
        facts.AddDeath(new DeathFact(0, (long)(T0 + 8), facts.InternName("an egg"), -1));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Raidman", IdentityKind.Player, RuleStrength.Certain, "R2-who");

        var rows = FightProjection.Build(facts, timeline);

        Assert.AreEqual(3, rows.Count, "two deaths hand out two boundaries, no more");
        Assert.IsTrue(rows[0].Dead && rows[0].EndReason == DerivedFightEnd.Slain && rows[0].DamageTotal == 21, "the first egg: 10 + 11");
        Assert.IsTrue(rows[1].Dead && rows[1].DamageTotal == 25, "the second egg: 12 + 13");
        Assert.IsFalse(rows[2].Dead, "the third is still alive at the end of the capture");
        Assert.AreEqual(14 + 15, rows[2].DamageTotal);
    }

    /*
     * The gap that splits rows is legacy's expiry (FightManager.FightTimeout = 30 s), and the number was 300 until
     * one pull was read off `local/eqlog_Kizant_xegony.txt`. `Waxwork Abolishion` is hit from 18:34:08 to 18:34:53,
     * the raid spends 135 s on the adds that came out of it, and the boss returns at 18:37:08 for a 162 s second
     * life. At 300 s that was ONE row of 342 s: one entry fewer in a list whose entire job is counting encounters,
     * and a duration of "5 minutes" on a fight that ran 46 seconds. The quiet cost was the DPS clock — legacy's
     * selection totalled 324 s of span for its three rows because TimeRange.Add drops silences of 6 s and over,
     * while the merged row's single span charged 343 s for the same click.
     *
     * Matching an event to a row (a slain line, a charm sighting) keeps the wider window: that is a different
     * question from "did this fight stop", and tightening it with the split silently un-closes real charm rows.
     */
    [TestMethod]
    public void ABossThatFadesWhileItsAddsDie_GetsOneRowPerLife()
    {
        var facts = BuildFacts(
            ("Raidman", "Waxwork Abolishion", 100, 8, LabelTypes.Melee),
            ("Raidman", "Waxwork Abolishion", 100, 30, LabelTypes.Melee),
            ("Raidman", "Waxwork Abolishion", 100, 53, LabelTypes.Melee),
            ("Raidman", "Waxwork Lancer", 100, 70, LabelTypes.Melee),        // the adds take over
            ("Raidman", "Waxwork Lancer", 100, 95, LabelTypes.Melee),
            ("Raidman", "Waxwork Lancer", 100, 120, LabelTypes.Melee),
            ("Raidman", "Waxwork Lancer", 100, 145, LabelTypes.Melee),
            ("Raidman", "Waxwork Lancer", 100, 170, LabelTypes.Melee),
            ("Raidman", "Waxwork Abolishion", 100, 196, LabelTypes.Melee),   // the boss respawns
            ("Raidman", "Waxwork Abolishion", 100, 226, LabelTypes.Melee),
            ("Raidman", "Waxwork Abolishion", 100, 256, LabelTypes.Melee),
            ("Raidman", "Waxwork Abolishion", 100, 286, LabelTypes.Melee));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Raidman", IdentityKind.Player, RuleStrength.Certain, "R2-who");

        var lives = FightProjection.Build(facts, timeline)
            .Where(r => r.Name == "Waxwork Abolishion").ToList();

        Assert.AreEqual(2, lives.Count, "a fade and a respawn are two encounters, not one five-minute one");
        Assert.AreEqual(45, lives[0].EndTime - lives[0].BeginTime, "the first life is the time it was hit, not the fade");
        Assert.AreEqual(DerivedFightEnd.Gap, lives[0].EndReason, "and it ends as a silence, not as a kill");
        Assert.AreEqual(90, lives[1].EndTime - lives[1].BeginTime, "and so is the second");
    }

    [TestMethod]
    public void AQuarterMinuteOfQuietIsStillTheSameFight()
    {
        // The other side of the 30 s number: pulling the split tighter would cut one continuous fight into
        // pieces every time a raid wipes its aggro list for half a minute, and each piece would be a row.
        var facts = BuildFacts(
            ("Raidman", "Grul", 100, 0, LabelTypes.Melee),
            ("Raidman", "Grul", 100, 25, LabelTypes.Melee));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Raidman", IdentityKind.Player, RuleStrength.Certain, "R2-who");

        Assert.AreEqual(1, FightProjection.Build(facts, timeline).Count);
    }

    /*
     * BeginDamageTime/LastDamageTime used to be stamped at row creation and never touched again, so every projected
     * row claimed a zero-length damage window equal to its own birth second. Sectionizer walks LastDamageTime for the
     * non-tanking divider list, so that was not cosmetic; and a row that was never hit has to say NaN, because "no
     * damage time" and "damage time of zero seconds" are different statements about a fight.
     */
    [TestMethod]
    public void ARowRemembersItsDamageTimeApartFromItsTankingTime()
    {
        var facts = BuildFacts(
            ("Raidman", "Grul", 50, 10, LabelTypes.Melee),    // the raid's output on Grul
            ("Grul", "Raidman", 500, 25, LabelTypes.Melee));  // what Grul did back
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Raidman", IdentityKind.Player, RuleStrength.Certain, "R2-who");

        var row = FightProjection.Build(facts, timeline).Single();
        Assert.AreEqual("Grul", row.Name);
        Assert.AreEqual(T0 + 10, row.BeginDamageTime, "damage time opens on the first hit LANDED ON this row");
        Assert.AreEqual(T0 + 10, row.LastDamageTime, "and does not follow the row's own swings");
        Assert.AreEqual(T0 + 25, row.BeginTankingTime, "the other direction keeps its own window");
        Assert.AreEqual(T0 + 25, row.LastTankingTime);
    }

    [TestMethod]
    public void ARowNobodyHit_HasNoDamageTimeRatherThanAFakeOne()
    {
        var facts = BuildFacts(("Grul", "Raidman", 500, 20, LabelTypes.Melee));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Raidman", IdentityKind.Player, RuleStrength.Certain, "R2-who");

        var row = FightProjection.Build(facts, timeline).Single();
        Assert.IsTrue(double.IsNaN(row.BeginDamageTime) && double.IsNaN(row.LastDamageTime),
            "a row that never took a hit cannot report its own birth second as damage time");
        Assert.IsFalse(double.IsNaN(row.LastTankingTime), "the direction that did happen is still timed");
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
    public void SameSecondKillingBlow_StaysInTheDeadRow_NoPhantomRow()
    {
        // The real shape (Waxwork Lancer, 2026-09-13 log): the last hits and the slain line all
        // carry the same second-resolution timestamp, the slain line LAST. Legacy folds them into
        // one dead fight; "dt <= t" used to split at the first same-second fact, orphaning the
        // killing blow into a zero-length live row behind the dead one.
        var facts = BuildFacts(
            ("Nniki", "Waxwork Lancer", 220465, 0, LabelTypes.Melee),
            ("Sancus", "Waxwork Lancer", 54797, 0, LabelTypes.Melee),
            ("Highwizard", "Waxwork Lancer", 7461640, 1, LabelTypes.Melee)); // killing blow, same second as the slain line
        facts.AddDeath(new DeathFact(3, (long)(T0 + 1), facts.InternName("Waxwork Lancer"), -1));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Nniki", IdentityKind.Player, RuleStrength.Certain, "R2-who");
        timeline.SetIdentity("Sancus", IdentityKind.Player, RuleStrength.Certain, "R2-who");
        timeline.SetIdentity("Highwizard", IdentityKind.Player, RuleStrength.Certain, "R2-who");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count, "no extra row behind the dead one");
        Assert.IsTrue(rows[0].Dead, "the slain line still marks the engagement dead");
        Assert.AreEqual(7736902, rows[0].DamageTotal, "the killing blow belongs to this fight");
        Assert.AreEqual((long)(T0 + 1), rows[0].LastTime);
    }

    [TestMethod]
    public void HitStrictlyAfterTheSlainSecond_StillOpensTheNextEngagement()
    {
        // The split boundary is the slain's second, not the slain line's position: a later
        // instance first struck one second after the death starts its own (live) row.
        var facts = BuildFacts(
            ("Nniki", "Waxwork Lancer", 220465, 0, LabelTypes.Melee),
            ("Highwizard", "Waxwork Lancer", 7461640, 1, LabelTypes.Melee),
            ("Nniki", "Waxwork Lancer", 900, 2, LabelTypes.Melee));       // next instance
        facts.AddDeath(new DeathFact(2, (long)(T0 + 1), facts.InternName("Waxwork Lancer"), -1));
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Nniki", IdentityKind.Player, RuleStrength.Certain, "R2-who");
        timeline.SetIdentity("Highwizard", IdentityKind.Player, RuleStrength.Certain, "R2-who");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(2, rows.Count);
        Assert.IsTrue(rows[0].Dead);
        Assert.AreEqual(7682105, rows[0].DamageTotal, "everything up to and including the slain second stays in the dead row");
        Assert.IsFalse(rows[1].Dead);
        Assert.AreEqual(900, rows[1].DamageTotal);
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
