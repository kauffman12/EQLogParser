using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * R9 charm windows: an OPEN window with state-based closes (CharmWindowPolicy).
 *
 * The log gives one fact — "<npc> has been charmed." — and no bookkeeping. Charm expiry is never
 * written: eqlog_Kizant_xegony-01-06-24 carries 3,825 "spell has worn off of" lines and not one ends a
 * charm. So the window has to be closed by what happens next (the mob turns on the raid, the mob dies,
 * six minutes pass) rather than by an expected line, and the name is the key, which is what makes six
 * charms of "an exiled bloodhound" one pet entry instead of six rows.
 *
 * The fixture lines here are shaped from real captures: the success line is exactly what EQ writes, and
 * every wear-off line uses a spell name measured in those files.
 */
[TestClass]
public class CharmWindowPolicyTest
{
    // The harness reads the author's name out of the fixture filename (eqlog_(Player)_(Server).txt), so
    // every "Your …" line in this file resolves to it.
    private const string Self = "Charmone";

    private static double T(string stamp) => DateUtil.StandardDateToDotNetSeconds(stamp + " x");

    // Runs the real parser over synthetic lines and applies the rules cold, exactly like a capture.
    private static MirrorRuleOutcome Run(out EntityTimeline timeline, out DamageFactTable facts, params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mirror-charm-" + Guid.NewGuid().ToString("N")));
        var log = Path.Combine(dir.FullName, "eqlog_Charmone_Eqgate.txt");   // filename seeds ConfigUtil.PlayerName
        File.WriteAllLines(log, lines);

        facts = PipelineHarness.RunFileWithMirror(log).Facts;
        timeline = new EntityTimeline();
        return ClassificationRules.Apply(facts, timeline);
    }

    private static int CountCharmEnds(DamageFactTable facts)
    {
        var n = 0;
        foreach (var e in facts.Evidence) if (e.Kind == EvidenceFact.EvCharmEnd) n++;
        return n;
    }

    [TestMethod]
    public void TheCharmSpellVocabularyIsClosed()
    {
        // In: the two words measured in the captures, with and without a roman rank.
        foreach (var spell in new[] { "Charm", "Charm XVII", "Compulsion", "Compulsion IV" })
        {
            Assert.IsTrue(CharmSpells.IsCharmSpellName(spell), $"{spell} is a charm spell in the logs");
        }

        // Out: the three ways real spell names tried to get in. The first one is why substring matching
        // is not an option — 1,018 casts of it across two files against about ten actual charms.
        foreach (var notCharm in new[]
                 {
                   "Beguiler's Directed Banishment I", "Beguiler's Banishment VI", "Bewilderment of Lights II",
                   "Group Perfected Invisibility I", "Frost Shackles I", "Charmwise", "Charm of the Vile",
                   "Perma Charm", "", null
                 })
        {
            Assert.IsFalse(CharmSpells.IsCharmSpellName(notCharm), $"{notCharm ?? "<null>"} is not a charm spell");
        }
    }

    [TestMethod]
    public void ABuffWearOffLineIsNotACharmEndAtAll()
    {
        // Every buff in the game writes "<Owner>'s <spell> spell has worn off of <npc>." — 15,936 of the
        // 15,943 such lines across three captures. Only the charm spell's version may reach the timeline,
        // and the gate lives in the sensor: a root's wear-off must not even become an evidence fact.
        var outcome = Run(out var timeline, out var facts,
            "[Sun Apr 26 18:00:10 2026] a toughened horror has been charmed.",
            "[Sun Apr 26 18:00:20 2026] Your Frost Shackles I spell has worn off of a toughened horror.",
            "[Sun Apr 26 18:00:30 2026] Your Group Perfected Invisibility I spell has worn off of a toughened horror.",
            "[Sun Apr 26 18:00:40 2026] Your Beguiler's Directed Banishment I spell has worn off of a toughened horror.",
            "[Sun Apr 26 18:10:00 2026] A grodog thug hits Verifyguy for 300 points of damage.");

        Assert.AreEqual(0, CountCharmEnds(facts), "a buff wear-off line became charm-end evidence");
        Assert.AreEqual(1, outcome.Charms.Count);

        var w = outcome.Charms[0];
        Assert.AreEqual(CharmEndReason.Cap, w.Reason, "a buff line closed a charm window");
        Assert.AreEqual(T("[Sun Apr 26 18:00:10 2026]"), w.T0);
        Assert.AreEqual(T("[Sun Apr 26 18:06:10 2026]"), w.T1, "the ceiling is six minutes from the last sighting");

        // and the timeline agrees on both sides of that boundary
        Assert.AreEqual(AffiliationKind.Friendly, timeline.AffiliationAt("a toughened horror", T("[Sun Apr 26 18:06:09 2026]"), out var src));
        Assert.AreEqual("R9-charm", src);
        Assert.AreEqual(AffiliationKind.Enemy, timeline.AffiliationAt("a toughened horror", T("[Sun Apr 26 18:06:10 2026]"), out _));
    }

