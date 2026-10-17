using EQLogParser;

namespace EQLogParser;

/*
 * Who gets the credit for a heal, shape by shape (issue #191). Reconciling the spreadsheet export against a real log
 * turned each of these from an opinion into a number on one 560k-line capture - `local/exports/eqlog_Kizant_xegony-selected.txt`:
 * pet self-heals 29,660 lines / 52,124,455 landed, spaced NPC healers 2,297 lines / 16,522,613, negative HOT ticks 84.
 * Every one of those lines used to be dropped for want of a healer name; the operator ruled on each case, and this file
 * is the ruling in testable form:
 *
 *   1. A pet's own tick is the PET's healing. `Mark of the Righteous` is a reverse damage shield the raid's cleric put
 *      on the pet; the cleric cast it long before the tick, often many ticks, and the player named in the pet's name
 *      did nothing at that moment. So the healer is the whole possessive name, and no owner substitution happens.
 *   2. A ward is the opposite case: `Nymera\s ward` is an item Nymera dropped in the raid, Nymera is its healer, and
 *      that fold lives at parse time so the board, the hit log, the overlay and the export cannot disagree.
 *   3. A mob that heals is its own healer. `a killer moth healed Shmid` is a proc off an NPC; a raid healer reads the
 *      spell column (Divine Interstition) to see the real cause, so keeping the literal name is honest, and a spaced
 *      name is not a reason to lose 16.5 M of healing.
 *   4. Nobody named, nobody credited: the passive (`Foob is healed by a reflected flame.`), the "has/have been healed"
 *      family, and the cast-less spell-subject line stay dropped - see IsSubjectIsSpell for why the last one may not
 *      take the spell's own name.
 *   5. EQ prints some HOT ticks negative and the target was HEALED by the magnitude, so the sign is stripped (HOT only:
 *      no other heal shape is known to print this way). The paren sitting below the landed amount must not post a
 *      negative into the overheal column either.
 */
[TestClass]
[DoNotParallelize]
public class HealCreditLawTest
{
    [TestInitialize]
    public void Setup()
    {
        RecordsStore.Instance.Clear(false);
        HealTap.Clear();
        PlayerRegistry.Instance.Clear();
        ConfigUtil.PlayerName = "Kizant";
    }

    [TestCleanup]
    public void Cleanup()
    {
        RecordsStore.Instance.Clear(false);
        HealTap.Clear();
        PlayerRegistry.Instance.Clear();
        ConfigUtil.PlayerName = "";
    }

    // ---- the shapes, one test each ---------------------------------------------------------

    [TestMethod]
    public void APetSelfHealIsCreditedToThePetAndNotToThePlayerInTheName()
    {
        Assert.IsTrue(Parse("Jazrakhan`s pet healed itself for 6109 hit points by Mark of the Righteous XIV Rk. III.", 10));
        var rec = Last();
        Assert.AreEqual("Jazrakhan`s pet", rec.Healer,
            "the tick is the pet's own - the owner only cast the shield, and the cleric cast it long before this");
        Assert.AreEqual("Jazrakhan`s pet", rec.Healed, "'itself' resolves to the subject of the sentence, which is the pet");
        Assert.AreEqual(6109u, rec.Total);
    }

    [TestMethod]
    public void AWardHealIsCreditedToThePlayerWhoDroppedIt()
    {
        Assert.IsTrue(Parse("Nymera`s ward healed Soddann for 0 (15000) hit points by Exquisite Restoration XIX.", 20));
        var rec = Last();
        Assert.AreEqual("Nymera", rec.Healer, "a ward is a thing the player placed; its healing is hers");
        Assert.AreEqual(0u, rec.Total, "the target was at full health: nothing landed");
        Assert.AreEqual(15000u, rec.OverTotal, "the paren is what the spell COULD have done - 0 (100) means 100 of overheal");
    }

    [TestMethod]
    public void AnNpcHealerKeepsItsSpacedName()
    {
        Assert.IsTrue(Parse("a killer moth healed Shmid for 852000 hit points by Divine Interstition Rk. III.", 30));

        // The reader capitalises a sentence's first letter, which is why the name arrives as "A killer moth".
        Assert.AreEqual("A killer moth", Last().Healer,
            "the proc belongs to the mob that owns it; the spell column tells the reader the real cause");

        Assert.IsTrue(Parse("Tallongast, The Egg healed Foob for 852000 hit points by Divine Interstition Rk. III.", 31));
        Assert.AreEqual("Tallongast, The Egg", Last().Healer, "a comma and a title are part of the name, not a sentence end");
    }

    [TestMethod]
    public void AnActivePerfectAuxiliaryIsNotPartOfTheHealerName()
    {
        Assert.IsTrue(Parse("The gods have healed Shmid for 852000 points of damage.", 40));
        Assert.AreEqual("The gods", Last().Healer, "'have' is the auxiliary; the actor is what precedes it");
    }

