using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * R16 — raised servants: "Suleka's corpse rises to serve Coas." (the necro ability Wake the Dead).
 *
 * Two properties hold this together.
 *
 * Ownership belongs to the WINDOW, never to the name. A corpse can be raised again later by the same necro
 * or a different one (measured: `Nniki's corpse` raised twice in one capture, 5,769 s apart), so a name->owner
 * map would let whichever master read the log last claim every servant that ever wore that name — and credit
 * the first one's damage to the second. The window runs from its raise line until that corpse is raised again
 * or dies ("<name> wordlessly falls in battle."), which are the only two endings the log writes.
 *
 * The corpse NAME proves nothing, so only a raise line may open a window. "X's corpse" is also 2,201 lines /
 * 488 M HP of lingering boss damage (scalewrought blood poison, knife flurry, drones) and a few respawning husk
 * mobs whose names happen to end that way — none of those names was ever raised, and a name-shape rule would
 * have moved all of that output onto our side.
 *
 * What the boards show is one entry per master — `Coas`s pets` — in the same shape as a swarm pet, so the
 * existing owner cut folds it onto Coas's own row. The fact table keeps the name the log wrote.
 */
[TestClass]
public class ServantWindowTest
{
    private const double T0 = 10_000;

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "mini-data", "mirror", "rules-fixture.txt");

    private static double At(string clock) => DateUtil.StandardDateToDotNetSeconds($"[Sun Apr 26 {clock} 2026] x");

    private static DamageFactTable BuildFacts(params (string Atk, string Def, uint Dmg, double T)[] rows)
    {
        var facts = new DamageFactTable(64);
        var seq = 0;
        foreach (var (atk, def, dmg, t) in rows)
        {
            facts.AddFact(new DamageFact(seq++, (long)(T0 + t), facts.InternName(atk), facts.InternName(def),
                total: dmg, typeId: LabelTypes.Melee, flags: 0, modMask: 0, subIdx: ushort.MaxValue));
        }
        return facts;
    }

    private static void AddRaise(DamageFactTable facts, string servant, string owner, double t)
        => facts.AddEvidence(new EvidenceFact(1_000 + facts.EvidenceCount, (long)(T0 + t),
            facts.InternName(servant), EvidenceFact.EvServantRaise, facts.InternAux(owner)));

    private static void AddFall(DamageFactTable facts, string name, double t)
        => facts.AddEvidence(new EvidenceFact(1_000 + facts.EvidenceCount, (long)(T0 + t),
            facts.InternName(name), EvidenceFact.EvFallsInBattle));

    [TestMethod]
    public void TwoRaisesOfOneCorpse_OwnershipIsPerWindowNotPerName()
    {
        var facts = new DamageFactTable(64);
        AddRaise(facts, "Vexmaw's corpse", "Coaswin", 0);
        AddRaise(facts, "Vexmaw's corpse", "Mortuus", 90);

        var timeline = new EntityTimeline();
        var outcome = ClassificationRules.Apply(facts, timeline);

        Assert.AreEqual(2, outcome.Servants.Count, "one entry per raise line, not one per name");
        Assert.AreEqual("Coaswin", outcome.Servants[0].Owner);
        Assert.AreEqual("Mortuus", outcome.Servants[1].Owner);

        // The re-raise is the closer: 90 s of service, then a different master from that second on.
        Assert.AreEqual(90, outcome.Servants[0].ToS - outcome.Servants[0].FromS, 0.001);
        Assert.AreEqual("Coaswin", timeline.PetOwnerAt("Vexmaw's corpse", T0 + 10, out var src));
        Assert.AreEqual("R16-servant", src);
        Assert.AreEqual(AffiliationKind.PetOfPlayer, timeline.AffiliationAt("Vexmaw's corpse", T0 + 10, out _));
        Assert.AreEqual("Mortuus", timeline.PetOwnerAt("Vexmaw's corpse", T0 + 90, out _));

        // The last raise has no ending in the log at all, so it runs to the end of the capture — and with no
        // other line ever naming that corpse (measured over six captures) there is nothing to misattribute.
        Assert.IsTrue(double.IsPositiveInfinity(outcome.Servants[1].ToS));
        Assert.AreEqual("Mortuus", timeline.PetOwnerAt("Vexmaw's corpse", T0 + 90_000, out _));

        // Before the first raise: nobody's.
        Assert.IsNull(timeline.PetOwnerAt("Vexmaw's corpse", T0 - 1, out _));
    }

    [TestMethod]
    public void FallingInBattle_ClosesTheWindowItWasRaisedInto()
    {
        var facts = new DamageFactTable(64);
        AddRaise(facts, "A malicious husk's corpse", "Rhot", 0);
        AddFall(facts, "A malicious husk's corpse", 120);

        var timeline = new EntityTimeline();
        var outcome = ClassificationRules.Apply(facts, timeline);

        Assert.AreEqual(120, outcome.Servants[0].ToS - outcome.Servants[0].FromS, 0.001);
        Assert.AreEqual("Rhot", timeline.PetOwnerAt("A malicious husk's corpse", T0 + 60, out _));
        Assert.IsNull(timeline.PetOwnerAt("A malicious husk's corpse", T0 + 120, out _));

        // The same husk mob respawns and is raised by somebody else: a new window, a new master. Nothing
        // about the earlier one leaks forward, which is the whole reason ownership is not stored per name.
        AddRaise(facts, "A malicious husk's corpse", "Coaswin", 300);
        var second = new EntityTimeline();
        ClassificationRules.Apply(facts, second);
        Assert.IsNull(second.PetOwnerAt("A malicious husk's corpse", T0 + 200, out _));
        Assert.AreEqual("Coaswin", second.PetOwnerAt("A malicious husk's corpse", T0 + 301, out _));

        // And a fall line names nothing on its own — it is only ever a closer.
        var loneFacts = new DamageFactTable(64);
        AddFall(loneFacts, "a nameless husk's corpse", 0);
        Assert.AreEqual(0, ClassificationRules.Apply(loneFacts, new EntityTimeline()).Servants.Count);
    }

    [TestMethod]
    public void RaisedServant_IsOurSideOnlyWhileOwned()
    {
        // The servant hits a verified raider. While owned that is friendly fire and the fact is dropped; once
        // it has fallen in battle the same line is a hostile and opens its own row.
        var facts = BuildFacts(
            ("Vexmaw's corpse", "Illuminai", 400, 10),
            ("Vexmaw's corpse", "Illuminai", 500, 60));
        AddRaise(facts, "Vexmaw's corpse", "Coaswin", 0);
        AddFall(facts, "Vexmaw's corpse", 30);

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Certain, "R1-target");
        ClassificationRules.Apply(facts, timeline);

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count, "the owned hit is friendly fire; only the post-death one is a hostile");
        Assert.AreEqual("Vexmaw's corpse", rows[0].Name);
        Assert.AreEqual(500, rows[0].DamageByOwner);
    }

    [TestMethod]
    public void CorpseNameNeverRaised_IsNotSomebodysPet()
    {
        // Identical name shape, no raise line. Without this guard the same wording that carries 488 M HP of
        // lingering boss damage becomes somebody's pet and our side gains half a billion hit points.
        var facts = BuildFacts(("a rotting hyena's corpse", "Illuminai", 900, 10));

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Certain, "R1-target");
        var outcome = ClassificationRules.Apply(facts, timeline);

        Assert.AreEqual(0, outcome.Servants.Count);
        Assert.IsNull(timeline.PetOwnerAt("a rotting hyena's corpse", T0 + 10, out _));

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("a rotting hyena's corpse", rows[0].Name);
        Assert.AreEqual(900, rows[0].DamageByOwner);
    }

    [TestMethod]
    public void Fixture_RaisesCloseOnReRaiseAndOnDeath_AndClaimNoIdentity()
    {
        Assert.IsTrue(File.Exists(FixturePath), $"missing fixture: {FixturePath}");

        var run = PipelineHarness.RunFileWithMirror(FixturePath);
        var timeline = new EntityTimeline();
        var outcome = ClassificationRules.Apply(run.Facts, timeline);

        Assert.AreEqual(3, outcome.Servants.Count, "the fixture's raise lines produced no evidence — tap not wired?");
        Assert.AreEqual(("Vexmaw's corpse", "Coaswin"), (outcome.Servants[0].Servant, outcome.Servants[0].Owner));
        Assert.AreEqual(90, outcome.Servants[0].ToS - outcome.Servants[0].FromS, 0.001, "closed by the re-raise");
        Assert.AreEqual(("Zorben's corpse", "Coaswin"), (outcome.Servants[1].Servant, outcome.Servants[1].Owner));
        Assert.IsTrue(double.IsPositiveInfinity(outcome.Servants[1].ToS), "no ending of any kind was written");
        Assert.AreEqual(("Vexmaw's corpse", "Mortuus"), (outcome.Servants[2].Servant, outcome.Servants[2].Owner));
        Assert.AreEqual(30, outcome.Servants[2].ToS - outcome.Servants[2].FromS, 0.001, "closed by \"falls in battle\"");

        // Neither side of the line claims an identity: a raised corpse is not a person, and naming a master is
        // not proof the master is a player (NPCs raise corpses too — Rhot did exactly that).
        foreach (var name in new[] { "Vexmaw's corpse", "Zorben's corpse", "Coaswin", "Mortuus" })
        {
            Assert.AreEqual(IdentityKind.Unknown, timeline.IdentityWithSource(name, out _), $"{name} gained an identity from a raise line");
        }
    }

    [TestMethod]
    public void Fixture_ServantDamageReadsAsOnePetEntryPerMaster()
    {
        var run = PipelineHarness.RunFileWithMirror(FixturePath);
        var timeline = new EntityTimeline();
        ClassificationRules.Apply(run.Facts, timeline);

        var index = new MirrorDamageIndex();
        var rows = FightProjection.Build(run.Facts, timeline, index.OnFact);
        // The parser capitalises a name at the start of a line, so the row key is "An aetherial hydra".
        var hydraRows = rows.Where(r => "an aetherial hydra".Equals(r.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.IsTrue(hydraRows.Count > 0, "no hydra row to read the servant damage out of");

        var totals = new Dictionary<string, (string Owner, long Total)>();
        foreach (var fight in MirrorSummaryFights.Build(hydraRows, index, run.Facts, timeline).Fights)
        {
            foreach (var block in fight.DamageBlocks)
            {
                foreach (var action in block.Actions)
                {
                    if (action is not DamageRecord record) continue;
                    var key = record.Attacker ?? "?";
                    totals.TryGetValue(key, out var seen);
                    totals[key] = (record.AttackerOwner, seen.Total + record.Total);
                }
            }
        }

        // Both of Coaswin's corpses arrive as ONE entry, owned by Coaswin — which is what lets the boards fold
        // it onto his row with the pet cut they already apply.
        Assert.AreEqual(("Coaswin", 1000L), totals["Coaswin`s pets"]);

        // The re-raise belongs to the next master...
        Assert.AreEqual(("Mortuus", 100L), totals["Mortuus`s pets"]);

        /*
         * ...and Vexmaw's own two hits stay his. One is the corpse after it fell back down (window over), one is
         * the man himself swinging beside his own raised body at 18:48:07 — both arrive at the fact table under
         * the identical stripped name, so this is the assertion that keeps a resurrected raider's damage from
         * being recruited into whoever last woke his corpse. The flag on the fact (FlagCorpseAttacker) is the
         * only thing that tells them apart, and outside the window it is not enough on its own.
         */
        Assert.AreEqual((null, 305L), totals["Vexmaw"]);
        Assert.IsFalse(totals.ContainsKey("Vexmaw's corpse"));
    }
}