    [TestMethod]
    public void ARealCharmWearOffClosesTheWindowAndNamesTheOwner()
    {
        // "Your Charm XVII spell has worn off of …" — the seven lines in eqlog_Incogitable_xegony that are
        // actual charm ends, out of 15,943 wear-offs. "Your" resolves to the log author's own key ("You").
        var outcome = Run(out var timeline, out var facts,
            "[Sun Apr 26 18:00:10 2026] a toughened horror has been charmed.",
            "[Sun Apr 26 18:00:20 2026] Your Frost Shackles I spell has worn off of a toughened horror.",
            "[Sun Apr 26 18:00:40 2026] Your Charm XVII spell has worn off of a toughened horror.");

        Assert.AreEqual(1, CountCharmEnds(facts), "the gate let a non-charm wear-off through or lost the real one");
        Assert.AreEqual(1, outcome.Charms.Count);

        var w = outcome.Charms[0];
        Assert.AreEqual(CharmEndReason.WearOff, w.Reason);
        Assert.AreEqual(T("[Sun Apr 26 18:00:40 2026]"), w.T1);
        Assert.AreEqual(Self, w.Owner);
        Assert.AreEqual(Self, timeline.OwnerOf("a toughened horror", T("[Sun Apr 26 18:00:39 2026]")));

        // half-open on the end side, like every other interval here
        Assert.AreEqual(AffiliationKind.Enemy, timeline.AffiliationAt("a toughened horror", T("[Sun Apr 26 18:00:40 2026]"), out _));
        Assert.IsNull(timeline.OwnerOf("a toughened horror", T("[Sun Apr 26 18:00:40 2026]")));
    }

    [TestMethod]
    public void AThirdPartyWearOffNamesThatOwnerInstead()
    {
        // The success line never says who charmed anything, but a third-party wear-off line DOES — and it
        // applies retroactively to the window it ends (measured shape: "<name>`s <spell> spell has worn off").
        var outcome = Run(out var timeline, out _,
            "[Sun Apr 26 18:50:00 2026] a skeleton has been charmed.",
            "[Sun Apr 26 18:52:00 2026] Incogitable`s Charm IV spell has worn off of a skeleton.");

        Assert.AreEqual(1, outcome.Charms.Count);
        Assert.AreEqual("Incogitable", outcome.Charms[0].Owner);
        Assert.AreEqual("Incogitable", timeline.OwnerOf("a skeleton", T("[Sun Apr 26 18:51:00 2026]")));
    }

