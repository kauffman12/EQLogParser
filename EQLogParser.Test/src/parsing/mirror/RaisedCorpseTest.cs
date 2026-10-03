using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * Corpses in the log, and what the mirror does with them: as little as possible.
 *
 * Three shapes exist, and only two of them ever appear in a fact.
 *
 *   `<player>'s corpse rises to serve <master>.`   (necro's Wake the Dead)
 *   The raised servant then attacks under ``<master>`s pet`` — the same name every other swarm pet uses, verified
 *   first-hand on the server (a necro called Kazcro: its risen corpses were `Kazcro`s pet`) and confirmed against
 *   six captures, where a master's possessive-pet share jumps 3.6x/8.7x in the 90 s after a raise burst while
 *   control pet owners move 1.0x-2.3x (docs/combat-mirror-design.md -> "Corpses need no rule"). Nothing in the
 *   log ever says which of those lines came from a corpse, so there is nothing here to interpret: R5 already
 *   cuts the owner off the name and credits the master, which is the outcome a raid meter wants.
 *
 *   `<player>'s corpse` as a DEFENDER — somebody is hitting it, so it is in the fight and stays in the list.
 *
 *   `<X>'s corpse` as a lingering effect source (2,201 lines / 488 M HP across six captures: boss poisons,
 *   drones). UpdateAttacker cuts the `'s corpse` off attackers before a record exists, so these arrive under the
 *   short name and are handled like any other unknown hostile.
 *
 * What must NOT happen is a rule that decides sides from the name shape, or one that mints a pet entry: the
 * names are shared with lingering boss damage and with players who are alive again by the next pull.
 */
[TestClass]
public class RaisedCorpseTest
{
    private static DamageFactTable Run(out EntityTimeline timeline, params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mirror-corpse-" + Guid.NewGuid().ToString("N")));
        var log = Path.Combine(dir.FullName, "eqlog_Corpseone_Eqgate.txt");   // filename seeds ConfigUtil.PlayerName
        File.WriteAllLines(log, lines);

        var facts = PipelineHarness.RunFileDerived(log).Facts;
        timeline = new EntityTimeline();
        ClassificationRules.Apply(facts, timeline);
        return facts;
    }

    private static List<string> RowNames(DamageFactTable facts, EntityTimeline timeline)
      => FightProjection.Build(facts, timeline).Select(r => r.Name).ToList();

    [TestMethod]
    public void ARaiseLineRecruitsNothing()
    {
        // The raise line plus the damage the servant actually writes, one second apart in spirit. Nothing in
        // this run mentions the corpse as a combatant, so no rule may put it anywhere: not on our side (that
        // would delete the fight against whatever it is really fighting), not into an identity, not into a row.
        var facts = Run(out var timeline,
            "[Sun Apr 26 18:48:00 2026] Suleka's corpse rises to serve Kazcro.",
            "[Sun Apr 26 18:48:05 2026] Kazcro`s pet hits an aetherial hydra for 700 points of damage.");

        Assert.AreEqual(1, facts.Facts.Length, "the raise line became a fact?");
        Assert.AreEqual(IdentityKind.Unknown, timeline.IdentityWithSource("Suleka's corpse", out var servantSrc),
            $"a raised corpse gained an identity (source {servantSrc ?? "none"})");
        // The master DOES become a player — but not from the raise line: from his own pet line, whose name
        // carries the ownership word (R5). Worth pinning, because it is the mechanism that credits the risen
        // corpses to him without any wake-the-dead rule at all.
        Assert.AreEqual(IdentityKind.Player, timeline.IdentityWithSource("Kazcro", out var masterSrc),
            $"the master was not learned from ``Kazcro`s pet`` (source {masterSrc ?? "none"})");

        CollectionAssert.AreEqual(new[] { "An aetherial hydra" }, RowNames(facts, timeline));
    }

    [TestMethod]
    public void TheServantsDamageIsTheMastersBecauseTheLogSaysSo()
    {
        // Where the credit actually lands: ``Kazcro`s pet`` carries its owner inside the name, which is the
        // shape R5 was built for, and the summary hands out AttackerOwner from it. This is the whole reason a
        // wake-the-dead rule is unnecessary — including the corpse's own name would only split one player's
        // output across two rows.
        var facts = Run(out var timeline,
            "[Sun Apr 26 18:48:05 2026] Kazcro`s pet hits an aetherial hydra for 700 points of damage.");

        var index = new FightFactIndex();
        var rows = FightProjection.Build(facts, timeline, index.OnFact);
        var actions = FightSummarySource.Build(rows, index, facts).Fights
            .SelectMany(f => f.DamageBlocks.SelectMany(b => b.Actions)).OfType<DamageRecord>().ToList();

        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual("Kazcro`s pet", actions[0].Attacker);
        Assert.AreEqual("Kazcro", actions[0].AttackerOwner);
    }

    [TestMethod]
    public void APlayerHittingANamedCorpseKeepsItInTheFightList()
    {
        // The one case worth keeping: raid members are swinging at it, so it is a combatant. UpdateDefender
        // leaves the suffix alone (only attackers are cut), so it is listed under the name the log wrote.
        var facts = Run(out var timeline,
            "[Sun Apr 26 18:49:00 2026] You hit Vexmaw's corpse for 500 points of damage.");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("Vexmaw's corpse", rows[0].Name);
        Assert.AreEqual(500, rows[0].DamageToOwner);   // aimed AT the corpse
    }

    [TestMethod]
    public void ACorpseEffectHittingUsIsAHostileNotSomebodysPet()
    {
        // The 488 M HP shape. The attacker loses its suffix in the parser, so what reaches the mirror is
        // `A remnant`: an unknown name damaging the local player, which is a hostile row and nothing else.
        var facts = Run(out var timeline,
            "[Sun Apr 26 18:49:00 2026] A remnant's corpse hits You for 1200 points of damage.");

        Assert.AreEqual(1, facts.Facts.Length);
        Assert.AreEqual("A remnant", facts.NameOf(facts.Facts[0].AtkIdx));

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("A remnant", rows[0].Name);
        Assert.AreEqual(1200, rows[0].DamageByOwner);   // the corpse effect dealt it to us
    }
}
