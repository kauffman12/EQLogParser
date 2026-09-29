using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * What a charm does to the DISPLAY list (FightProjection), as opposed to what a window is
 * (CharmWindowPolicyTest). Agreed 2026-10:
 *
 *   - the initial charm counts as the NPC's death: its row closes there, marked dead with "charmed" as the
 *     reason. The raid stopped fighting that mob by taking it, and the alternative — a row running on until
 *     an inactivity gap swallows it — hides what actually ended the fight.
 *   - if the charmed mob then dies, that second death is an OUR-side death: like a party member falling, the
 *     raid gets no kill for it. One corpse cannot hand out two kills with the same name, and the death would
 *     otherwise land on whichever row of that name happened to be open — possibly a different instance three
 *     pulls away. The window is where it gets recorded: CharmEndReason.Death.
 *   - during the window the mob's own output is player-side credit belonging to its charmer, which is what
 *     folds an evening of charming `an imbued whipgrass` into one pet entry in that player's damage board.
 *
 * Name spelling note, because it bites every assertion here: records carry sentence-case names ("A bone
 * walker") while the charm confirm line writes "a bone walker". The two meet because EntityTimeline keys entity
 * names case-insensitively — see EntityNameKeyTest for that law; registering both spellings by hand was the old
 * workaround at this seam and is gone.
 */
[TestClass]
public class CharmRowProjectionTest
{
    private const string Self = "Charmrow";

    private static MirrorRuleOutcome Run(out EntityTimeline timeline, out DamageFactTable facts, params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mirror-charmrow-" + Guid.NewGuid().ToString("N")));
        var log = Path.Combine(dir.FullName, "eqlog_Charmrow_Eqgate.txt");   // filename seeds ConfigUtil.PlayerName
        File.WriteAllLines(log, lines);

        facts = PipelineHarness.RunFileWithMirror(log).Facts;
        timeline = new EntityTimeline();
        return ClassificationRules.Apply(facts, timeline);
    }

    private static double T(string stamp) => DateUtil.StandardDateToDotNetSeconds(stamp + " x");

    private static DerivedFight? Row(List<DerivedFight> rows, string name)
        => rows.FirstOrDefault(r => r.Name == name);

    // Damage records the summary board would be handed for a set of rows.
    private static List<DamageRecord> Records(IEnumerable<DerivedFight> rows, MirrorDamageIndex index, DamageFactTable facts)
      => MirrorSummaryFights.Build(rows.ToList(), index, facts).Fights
          .SelectMany(f => f.DamageBlocks.SelectMany(b => b.Actions)).OfType<DamageRecord>().ToList();

    [TestMethod]
    public void TheCharmClosesTheNpcRowAsDeath()
    {
        // The raid is swinging at it, then somebody charms it, and nothing about that mob is ever written
        // again. That row is not "still going" and it is not an inactivity gap: it ended when the mob became
        // ours (decision: count the initial charm as a death).
        var outcome = Run(out var timeline, out var facts,
            "[Sun Apr 26 18:00:00 2026] You hit a rune-etched warblade for 900 points of damage.",
            "[Sun Apr 26 18:00:05 2026] A rune-etched warblade hits You for 400 points of damage.",
            "[Sun Apr 26 18:00:10 2026] a rune-etched warblade has been charmed.");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count);

        var row = rows[0];
        Assert.AreEqual("A rune-etched warblade", row.Name);
        Assert.IsTrue(row.Dead, "the charm did not close the row");
        Assert.AreEqual(DerivedFightEnd.Charmed, row.EndReason);
        Assert.AreEqual(1, outcome.Charms.Count);

        // Control: without the charm line the same facts leave the row open and alive — the death marker came
        // from the charm, not from the fighting stopping.
        _ = Run(out var plainTimeline, out var plainFacts,
            "[Sun Apr 26 18:00:00 2026] You hit a rune-etched warblade for 900 points of damage.",
            "[Sun Apr 26 18:00:05 2026] A rune-etched warblade hits You for 400 points of damage.");
        var plain = FightProjection.Build(plainFacts, plainTimeline)[0];
        Assert.IsFalse(plain.Dead, "a row that merely stopped is reported as dead");
        Assert.AreEqual(DerivedFightEnd.Open, plain.EndReason);
    }

    [TestMethod]
    public void ACharmSightingLongAfterTheLastSwingStillClosesTheRow()
    {
        /*
         * Matching an event to a row is a wider window than splitting rows, and this is the case that proves they
         * must stay apart. The raid's last swing is a minute and change before the mesmerist lands the charm — the
         * two acts are by different people, so the gap between them says nothing about whether the fight is over;
         * what ends it is the charm. Under the 30 s that now splits rows this row would sit there reading "still
         * going" while its mob was already the raid's pet (12 such rows on Incogitable).
         */
        var outcome = Run(out var timeline, out var facts,
            "[Sun Apr 26 18:00:00 2026] You hit a rune-etched warblade for 900 points of damage.",
            "[Sun Apr 26 18:01:15 2026] a rune-etched warblade has been charmed.");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count, "one mob, one row");
        var row = rows[0];
        Assert.IsTrue(row.Dead && row.EndReason == DerivedFightEnd.Charmed,
            "a charm that arrives after a long silence still belongs to the row it ended");
        Assert.AreEqual(1, outcome.Charms.Count);
    }

    [TestMethod]
    public void ADeathWhileCharmedDoesNotGiveTheRaidAKill()
    {
        // The pet dies mid-charm (the raid's own AoE catches it, or the pull wipes it). The NPC row was already
        // closed and credited at the charm itself, so this death must not mark anything slain — it is a death
        // on our side of the ledger.
        var outcome = Run(out var timeline, out var facts,
            "[Sun Apr 26 18:10:00 2026] You hit a bone walker for 900 points of damage.",
            "[Sun Apr 26 18:10:10 2026] a bone walker has been charmed.",
            "[Sun Apr 26 18:10:30 2026] A bone walker was slain by Illuminai.");

        Assert.AreEqual(1, facts.Deaths.Length, "the slain line did not become a death fact");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("A bone walker", rows[0].Name);
        Assert.AreEqual(DerivedFightEnd.Charmed, rows[0].EndReason,
                        $"the pet's death re-marked the NPC row ({rows[0].EndReason})");

        // The window is what remembers it died.
        Assert.AreEqual(1, outcome.Charms.Count);
        Assert.AreEqual(CharmEndReason.Death, outcome.Charms[0].Reason, "the window lost the death that ended it");
    }

    [TestMethod]
    public void ACharmOfTheSameNameMuchLaterDoesNotKillThisRow()
    {
        // A charm closes THIS engagement's row, not every row that ever carried the name: an hour later the
        // raid pulls another mob of the same kind and charms it — a separate fight, a separate row.
        _ = Run(out var timeline, out var facts,
            "[Sun Apr 26 18:00:00 2026] You hit a cave bear for 900 points of damage.",
            "[Sun Apr 26 19:00:00 2026] a cave bear has been charmed.");

        var row = FightProjection.Build(facts, timeline)[0];
        Assert.IsFalse(row.Dead, "a charm an hour after the fight ended was treated as its end");
    }

    [TestMethod]
    public void WhileCharmedItsDamageIsPlayerSideCredit()
    {
        // Two things have to line up for a pet's output to become credit: identity (so the name is an NPC at
        // all) and the window (so it reads player-side for the span). The attacker spelling is the sentence-
        // initial one, which is exactly where the ordinal name keys used to lose it.
        var lines = new[]
        {
            "[Sun Apr 26 18:20:00 2026] You begin casting Charm XVII.",
            "[Sun Apr 26 18:20:04 2026] an imbued whipgrass has been charmed.",
            "[Sun Apr 26 18:20:20 2026] An imbued whipgrass hits a grodog thug for 400 points of damage.",
        };

        _ = Run(out var timeline, out var facts, lines);
        Assert.AreEqual(Self, timeline.OwnerOf("An imbued whipgrass", T("[Sun Apr 26 18:20:20 2026]")),
                        "the charmer was not reachable under the name the damage line writes");

        var rows = FightProjection.Build(facts, timeline);
        Assert.IsNull(Row(rows, "An imbued whipgrass"), "a mob on our side must not hold a row of its own");

        var victim = Row(rows, "A grodog thug");
        Assert.IsNotNull(victim, "the pet's target lost the exchange");
        Assert.IsTrue(victim.PlayerRollup.ContainsKey("An imbued whipgrass"), "the swing was not credited player-side");

        // …and with the confirm line removed the very same swing never becomes credit at all: both names are
        // NPCs, and two mobs swinging at each other is not a raid fight. The window is what makes this one ours
        // to count — which is why an unattributed window (ACharmWithNoCasterInventsNobody) is allowed to leave
        // the damage ownerless instead of guessing.
        _ = Run(out var plainTimeline, out var plainFacts, lines[0], lines[2]);
        Assert.AreEqual(0, FightProjection.Build(plainFacts, plainTimeline).Count,
                        "mob-vs-mob noise reached the list");
    }

    [TestMethod]
    public void ThePetsDamageReachesTheSummaryUnderItsCharmer()
    {
        // Where the user sees it: the damage board folds this into `Charmrow`'s pet line, so ten charms of the
        // same mob across an evening are one entry — through AttackerOwner, the same field a ``Bob`s pet`` line
        // uses.
        var outcome = Run(out var timeline, out var facts,
            "[Sun Apr 26 18:20:00 2026] You begin casting Charm XVII.",
            "[Sun Apr 26 18:20:04 2026] an imbued whipgrass has been charmed.",
            "[Sun Apr 26 18:20:20 2026] An imbued whipgrass hits a grodog thug for 400 points of damage.");

        Assert.AreEqual(1, outcome.Charms.Count);
        Assert.AreEqual(Self, outcome.Charms[0].Owner);

        var index = new MirrorDamageIndex(timeline);
        var rows = FightProjection.Build(facts, timeline, index.OnFact);
        var pet = Records(rows, index, facts).Where(a => a.Attacker == "An imbued whipgrass").ToList();

        Assert.AreEqual(1, pet.Count, "the charmed swing did not reach the summary");
        Assert.AreEqual(Self, pet[0].AttackerOwner, "the swing is not credited to the charmer's pet line");

        // Ownership is the window, not the name: past the ceiling nobody owns this mob, so from there on its
        // swings stop being anybody's pet damage.
        Assert.IsNull(timeline.OwnerOf("An imbued whipgrass", T("[Sun Apr 26 18:30:00 2026]")));
    }

    [TestMethod]
    public void RaidDamageOnTheirOwnCharmedMobStaysInTheList()
    {
        // The mob is ours now, and the raid's stray AoE keeps landing on it. Dropping those swings as friendly
        // fire would quietly shrink a player's total, so they get the mob's post-charm row: our side, said out
        // loud, while the pre-charm half stays closed as the charm itself.
        _ = Run(out var timeline, out var facts,
            "[Sun Apr 26 18:40:00 2026] You hit a cave bear for 900 points of damage.",
            "[Sun Apr 26 18:40:05 2026] a cave bear has been charmed.",
            "[Sun Apr 26 18:40:20 2026] You hit a cave bear for 300 points of damage.");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(2, rows.Count, "the stray swings were dropped as friendly fire");

        Assert.IsTrue(rows[0].Dead && rows[0].EndReason == DerivedFightEnd.Charmed);
        Assert.AreEqual(900, rows[0].DamageTotal);

        var inCustody = rows[1];
        Assert.IsTrue(inCustody.CharmedOwned, "the row about our own mob does not say so");
        Assert.AreEqual(300, inCustody.DamageToOwner);
        Assert.IsTrue(inCustody.PlayerRollup.ContainsKey(Self), "the raid's own damage lost its credit");
    }

    [TestMethod]
    public void ACharmWithNoCasterInventsNobody()
    {
        // The other half of the credit rule: a window whose caster the log never showed (another raid member's
        // charm, in a log without their casts) leaves AttackerOwner null. Folding that damage onto whichever
        // raider happened to be nearby would move real credit to the wrong player.
        var outcome = Run(out var timeline, out var facts,
            "[Sun Apr 26 18:20:04 2026] an imbued whipgrass has been charmed.",
            "[Sun Apr 26 18:20:20 2026] An imbued whipgrass hits a grodog thug for 400 points of damage.");

        Assert.AreEqual(1, outcome.Charms.Count);
        Assert.IsNull(outcome.Charms[0].Owner);

        var index = new MirrorDamageIndex(timeline);
        var rows = FightProjection.Build(facts, timeline, index.OnFact);
        var pet = Records(rows, index, facts).Where(a => a.Attacker == "An imbued whipgrass").ToList();

        Assert.AreEqual(1, pet.Count);
        Assert.IsNull(pet[0].AttackerOwner, "an unattributed charm claimed a charmer");
    }

    [TestMethod]
    public void APetRowIsNotOnTheFightList()
    {
        // The list shows encounters, not pets (agreed 2026-10, same rule that keeps `Ziggy`s pet` out of the
        // legacy table). The mob's own row stays — closed as the charm, which is how that encounter ended — and
        // the post-charm row about the same animal does not appear.
        _ = Run(out var timeline, out var facts,
            "[Sun Apr 26 18:40:00 2026] You hit a cave bear for 900 points of damage.",
            "[Sun Apr 26 18:40:05 2026] a cave bear has been charmed.",
            "[Sun Apr 26 18:40:20 2026] You hit a cave bear for 300 points of damage.");

        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(2, rows.Count, "the projection lost the pre/post-charm split");

        var visible = CharmPetRows.Visible(rows);
        Assert.AreEqual(1, visible.Count, "a pet row reached the fight list");
        Assert.IsTrue(visible[0].Dead && visible[0].EndReason == DerivedFightEnd.Charmed,
                      "the row that stayed listed is not the encounter");
    }

    [TestMethod]
    public void HidingAPetRowDoesNotDeleteItsDamage()
    {
        // Why hiding is a display rule and not a projection rule: those swings are real raid damage, and a board
        // is built from whatever selection a click produced. Hand the hidden row back and the player who swung
        // keeps both halves.
        _ = Run(out var timeline, out var facts,
            "[Sun Apr 26 18:40:00 2026] You hit a cave bear for 900 points of damage.",
            "[Sun Apr 26 18:40:05 2026] a cave bear has been charmed.",
            "[Sun Apr 26 18:40:20 2026] You hit a cave bear for 300 points of damage.");

        var index = new MirrorDamageIndex(timeline);
        var rows = FightProjection.Build(facts, timeline, index.OnFact);
        var visible = CharmPetRows.Visible(rows);

        Assert.AreEqual(1, Records(visible, index, facts).Count(a => a.Attacker == Self),
                        "the visible rows alone were not the smaller number");

        var actions = Records(CharmPetRows.WithHiddenPets(visible, rows), index, facts);
        Assert.AreEqual(2, actions.Count(a => a.Attacker == Self), "hiding the pet row lost the raid's swings");
        Assert.AreEqual(1200, actions.Where(a => a.Attacker == Self).Sum(a => (long)a.Total),
                        "the stray swings changed total on the way to the board");
    }

    [TestMethod]
    public void WithHiddenPetsAddsPairedOrOverlappingRowsOnly()
    {
        // Two ways a hidden pet row belongs to a click, and one way it does not. Synthetic rows because this is
        // arithmetic on links and spans rather than parsing.
        static DerivedFight RowAt(string name, double from, double to, bool pet)
            => new() { Name = name, BeginTime = from, LastTime = to, RaidPet = pet };

        var encounter = RowAt("a cave bear", 100, 200, false);
        var itsOwnPet = RowAt("a cave bear", 210, 400, true);      // starts after the encounter ends: link only
        var insideWindow = RowAt("a bone walker", 150, 180, true); // no encounter of its own, but overlaps
        var elsewhere = RowAt("a skeleton", 900, 990, true);       // another pull entirely
        itsOwnPet.EncounterRow = encounter;
        var all = new[] { encounter, itsOwnPet, insideWindow, elsewhere };

        var back = CharmPetRows.WithHiddenPets([encounter], all);
        Assert.IsTrue(back.Contains(itsOwnPet), "the clicked encounter lost its own pet half");
        Assert.IsTrue(back.Contains(insideWindow), "the pet inside the clicked window did not come back");
        Assert.IsFalse(back.Contains(elsewhere), "a pet row from another pull was dragged in");

        // Asking again adds nothing: every row once, still in engagement order.
        var again = CharmPetRows.WithHiddenPets(back, all);
        Assert.AreEqual(back.Count, again.Count, "a hidden row was added twice");
        Assert.AreEqual(100, again[0].BeginTime, "the merged selection lost projection order");

        // And the link is what makes the pair work — strip it and the same rows say no.
        itsOwnPet.EncounterRow = null;
        Assert.IsFalse(CharmPetRows.WithHiddenPets([encounter], all).Contains(itsOwnPet),
                       "a pet row came back with neither a link nor an overlap");
    }

    [TestMethod]
    public void ACharmedRaidMemberStaysOnTheList()
    {
        // The trap on the other side of hiding: "has been charmed" registers its target as an NPC at R9's own
        // strength, so a raid member who gets charmed looks exactly like a mob by kind alone. She is an encounter —
        // the raid fights her and takes her back — so her row stays listed with the "charmed" status, which is why
        // FightProjection requires an NPC reason stronger than the charm line before calling a row a pet.
        _ = Run(out var timeline, out var facts,
            "[Sun Apr 26 18:50:00 2026] Bithika hits a grodog thug for 500 points of damage.",
            "[Sun Apr 26 18:50:05 2026] bithika has been charmed.",
            "[Sun Apr 26 18:50:20 2026] You hit Bithika for 300 points of damage.");

        var rows = FightProjection.Build(facts, timeline);
        var raider = Row(rows, "Bithika");
        Assert.IsNotNull(raider, "the raid's fight with their own charmed member disappeared from the list");
        Assert.IsTrue(raider.CharmedOwned, "the row does not say a window put her on the enemy side");
        Assert.IsFalse(raider.RaidPet, "a raid member was classified as the raid's pet");
        Assert.IsTrue(CharmPetRows.Visible(rows).Contains(raider), "a charmed raid member was hidden from the list");
    }
}