    [TestMethod]
    public void TheWindowEndsTheInstantTheCharmedNameHitsOurSide()
    {
        // What actually happens when a charm breaks (the user's scenario: the caster vanishes, the mob
        // turns): EQ writes nothing about the charm, the mob just starts damaging raid members. That first
        // hit closes the window, and it is not credited as pet damage afterwards.
        var outcome = Run(out var timeline, out _,
            "[Sun Apr 26 18:09:00 2026] Targeted (Player): Verifyguy",
            "[Sun Apr 26 18:10:00 2026] a fiddlehorns apprentice has been charmed.",
            "[Sun Apr 26 18:10:20 2026] A fiddlehorns apprentice hits Verifyguy for 400 points of damage.");

        Assert.AreEqual(1, outcome.Charms.Count);
        var w = outcome.Charms[0];
        Assert.AreEqual(CharmEndReason.HitOurSide, w.Reason);
        Assert.AreEqual(T("[Sun Apr 26 18:10:20 2026]"), w.T1, "the break did not close the window");

        Assert.AreEqual(AffiliationKind.Friendly, timeline.AffiliationAt("a fiddlehorns apprentice", T("[Sun Apr 26 18:10:19 2026]"), out _));
        Assert.AreEqual(AffiliationKind.Enemy, timeline.AffiliationAt("a fiddlehorns apprentice", T("[Sun Apr 26 18:10:20 2026]"), out _));
    }

    [TestMethod]
    public void AHitOnAnUnknownNameDoesNotBreakTheCharm()
    {
        // The guard on the rule above: only a defender the other rules put ON OUR SIDE ends a window.
        // Unclassified names are the majority of what a pulled mob fights on the way in, and treating one
        // as a break would cut most windows short at the first swing.
        var outcome = Run(out _, out _,
            "[Sun Apr 26 18:10:00 2026] a fiddlehorns apprentice has been charmed.",
            "[Sun Apr 26 18:10:20 2026] A fiddlehorns apprentice hits Zorchmaw for 400 points of damage.",
            "[Sun Apr 26 18:20:00 2026] A grodog thug hits Zorchmaw for 400 points of damage.");

        Assert.AreEqual(1, outcome.Charms.Count);
        Assert.AreEqual(CharmEndReason.Cap, outcome.Charms[0].Reason);
    }

    [TestMethod]
    public void OurOwnCharmCastIsWhatMakesThePetOurs()
    {
        // Measured Δ = 4 s twice in eqlog_Incogitable_xegony ("You begin casting Charm XVII." → success),
        // with an interrupt/resume line in one of the two. The cast is the only ownership signal a success
        // line ever gets.
        var outcome = Run(out var timeline, out _,
            "[Sun Apr 26 18:20:00 2026] You begin casting Charm XVII.",
            "[Sun Apr 26 18:20:04 2026] a Nokk darkblade has been charmed.");

        Assert.AreEqual(1, outcome.Charms.Count);
        Assert.AreEqual(Self, outcome.Charms[0].Owner);
        Assert.AreEqual(Self, timeline.OwnerOf("a Nokk darkblade", T("[Sun Apr 26 18:22:00 2026]")));
    }

    [TestMethod]
    public void TheNearestSpellWasNotTheCharmer()
    {
        // The lookback is 8 s, not "whatever was cast recently". Whatever the caster was doing sits 3-25 s
        // before a success line and is unrelated (these three spell names are the top offenders measured in
        // the captures), so a wider window invents owners out of busy casters.
        var outcome = Run(out var timeline, out _,
            "[Sun Apr 26 18:20:00 2026] You begin casting Slowing Helix VII.",
            "[Sun Apr 26 18:20:04 2026] a Nokk darkblade has been charmed.",
            "[Sun Apr 26 18:30:00 2026] Beguiler begins casting Beguiler's Directed Banishment I.",
            "[Sun Apr 26 18:30:04 2026] an obsidian fiend has been charmed.");

        Assert.AreEqual(2, outcome.Charms.Count);
        foreach (var w in outcome.Charms)
        {
            Assert.IsNull(w.Owner, $"{w.Name} was attributed to {w.Owner} by a non-charm cast");
            Assert.IsNull(timeline.OwnerOf(w.Name, w.T0 + 1));
        }
    }

