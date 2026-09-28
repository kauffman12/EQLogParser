using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * MirrorStats — the one calculation a surface asks about the rows it is showing.
 *
 * The requirement these tests exist for: a damage meter that displays "the fights this session touched" and a fight
 * list where the operator selects those same fights and presses summary must print the same digits, because they are
 * the same question asked of the same facts through the same arithmetic. Nothing here is tuned to agree; the seam is
 * asserted instead, so that a change to either side's plumbing fails loudly rather than shipping two totals.
 *
 *   - one row's scope reports exactly the damage its own line evidence holds (the hits aimed at that row's name), and
 *     says nothing at all when asked about no rows — an empty event would be a zero the log never wrote;
 *   - scopes add: the union of two disjoint row sets totals the same as their separate totals, which is what makes
 *     "the session so far" and "select-all in the list" one number (each fact belongs to exactly one row);
 *   - a scope builds on its OWN DamageStatsBuilder, so an overlay refreshing once a second cannot repaint the board
 *     an open summary is showing. That is the failure mode of "just reuse the builder": shared singleton, two scopes,
 *     and the operator's summary starts flickering through the meter's window;
 *   - a heal-less scope arrives as an empty heal list rather than null, so a refresh clears healing instead of
 *     leaving the previous scope's numbers on screen.
 */
[TestClass]
public class MirrorStatsTest
{
    private const double T0 = 1_000;

    // Same fixture shape as MirrorSummaryFightsTest: facts by attacker/defender/amount/second/label.
    private static DamageFactTable BuildFacts(params (string Atk, string Def, long Dmg, double T, byte Label)[] rows)
    {
        var facts = new DamageFactTable(64);
        var seq = 0;
        foreach (var (atk, def, dmg, t, label) in rows)
        {
            var a = facts.InternName(atk);
            var d = facts.InternName(def);
            facts.AddFact(new DamageFact(seq++, (long)(T0 + t), a, d, total: (uint)dmg, typeId: label,
              flags: 0, modMask: 0, subIdx: ushort.MaxValue));
        }
        return facts;
    }

    // The pass MirrorSession runs over a capture: rules, then the projection with its index sink.
    private static (List<DerivedFight> Fights, MirrorDamageIndex Index, DamageFactTable Facts) Derive(
      DamageFactTable facts, EntityTimeline? timeline = null)
    {
        var line = timeline ?? new EntityTimeline();
        ClassificationRules.Apply(facts, line);

        var index = new MirrorDamageIndex();
        var fights = FightProjection.Build(facts, line, index.OnFact);
        Sectionizer.StampGroupIds(fights);
        return (fights, index, facts);
    }

    // A raid hitting two mobs, each mob hitting back once. The hits BACK are the tanking side: they belong to the
    // rows but not to the raid's damage column, and they are what makes an expected total worth writing out by hand.
    private static (List<DerivedFight> Fights, MirrorDamageIndex Index, DamageFactTable Facts) TwoPulls()
    {
        var facts = BuildFacts(
            ("Illuminai", "Grimling", 500, 0, LabelTypes.Melee),
            ("Bithika", "Grimling", 300, 1, LabelTypes.Dd),
            ("Grimling", "Illuminai", 120, 2, LabelTypes.Melee),      // tanking side of the Grimling row
            ("Illuminai", "Skeleton", 700, 40, LabelTypes.Melee),
            ("Bithika", "Skeleton", 250, 41, LabelTypes.Dd),
            ("Skeleton", "Bithika", 90, 42, LabelTypes.Melee));       // tanking side of the Skeleton row

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Bithika", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        return Derive(facts, timeline);
    }

    private static DerivedFight Row(List<DerivedFight> rows, string name)
        => rows.First(r => r.Name == name);

    private static HealFactTable NoHeals(DamageFactTable facts) => new(facts);

    // ---- one scope, one row set ----

    [TestMethod]
    public void AScopeReportsTheDamageAimedAtItsOwnRows()
    {
        var (rows, index, facts) = TwoPulls();

        var stats = MirrorStats.For([Row(rows, "Grimling")], index, facts, NoHeals(facts))?.CombinedStats;

        Assert.IsNotNull(stats, "a scope with a row in it produces a board");
        // 500 + 300: everything aimed at the Grimling row's name. The 120 it dealt to Illuminai is the raid taking a
        // hit (tanking side) and must not appear as the raid's damage output.
        Assert.AreEqual(800L, (long)stats.RaidStats.Total, "one row's scope is that row's own damage");
        // The raid row's own Hits comes back 0 out of the damage builder (measured, not assumed), so a scope reads
        // player rows for hit counts — which is where the meter's own list gets them from too.
        Assert.AreEqual(2, stats.StatsList.Count, "one row per raider");
        Assert.AreEqual(2L, stats.StatsList.Sum(p => (long)p.Hits), "each raider's own hit count");
    }