    [TestMethod]
    public void ANameThatIsOnlyAPatientNamesNoHealer()
    {
        // Passive: the subject is the healed, and the "by" clause is a cause, not a person.
        Assert.IsFalse(Parse("Foob is healed by a reflected flame for 5000 hit points by Reflection.", 50));

        // The "has been" / "have been" family has no actor in it at all.
        Assert.IsFalse(Parse("Rellam has been healed over time for 900 hit points by Roar of the Lion IX.", 51));
        Assert.IsFalse(Parse("You have been healed over time for 1063 (8211) hit points by Roar of the Lion 6.", 52));

        Assert.AreEqual(0, HealTap.All().Count, "a dropped line must not reach the tap either");
    }

    [TestMethod]
    public void ACastlessHealDoesNotTakeTheSpellsOwnName()
    {
        // Standing law (HealingLineParser.IsSentenceEnd): nobody is named on these, so nothing is stored - and a rank
        // tail must never mint a healer called "II" either.
        Assert.IsFalse(Parse("Bastion of Divinity healed Xxuro over time for 6670 hit points by Bastion of Divinity Effect.", 60));
        Assert.IsFalse(Parse("Bastion of Divinity Rk. II healed Xxuro over time for 6670 hit points by Bastion of Divinity Effect II.", 61));
        Assert.AreEqual(0, HealTap.All().Count);
    }

    [TestMethod]
    public void ANegativeHotTickIsStoredAsTheHealingItActuallyDid()
    {
        Assert.IsTrue(Parse("Tilwedarx healed Foob over time for -146133 (47344) hit points by Abundant Healing LXIX.", 70));
        var rec = Last();
        Assert.AreEqual(146133u, rec.Total, "EQ printed the tick negative; the target was healed by that magnitude");
        Assert.AreEqual(Labels.Hot, rec.Type);
        Assert.AreEqual(47344u, rec.OverTotal);

        // A minus on a DIRECT heal is not this shape and stays a line the parser does not understand.
        Assert.IsFalse(Parse("Tilwedarx healed Foob for -5000 hit points by Abundant Healing LXIX.", 71));
    }

    [TestMethod]
    public void OverhealCannotGoNegativeWhenTheParenSitsBelowTheLandedAmount()
    {
        var stats = new PlayerSubStats();
        StatsUtil.UpdateHealStats(stats, Heal(146133, 47344));

        Assert.AreEqual(146133, stats.Total, "the tick counts as the healing it did");
        Assert.AreEqual(0, stats.Extra, "OverTotal below Total is EQ's negative-tick print, not a negative overheal");
    }

    // ---- what a heal line may NOT conclude about a name ------------------------------------

    /*
     * A name on the HEALER side of a heal line is no evidence that it is a PERSON: `a killer moth healed Shmid`,
     * `The gods have healed Shmid`, and now 29,660 pet ticks per capture put non-player names in that seat. What the
     * rules do with them is the low-weight answer already: R15 reads heal evidence only from casters whose OWN identity
     * is raid-side at Strong, so a mob healer proves nothing about anybody (it cannot feed the healed name's verdict),
     * and being seen healing claims nothing for itself - this name lands as Npc from the shipped mob table, not from
     * anything the heal line said. Pinned because before these parser fixes the NPC names never reached the pool to be
     * asked about at all.
     */
    [TestMethod]
    public void ANameSeenOnlyAsAHealerClaimsNothingForItself()
    {
        var lines = new List<string>();
        for (var i = 0; i < 24; i++)
        {
            lines.Add($"[Sat Aug 01 10:{i / 60:00}:{i % 60:00} 2026] a killer moth healed Ryft for 8520 hit points by Divine Interstition Rk. III.");
            lines.Add($"[Sat Aug 01 10:{i / 60:00}:{(i + 30) % 60:00} 2026] a killer moth healed Shmid for 8520 hit points by Divine Interstition Rk. III.");
        }

        var run = RunDerive(lines.ToArray());
        var timeline = new EntityTimeline();
        ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

        var kind = timeline.IdentityWithSource("a killer moth", out var source);
        Assert.AreNotEqual(IdentityKind.Player, kind,
            $"a mob that heals people was claimed as a Player by {source} - being seen healing is not personhood");
        Assert.AreEqual(IdentityKind.Npc, kind, "the mob table claims it, which is the answer that has nothing to do with healing");
        Assert.AreNotEqual("R15-healed", source, "heal evidence is read from Strong raid-side casters, never from a mob's own ticks");
    }

    // ---- helpers --------------------------------------------------------------------------

    private static HealRecord Heal(uint total, uint overTotal) =>
        new() { Healer = "Tilwedarx", Healed = "Foob", Type = Labels.Hot, SubType = "Abundant Healing LXIX", Total = total, OverTotal = overTotal };

    private static bool Parse(string action, double beginTime) =>
        HealingLineParser.Process(new LineData { Action = action, BeginTime = beginTime });

    private static HealRecord Last()
    {
        var seen = HealTap.All();
        Assert.IsTrue(seen.Count > 0, "the parser accepted the line but fired no heal event");
        return seen[^1].Item2;
    }

    private static PipelineHarness.DeriveRunResult RunDerive(params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "derive-credit-" + Guid.NewGuid().ToString("N")));
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