    [TestMethod]
    public void ReCharmsOfTheSameMobAreOneWindow()
    {
        // The point of keying on the name: an evening of charming "an exiled bloodhound" is one entry in
        // the pet list. These two sightings are the shape measured at 13:11:03 / 13:12:15 in the Kizant
        // capture — the second lands inside the first window, so they merge and the clock restarts there.
        var outcome = Run(out _, out _,
            "[Sun Apr 26 18:30:00 2026] an exiled bloodhound has been charmed.",
            "[Sun Apr 26 18:31:12 2026] an exiled bloodhound has been charmed.");

        Assert.AreEqual(1, outcome.Charms.Count, "a re-charm split into two windows");
        var w = outcome.Charms[0];
        Assert.AreEqual(2, w.Starts);
        Assert.AreEqual(T("[Sun Apr 26 18:30:00 2026]"), w.T0, "the merged window must start at the first sighting");
        Assert.AreEqual(T("[Sun Apr 26 18:37:12 2026]"), w.T1, "a re-charm resets the ceiling to six minutes from itself");

        // …and a sighting past the ceiling is a separate pet span, not a merge
        var second = Run(out _, out _,
            "[Sun Apr 26 18:30:00 2026] an exiled bloodhound has been charmed.",
            "[Sun Apr 26 18:45:00 2026] an exiled bloodhound has been charmed.");
        Assert.AreEqual(2, second.Charms.Count);
    }

    [TestMethod]
    public void DeathClosesTheWindowEvenWhenTheArticleCaseDiffers()
    {
        // Charm lines print "a X", slain lines print "A X" — the window keys case-insensitively while
        // identity keys stay ordinal.
        var outcome = Run(out var timeline, out _,
            "[Sun Apr 26 18:45:30 2026] a grimtooth matriarch has been charmed.",
            "[Sun Apr 26 18:46:10 2026] A grimtooth matriarch was slain by Sirmr!");

        Assert.AreEqual(1, outcome.Charms.Count);
        Assert.AreEqual(CharmEndReason.Death, outcome.Charms[0].Reason);
        Assert.AreEqual(AffiliationKind.Friendly, timeline.AffiliationAt("a grimtooth matriarch", T("[Sun Apr 26 18:46:09 2026]"), out _));
        Assert.AreEqual(AffiliationKind.Enemy, timeline.AffiliationAt("a grimtooth matriarch", T("[Sun Apr 26 18:46:10 2026]"), out _));
    }

    [TestMethod]
    public void DamageAgainstTheSameMobNameIsReportedAsAmbiguous()
    {
        // Another mob of the same species walking into the span is indistinguishable from the pet, and the
        // UI should say so instead of pretending to certainty. Same-name hits are counted, not hidden.
        var outcome = Run(out _, out _,
            "[Sun Apr 26 18:50:00 2026] an imbued whipgrass has been charmed.",
            "[Sun Apr 26 18:50:30 2026] An imbued whipgrass hits a grodog thug for 100 points of damage.",
            "[Sun Apr 26 18:50:40 2026] An imbued whipgrass hits an imbued whipgrass for 100 points of damage.",
            "[Sun Apr 26 18:50:50 2026] An imbued whipgrass hits an imbued whipgrass for 100 points of damage.",
            "[Sun Apr 26 18:57:30 2026] An imbued whipgrass hits a grodog thug for 100 points of damage.");

        Assert.AreEqual(1, outcome.Charms.Count);
        var w = outcome.Charms[0];
        Assert.AreEqual(3, w.FactCount, "only the facts inside the window belong to it");
        Assert.AreEqual(2, w.SameNameFactCount);
    }

    [TestMethod]
    public void ACharmThatOutlivesTheLogStillEndsAtTheCeiling()
    {
        // Nothing contradicts a charm standing at the last line of the file — but "ours forever" would flip
        // the side of every later mob of that name in a re-derived log, so the ceiling still applies and
        // the reason records why no close was ever seen.
        var outcome = Run(out _, out _,
            "[Sun Apr 26 19:00:00 2026] an obsidian fiend has been charmed.");

        Assert.AreEqual(1, outcome.Charms.Count);
        Assert.AreEqual(CharmEndReason.LogEnd, outcome.Charms[0].Reason);
        Assert.AreEqual(T("[Sun Apr 26 19:06:00 2026]"), outcome.Charms[0].T1);
    }
}