    [TestMethod]
    public void ScopesAddUpToTheSameTotalAsTheirUnion()
    {
        var (rows, index, facts) = TwoPulls();
        var grim = Row(rows, "Grimling");
        var bone = Row(rows, "Skeleton");

        var first = MirrorStats.For([grim], index, facts, NoHeals(facts))?.CombinedStats;
        var second = MirrorStats.For([bone], index, facts, NoHeals(facts))?.CombinedStats;
        var both = MirrorStats.For([grim, bone], index, facts, NoHeals(facts))?.CombinedStats;

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.IsNotNull(both);
        // The exactness law in its smallest form: taking the fights one at a time and taking them together cannot come
        // to different damage, because FightProjection files each fact into exactly one row. If a fact ever landed on
        // two rows this sum reads high; if it filed onto none, low.
        Assert.AreEqual((long)first.RaidStats.Total + second.RaidStats.Total, (long)both.RaidStats.Total,
          "select-all totals the same as the parts (no fact counted twice, none dropped)");
        Assert.AreEqual((long)first.RaidStats.Hits + second.RaidStats.Hits, (long)both.RaidStats.Hits, "and the same for hits");
    }

    [TestMethod]
    public void TenPullsInOneWindowTotalTheSameAsTheRowsOneByOne()
    {
        // The scenario from the requirement: monitoring a log, ten mobs die inside the meter's reset window. Whatever
        // set of rows that session covers, the meter's total is the list's select-all total.
        var legs = new List<(string Atk, string Def, long Dmg, double T, byte Label)>();
        for (var mob = 0; mob < 10; mob++)
        {
            var name = $"Mob{mob:00}";
            // Ten seconds of work per mob, spaced so each is its own row.
            var at = mob * 60.0;
            legs.Add(("Illuminai", name, (uint)(100 + mob), at, LabelTypes.Melee));
            legs.Add(("Bithika", name, (uint)(1_000 + mob * 7), at + 1, LabelTypes.Dd));
            legs.Add(("Illuminai", name, (uint)(50 + mob), at + 2, LabelTypes.Dot));
        }

        var facts = BuildFacts([.. legs]);
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Bithika", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        var (rows, index, table) = Derive(facts, timeline);

        var expected = legs.Sum(leg => leg.Dmg);
        var scope = MirrorStats.For(rows, index, table, NoHeals(table))?.CombinedStats;

        Assert.IsNotNull(scope);
        Assert.AreEqual(10, rows.Count, "ten mobs, ten rows");
        Assert.AreEqual(expected, (long)scope.RaidStats.Total, "the session's total is every fact the rows hold");
        Assert.AreEqual(legs.Count, scope.StatsList.Sum(p => (long)p.Hits), "every hit lands on a player row");
    }

    // ---- what a refresh must not touch ----

    [TestMethod]
    public void ARefreshLeavesTheBoardsLastAnswerAlone()
    {
        var (rows, index, facts) = TwoPulls();

        // Prime the shared builder with a DIFFERENT scope: one row only. This is what an open summary holds between
        // clicks, and it is what a meter refreshing through the singleton would overwrite once per second.
        DamageStatsBuilder.Instance.BuildTotalStats(SummaryForRow(Row(rows, "Grimling"), index, facts));
        var before = DamageStatsBuilder.Instance.GetLastStats()?.CombinedStats?.RaidStats.Total;
        Assert.AreEqual(800L, before, "the summary's own scope was built through the shared builder");

        _ = MirrorStats.For(rows, index, facts, NoHeals(facts));

        var after = DamageStatsBuilder.Instance.GetLastStats()?.CombinedStats?.RaidStats.Total;
        Assert.AreEqual((long)before!, (long)after!,
          "a scope calculation ran on its own builder — the board an open summary shows is untouched");
    }

    // Options for the shared builder holding exactly one derived row, the way a one-row click would.
    private static GenerateStatsOptions SummaryForRow(DerivedFight row, MirrorDamageIndex index, DamageFactTable facts)
    {
        var input = MirrorSummaryFights.Build([row], index, facts);
        var options = new GenerateStatsOptions { AllRanges = input.AllRanges };
        foreach (var fight in input.Fights) options.Npcs.Add(fight);
        return options;
    }

    // ---- the two empty cases, which are different ----

    [TestMethod]
    public void AScopeOfNoRowsSaysNothing()
    {
        var (rows, index, facts) = TwoPulls();

        Assert.IsNull(MirrorStats.For([], index, facts, NoHeals(facts)),
          "no rows is no scope: an empty board would be a zero the log never wrote");
        Assert.IsNull(MirrorStats.For(null!, index, facts, NoHeals(facts)));
    }

    [TestMethod]
    public void AScopeWithoutHealingArrivesAsAnEmptyHealList()
    {
        var (rows, index, facts) = TwoPulls();
        var row = Row(rows, "Grimling");

        // What MirrorStats hands the builder on the healing half. Null would mean "say nothing about healing" and a
        // surface would go on showing the PREVIOUS scope's numbers after a refresh; this capture has no heals, so an
        // empty list is the honest answer, and the damage half of the same scope still reports.
        var input = MirrorSummaryFights.Build([row], index, facts);
        Assert.AreEqual(0, MirrorSummaryHeals.Materialize(NoHeals(facts), input.AllRanges).Count,
          "a heal-less scope says 'nothing healed', not 'nothing to say about healing'");
        Assert.IsNotNull(MirrorStats.For([row], index, facts, NoHeals(facts))?.CombinedStats);
    }
}
