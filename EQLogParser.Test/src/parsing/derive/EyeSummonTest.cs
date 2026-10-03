using EQLogParser;

namespace EQLogParser;

/*
 * R19 - the eye a player summons (docs/combat-mirror-design.md → "An eye is not a combatant").
 *
 * The client names the summon after whoever called it: `Eye of Shennron`, written in the same slot a mob name
 * goes, with no possessive and no owner line. Eight captures 2022-2026 (~4.2 GB) give:
 *
 *   never an attacker            0 lines in any capture - the eye does nothing but stand there;
 *   only its own name ever hits it  every damage line names the owner as the striker (Shennron→`Eye of Shennron`,
 *                                Coas, Soell, Reisil), and every single one lands for exactly 1 point:
 *                                348 lines, 348 points of damage in total across all eight files;
 *   and it dies at once          `Eye of X has been slain by X!` / `was slain by X!`, plus the flavour deaths
 *                                `is rent by decrepit wrath.` / `looks pale.` - 376 death lines.
 *
 * Two laws come out of that, and they are not the same law:
 *
 *   WHO STRUCK IT IS EVIDENCE    hitting (or killing) the eye that carries YOUR name means you own a summon, and
 *                                only a player character summons one. That is R19, Strong, so a `Targeted (Player)`
 *                                or `/who` verdict on the same name still outranks it.
 *   WHO DID NOT STRIKE IT IS NOT anyone can swing at somebody else's eye - 5 of those 376 death lines name a killer
 *                                who is not the owner (`Eye of Culoo has been slain by Tolzol`) - so a foreign blade
 *                                claims nothing, and no rule may read "killed an eye" as "owns an eye".
 *
 * Cold mode throughout: no registry seed, so every verdict below is attributable to one rule.
 */
