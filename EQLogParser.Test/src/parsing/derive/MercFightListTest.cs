using System.Collections.Generic;

using EQLogParser;

namespace EQLogParser;

/*
 * The consequence, asserted where it lands: a name the bot table places is never a fight, and its damage and healing are
 * counted like a player's (docs/DesignNotes.md → "The bot table in `data/mercs.txt`").
 *
 * Both halves come from ONE thing — `IdentityKind.Merc` was already player-side everywhere the app asks (`IsPlayerSide`,
 * `IsConfirmedRaidPersonAt`, and FightSummarySource's attacker naming) — so this file asserts that no door in the fight
 * list or the roll-up has quietly decided a mercenary is a mob. Before R27 these names read NPC (their article, or the
 * creature list), which put them in the fight list as opponents AND dropped their swings as mob-on-mob noise: the operator
 * lost the bot's output and gained a fake encounter, in the same row.
 *
 * These tests set the identity directly at `ClassificationRules.MercDatabaseStrength` rather than deriving it: that the
 * rule emits exactly this claim from a row of mercs.txt is MercDatabaseTierTest's job. Split that way, a failure here
 * names the door that broke rather than the rule.
 */
[TestClass]
public class MercFightListTest
{
    private const double T0 = FixtureTime.Base;

    private const string Bot = "a hired bazu";
    private const string Mob = "Kurn the Sentry";

    private static DamageFactTable BuildFacts(params (string Atk, string Def, uint Dmg, double T)[] rows)
    {
        var facts = new DamageFactTable(64);
        var seq = 0;
        foreach (var (atk, def, dmg, t) in rows)
        {
            var a = facts.InternName(atk);
            var d = facts.InternName(def);
            facts.AddFact(new DamageFact(seq++, (long)(T0 + t), a, d, total: dmg, typeId: LabelTypes.Melee,
              flags: 0, modMask: 0, subIdx: ushort.MaxValue));
        }
        return facts;
    }

    private static EntityTimeline BotAsMerc(EntityTimeline? timeline = null)
    {
        timeline ??= new EntityTimeline();
        timeline.SetIdentity(Bot, IdentityKind.Merc, ClassificationRules.MercDatabaseStrength, "R27-mercdb");
        timeline.SetIdentity(Mob, IdentityKind.Npc, RuleStrength.Medium, "R14-shape");
        return timeline;
    }

    // ---- the bot's own output ----

    /*
     * The bot's swing belongs to the mob's fight and is credited to the bot. This is the damage that used to vanish: an
     * NPC-named attacker hitting an NPC-named defender is mob-on-mob noise, and TryCombatSides refuses the fact before any
     * row is opened.
     */
    [TestMethod]
    public void ABotSwingingAtAMobIsRaidDamage_CreditedToTheBot_OnTheMobsRow()
    {
        var facts = BuildFacts((Bot, Mob, 400, 0));
        var rows = FightProjection.Build(facts, BotAsMerc());

        Assert.AreEqual(1, rows.Count);
        var mob = rows[0];
        Assert.AreEqual(Mob, mob.Name, "the encounter the damage landed on owns the row");

        // Direction is named from the ROW'S ANCHOR, not from our side: the mob received this, so it sits in
        // DamageToOwner while PlayerRollup carries who dealt it. Getting these two the wrong way round reads as a
        // meter that works and disagrees with the grid.
        Assert.AreEqual(400, mob.DamageToOwner, "the bot's swing landed on the encounter");
        Assert.AreEqual(0, mob.DamageByOwner);

        /*
         * Rolled up under the INTERNED shape — `InternName` stores CapitalizeFirst, so the file's `a hired bazu` reaches a
         * board as "A hired bazu". Identity lookups ignore case; these dictionaries do not, which is why the key is read off
         * the fact rather than typed.
         */
        var bot = facts.NameOf(facts.Facts[0].AtkIdx);
        Assert.IsTrue(mob.PlayerRollup.TryGetValue(bot, out var agg) && agg.Damage == 400,
            $"a mercenary's number reads beside a player's, not folded away (roll-up keys: [{string.Join(", ", mob.PlayerRollup.Keys)}])");
    }

