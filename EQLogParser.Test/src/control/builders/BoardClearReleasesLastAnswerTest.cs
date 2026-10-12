using EQLogParser;

namespace EQLogParser;

/*
 * The production clear (LifecycleManager.Clear -> CombatEvents.ActiveDataCleared) blanks every board, and a board's LAST
 * ANSWER must die with it. _lastStatsEvent outlived the clear: Reset() clears the builder's group lists, but the event's
 * Groups list is a COPY of references, so 2.2M damage records and 1.2M heal records stayed reachable through
 * GetLastStats() after the log was cleared (measured; docs/DesignNotes.md -> "DPS and active-reader retention review at
 * 35420fcf"). The consumers ask between builds, where a fresh build is the answer - after a clear there is no answer.
 */
[TestClass]
public class BoardClearReleasesLastAnswerTest
{
    private const string Raider = "Kizant";

    private static GenerateStatsOptions OptionsWithOneFight(DamageFactTable facts, out Fight fight)
    {
        var timeline = new EntityTimeline();
        timeline.SetIdentity(Raider, IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Echohead", IdentityKind.Npc, RuleStrength.Medium, "R4-spell");

        var index = new FightFactIndex(timeline);
        var rows = FightProjection.Build(facts, timeline, index.OnFact);
        Sectionizer.StampGroupIds(rows);

        var input = FightSummarySource.Build(rows, index, facts);
        fight = input.Fights[0];

        var options = new GenerateStatsOptions
        {
            Source = "board clear releases the last answer",
            AllRanges = input.AllRanges,
        };
        options.Npcs.Add(fight);
        return options;
    }

    [TestMethod]
    public void ActiveDataCleared_DropsTheLastAnswerFromBothBuilders()
    {
        var facts = new DamageFactTable(64);
        var seq = 0;
        for (var t = 1_000_000; t < 1_000_030; t++)
        {
            facts.AddFact(new DamageFact(seq++, t, facts.InternName(Raider), facts.InternName("Echohead"),
                                         total: 1234, typeId: LabelTypes.Melee, flags: 0, modMask: 0, subIdx: ushort.MaxValue));
        }

        var options = OptionsWithOneFight(facts, out _);
        DamageStatsBuilder.Instance.BuildTotalStats(options);

        var lastDamage = DamageStatsBuilder.Instance.GetLastStats();
        Assert.IsNotNull(lastDamage, "a completed build leaves its answer");
        Assert.AreEqual(1, lastDamage.Groups.Sum(g => g.Count), "the one fight must be on the board");

        // The healing side through the record seam: a small time-ascending list is exactly what the derived path hands over.
        var heal = new HealRecord
        {
            Healer = Raider,
            Healed = "Berauni",
            Total = 500,
            OverTotal = 12,
            Type = LabelTypes.LabelOf(LabelTypes.Heal),
            SubType = Labels.SelfHeal,
            ModifiersMask = -1,
        };
        options.Heals = [(1_000_010d, heal)];
        HealingStatsBuilder.Instance.BuildTotalStats(options);
        Assert.IsNotNull(HealingStatsBuilder.Instance.GetLastStats(), "a completed build leaves its answer");

        CombatEvents.FireActiveDataCleared(true);

        Assert.IsNull(DamageStatsBuilder.Instance.GetLastStats(),
            "the damage builder's last answer must not outlive the capture that produced it");
        Assert.IsNull(HealingStatsBuilder.Instance.GetLastStats(),
            "the healing builder's last answer must not outlive the capture that produced it");
    }
}