[TestClass]
[DoNotParallelize]
public class EyeSummonTest
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

    // ---- the shape ----

    /*
     * The prefix is the whole vocabulary, and the owner is what follows it. Deliberately loose about the suffix
     * (any non-empty text): the OWNER claim only ever fires when an entity attacked with exactly that string, so a
     * multi-word or punctuated suffix simply never matches instead of inventing a raider.
     */
    [TestMethod]
    public void TheEyeShapeNamesItsOwner()
    {
        Assert.AreEqual("Shennron", ClassificationRules.EyeSummonOwnerInName("Eye of Shennron"));
        Assert.AreEqual("Coas", ClassificationRules.EyeSummonOwnerInName("Eye of Coas"));

        // Entity names are looked up without case everywhere else, and this is one of them.
        Assert.AreEqual("Shennron", ClassificationRules.EyeSummonOwnerInName("eye of Shennron"));

        // Not eyes: an article in front is a different name, "Eyeless" merely shares the letters, and a prefix with
        // nothing behind it names no summon.
        Assert.IsNull(ClassificationRules.EyeSummonOwnerInName("The Eye of Shennron"));
        Assert.IsNull(ClassificationRules.EyeSummonOwnerInName("Eyeless Hunter"));
        Assert.IsNull(ClassificationRules.EyeSummonOwnerInName("Eye of "));
        Assert.IsNull(ClassificationRules.EyeSummonOwnerInName(string.Empty));
    }

    /*
     * Three eyes are worth counting and stay mobs, so the shape can never swallow a boss's eye whole. The list is
     * legacy's own guard (DamageLineParser.InIgnoreList kept exactly these three out) and it is closed: a fourth
     * arrives with this test, not with a wider regex.
     */
    [TestMethod]
    public void TheCountableEyeListIsThreeWordsNoMore()
    {
        Assert.IsNull(ClassificationRules.EyeSummonOwnerInName("Eye of Veeshan"));
        Assert.IsNull(ClassificationRules.EyeSummonOwnerInName("Eye of Despair"));
        Assert.IsNull(ClassificationRules.EyeSummonOwnerInName("Eye of Mother"));

        var run = RunDerive(
            "[Mon May 04 18:50:01 2026] Targeted (NPC): Eye of Veeshan",
            "[Mon May 04 18:50:02 2026] Bulgar hits Eye of Veeshan for 4400 points of damage.",
            "[Mon May 04 18:50:12 2026] Bulgar hits Eye of Veeshan for 4400 points of damage.");

        Assert.AreEqual(2, run.Facts.FactCount, "a countable eye got swallowed by the ignore");
    }

    // ---- R19: the owner claim ----

    [TestMethod]
    public void StrikingYourOwnEyeCallsYouAPlayer()
    {
        var run = RunDerive(EyeFightLines().ToArray());
        AssertIdentity(Cold(run), "Shennron", IdentityKind.Player, "R19-eyeowner");

        // The eye itself never became a name to classify at all, which is the difference between "ignored" and
        // "known and Unknown": no row, nothing to click, nothing for a weaker rule to guess about later.
        Assert.IsFalse(HasEyeName(run.Facts.InternedNames), "an eye reached the name pool");
    }

    /*
     * Anyone can swing at somebody else's eye, so Tolzol gets nothing from this - not even a hint. He stays
     * Unknown, because the file says only that he hit a summon, and raid AoE does that to pets and mobs alike.
     */
    [TestMethod]
    public void AForeignBladeOnAnEyeClaimsNothing()
    {
        var timeline = Cold(RunDerive(
            "[Mon May 04 18:50:01 2026] Tolzol hit Eye of Shennron for 1 points of poison damage by Call for Blood XIII Rk. III.",
            "[Mon May 04 18:50:02 2026] Eye of Shennron was slain by Tolzol!"));

        Assert.AreEqual(IdentityKind.Unknown, timeline.Identity("Tolzol"),
            "hitting somebody else's eye was read as ownership");
    }

    /*
     * The claim needs the name inside the eye and the striker to be that name. These two lines are the same fight
     * with the names swapped, and neither raider is claimed by anyone: no `Eye of A` ever met `A`.
     */
    [TestMethod]
    public void TheClaimNeedsTheNameInsideTheEyeNotJustAnyEye()
    {
        var timeline = Cold(RunDerive(
            "[Mon May 04 18:50:01 2026] Aldiris hit Eye of Brytt for 1 points of magic damage by Rending of Ulnaa Rk. III.",
            "[Mon May 04 18:50:02 2026] Brytt hit Eye of Aldiris for 1 points of magic damage by Rending of Ulnaa Rk. III."));

        Assert.AreEqual(IdentityKind.Unknown, timeline.Identity("Aldiris"));
        Assert.AreEqual(IdentityKind.Unknown, timeline.Identity("Brytt"));
    }

    // ---- the accounting ----

    /*
     * An eye is not a combatant, in either stream. The point its owner's damage-over-time lands for must not
     * become raid output, the wasted heal on it must not become healer output, and its death must not dead-mark
     * anything - while the fight it happened inside keeps every real number.
     */
    [TestMethod]
    public void AnEyeNeverEntersEitherFactStream()
    {
        var run = RunDerive(EyeFightLines().ToArray());

        Assert.IsFalse(HasEyeName(run.Facts.InternedNames), "an eye reached the damage name pool");
        Assert.AreEqual(0, run.Facts.DeathCount, "an eye's death was captured");

        // Bulgar's two real hits on the sentinel are the whole damage story: neither Shennron's 1-point ticks on
        // his own eye nor anyone else's count.
        double total = 0;
        foreach (var f in run.Facts.Facts) total += f.Total;
        Assert.AreEqual(8800d, total, "eye damage reached the fact table");

        // The heal that landed on the eye is gone; the one that landed on a raider is not.
        double healed = 0;
        foreach (var h in run.HealFacts!.Heals) healed += h.Total;
        Assert.AreEqual(5000d, healed, "a heal spent on an eye reached the heal facts");
    }

    /*
     * The ledger remembers event-shaped rules only, and R19 reads an event: a summon is not in npcs.txt and not
     * in the name's spelling, so a later log may well never say it again.
     */
    [TestMethod]
    public void AnEyeOwnerIsWorthRemembering()
    {
        Assert.IsTrue(IdentityPriorStore.WorthRemembering("R19-eyeowner"));
    }

    // ---- fixture ----

    /*
     * One pull, verbatim shapes from local/eqlog_Kizant_xegony.txt (2026) and eqlog_Kizant_xegony-9-18-22.txt.
     * The eye lines sit in the middle of a real fight so a test cannot pass because nothing was parsed.
     */
    private static IEnumerable<string> EyeFightLines()
    {
        // Every eye line below is copied shape-for-shape out of local/eqlog_Kizant_xegony.txt: the strike is written
        // in the past tense and carries its spell ("hit ... for 1 points of poison damage by <spell> Rk. III."),
        // which is a different parser branch from "hits ... for N points of damage." — both have to be refused, and
        // only one of them was what the first version of this fixture exercised.
        yield return "[Mon May 04 18:50:00 2026] Shennron begins casting Eye of Zomm.";
        yield return "[Mon May 04 18:50:01 2026] Bulgar hits a frostbound sentinel for 4400 points of damage.";
        yield return "[Mon May 04 18:50:02 2026] Shennron hit Eye of Shennron for 1 points of poison damage by Call for Blood XIII Rk. III.";
        yield return "[Mon May 04 18:50:03 2026] Cozi healed Eye of Shennron for 0 (42909) hit points by Spiritual Shower Rk. III.";
        yield return "[Mon May 04 18:50:04 2026] Eye of Shennron is rent by decrepit wrath.";
        yield return "[Mon May 04 18:50:05 2026] Eye of Shennron was slain by Shennron!";
        yield return "[Mon May 04 18:50:06 2026] Cozi healed Bulgar for 5000 (42909) hit points by Spiritual Shower Rk. III.";
        yield return "[Mon May 04 18:50:07 2026] Bulgar hits a frostbound sentinel for 4400 points of damage.";
    }

    private static bool HasEyeName(IReadOnlyList<string> names) =>
        names.Any(n => n.StartsWith("Eye of", StringComparison.OrdinalIgnoreCase));

    private static EntityTimeline Cold(PipelineHarness.DeriveRunResult run)
    {
        var timeline = new EntityTimeline();
        ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
        return timeline;
    }

    private static PipelineHarness.DeriveRunResult RunDerive(params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "derive-eye-" + Guid.NewGuid().ToString("N")));
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

    private static void AssertIdentity(EntityTimeline timeline, string name, IdentityKind expected, string expectedSource)
    {
        var kind = timeline.IdentityWithSource(name, out var source);
        Assert.AreEqual(expected, kind, $"{name}: expected {expected}, got {kind} ({source ?? "no assignment"})");
        Assert.AreEqual(expectedSource, source, $"{name}: wrong rule got there first");
    }

}