    // ---- the bot is never an encounter ----

    /*
     * THE ASK. A mob beating the bot is that mob's fight, and the bot's name opens nothing of its own — while the hit
     * itself still reaches the tanking half, because a mercenary taking a swing is one of ours being beaten on
     * (`IsConfirmedRaidPersonAt` counts Merc with Player).
     */
    [TestMethod]
    public void ABotBeingHitOpensNoRowOfItsOwn_ButCountsAsDamageReceived()
    {
        var facts = BuildFacts((Mob, Bot, 700, 0));
        var index = new FightFactIndex();
        var rows = FightProjection.Build(facts, BotAsMerc(), index.OnFact);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(Mob, rows[0].Name);
        Assert.IsNull(rows.Find(r => string.Equals(r.Name, Bot, StringComparison.OrdinalIgnoreCase)),
            "a bot is never a fight in the list");

        // See the direction note above: this row is keyed on the mob, so the mob's own swing is DamageByOwner and the
        // raid's share of the engagement is nil.
        Assert.AreEqual(700, rows[0].DamageByOwner);
        Assert.AreEqual(0, rows[0].DamageToOwner);

        var tanking = index.TankingOrdinalsFor(rows[0]);
        Assert.AreEqual(1, tanking.Count, "the hit has to be reachable as damage somebody received");

        // Ignore-case: the fact stores the interned shape (CapitalizeFirst), see the roll-up note above.
        Assert.AreEqual(Bot, facts.NameOf(facts.Facts[tanking[0]].DefIdx), ignoreCase: true);
    }

    /*
     * Both halves of one pull at once: the bot swings, the mob answers. One row, the bot's output on the raid side and
     * its own wound on the received side — a player's shape of numbers, which is what "treated similar to players" buys.
     */
    [TestMethod]
    public void ABotInAPullProducesOneRow_WithItsOutputAndItsWound()
    {
        var facts = BuildFacts((Bot, Mob, 400, 0), (Mob, Bot, 700, 1));
        var rows = FightProjection.Build(facts, BotAsMerc());

        Assert.AreEqual(1, rows.Count, "one engagement, and the bot is not half of it twice");
        Assert.AreEqual(Mob, rows[0].Name);
        Assert.AreEqual(400, rows[0].DamageToOwner, "what the raid (bot included) put on the mob");
        Assert.AreEqual(700, rows[0].DamageByOwner, "and what the mob put back");
    }

    /*
     * The same facts with the SAME NAME read as an enemy — the state before the bot table, when its article placed it NPC
     * at Medium. Nothing is credited and no row for the mob is opened either: two mobs on each other is not a raid fight.
     * Kept as a test because it is the number the operator was missing, not a hypothetical.
     */
    [TestMethod]
    public void TheSameFactsWithTheNameReadAsAnEnemy_VanishEntirely()
    {
        var facts = BuildFacts((Bot, Mob, 400, 0));
        var timeline = new EntityTimeline();
        timeline.SetIdentity(Bot, IdentityKind.Npc, RuleStrength.Medium, "R14-shape");
        timeline.SetIdentity(Mob, IdentityKind.Npc, RuleStrength.Medium, "R14-shape");

        Assert.AreEqual(0, FightProjection.Build(facts, timeline).Count,
            "mob-on-mob noise is dropped; this is the hole the bot table closes");
    }

    /*
     * Friendly fire between our own side stays dropped with a mercenary in it exactly as it does with a player or a pet
     * (DamageFactTable's policy: damage our own side earns nothing). The bot table must not turn a bot into a target worth
     * metering when the raid's own AoE splashes over it.
     */
    [TestMethod]
    public void ABotFriendlyFiredEarnsNobodyAnything()
    {
        var facts = BuildFacts(("Aastrid", Bot, 250, 0));
        var timeline = BotAsMerc();
        timeline.SetIdentity("Aastrid", IdentityKind.Player, RuleStrength.Certain, "R1-target");

        Assert.AreEqual(0, FightProjection.Build(facts, timeline).Count);
    }
}
