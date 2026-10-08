using System.Text;

namespace EQLogParser;

/*
 * The damage board's golden: what the Damage Summary actually displays, frozen as text.
 *
 * Why a golden and not a pile of hand-written numbers: this file exists so the builder can be restructured (one
 * measurement pass instead of two record walks and three copies of the same counting function; a retained pool that
 * can be re-windowed and re-presented without touching records) with an answer to "did anyone's column change?" that
 * does not require re-reading arithmetic. Re-generating the golden is deliberate work, so a behaviour change shows up
 * as a diff on a named row and a named column instead of as a summary that "looks about right".
 *
 * What is frozen (see Snapshot for the exact columns — they are the DamageSummary grid's MappingNames plus the
 * structural facts behind them):
 *   - the raid line and EVERY player row: totals, Dps/Sdps, %, seconds, hits, max/min/best second, the average and
 *     rate columns, bane hits and the Special cell;
 *   - the pet breakdown: which rows are top level, which aggregate as `X +Pets`, and every child under its parent
 *     with its share of the parent;
 *   - the per-row Class column AND the PlayerClasses map the filter/summary controls read;
 *   - the sub-stat rows behind an expanded row (spell/melee lines, their crit counts and best seconds) — this is what
 *     catches a change in how Dd/Dot subtype keys are formed;
 *   - the events: the StatsGenerationEvent state sequence and every DataPointEvent action with its group/record
 *     counts (the chart's "repaint me" signal), so a refactor cannot quietly drop or duplicate one;
 *   - three windows over the SAME retained pool (`MaxSeconds` only, `MinSeconds`+`MaxSeconds`, and widening back out),
 *     because the pane's time choosers re-slice the groups the builder kept instead of asking the capture again, and
 *     that is the property any new pool has to keep.
 *
 * Regenerating after an INTENTIONAL change:
 *     EQLP_GOLDEN_WRITE=1 dotnet test EQLogParser.Test --filter DamageBoardGolden
 *     cp EQLogParser.Test/bin/Debug/net10.0/mini-data/board/damage-board.actual.txt \
 *        EQLogParser.Test/data/board/damage-board.golden.txt
 * then read the diff before committing it. The fixture is synthetic and small on purpose: every shape in it was taken
 * from a real capture's grammar, and the point is a readable diff, not volume (volume is what MeterBoardCostRealLogTest
 * and the gated real-log tests are for).
 */
[TestClass]
[DoNotParallelize]
public class DamageBoardGoldenTest
{
    private const string Fixture = "damage-board.txt";
    private const string GoldenFile = "damage-board.golden.txt";
    private const string ActualFile = "damage-board.actual.txt";

    // The six DamageValidator knobs are process-static and other tests flip them; a golden needs one known setting.
    private bool _ass, _bane, _ds, _fb, _hs, _su;
    private Func<string, bool>? _originalIsValidClass;

    [TestInitialize]
    public void Setup()
    {
        // The Class column is a registry read, and the registry only accepts a class write when the HOST says the label
        // is real (App.xaml.cs wires this in production). Unwired, every frenzy/cast class write is refused in silence
        // and the golden would freeze an empty Class column as if that were the app's answer. Same hook FrenzyClassTest sets.
        PipelineHarness.EnsureDataStore();
        _originalIsValidClass = CombatRecordLookup.IsValidClassName;
        CombatRecordLookup.IsValidClassName = name => EQDataStore.Instance.IsValidClassName(name);

        DamageLineParser.ResetProcessState();
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();
        HealRecordSource.Current = null;

        _ass = AppSettings.IsAssassinateDamageEnabled;
        _bane = AppSettings.IsBaneDamageEnabled;
        _ds = AppSettings.IsDamageShieldDamageEnabled;
        _fb = AppSettings.IsFinishingBlowDamageEnabled;
        _hs = AppSettings.IsHeadshotDamageEnabled;
        _su = AppSettings.IsSlayUndeadDamageEnabled;
        AppSettings.IsAssassinateDamageEnabled = true;
        AppSettings.IsBaneDamageEnabled = true;
        AppSettings.IsDamageShieldDamageEnabled = true;
        AppSettings.IsFinishingBlowDamageEnabled = true;
        AppSettings.IsHeadshotDamageEnabled = true;
        AppSettings.IsSlayUndeadDamageEnabled = true;

        // Group numbers are operator-entered (the pane's pencil writes through SetPlayerAssignedGroup), so the
        // golden states them rather than inheriting whatever a previous test in this process left assigned.
        DamageStatsBuilder.Instance.SetPlayerAssignedGroup("Akira", 1);
        DamageStatsBuilder.Instance.SetPlayerAssignedGroup("Bryn", 2);
    }

    [TestCleanup]
    public void Cleanup()
    {
        AppSettings.IsAssassinateDamageEnabled = _ass;
        AppSettings.IsBaneDamageEnabled = _bane;
        AppSettings.IsDamageShieldDamageEnabled = _ds;
        AppSettings.IsFinishingBlowDamageEnabled = _fb;
        AppSettings.IsHeadshotDamageEnabled = _hs;
        AppSettings.IsSlayUndeadDamageEnabled = _su;

        // Out of range removes the assignment rather than leaving a zero behind, so the next test sees no group at all.
        DamageStatsBuilder.Instance.SetPlayerAssignedGroup("Akira", -1);
        DamageStatsBuilder.Instance.SetPlayerAssignedGroup("Bryn", -1);
        CombatRecordLookup.IsValidClassName = _originalIsValidClass ?? (_ => false);
        DamageLineParser.ResetProcessState();
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();
    }



    // The production door for a click: classify, project the rows, stamp sections, materialize. Same sequence
    // DerivedHealBoardTest uses, so this golden sits on the same input the app's select-all hands the builder.
    private static IReadOnlyList<Fight> Rows(PipelineHarness.DeriveRunResult run)
    {
        ClassificationRules.Apply(run.Facts, run.Timeline);
        var index = new FightFactIndex();
        var rows = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
        Sectionizer.StampGroupIds(rows);
        return FightSummarySource.Build(rows, index, run.Facts).Fights;
    }

    private static TimeRange WholeRange(IReadOnlyList<Fight> rows)
    {
        var range = new TimeRange();
        foreach (var row in rows) range.Add(new TimeSegment(row.BeginDamageTime, row.LastDamageTime));
        return range;
    }

    [TestMethod]
    public void DamageBoard_FullBuild_MatchesGolden()
    {
        var run = PipelineHarness.RunFileDerived(BoardGolden.FixturePath(Fixture));
        var rows = Rows(run);
        Assert.IsTrue(rows.Count >= 2, "fixture should produce at least two fight rows (two mobs, one life each)");

        var builder = DamageStatsBuilder.Instance;
        var states = new List<string>();
        var dataPoints = new List<string>();
        var snapshot = new StringBuilder();

        // RebuildTotalStats does nothing unless somebody is listening (it is the pane's door), so both events are
        // subscribed for the whole sequence — including the no-data paths.
        void OnStatus(StatsGenerationEvent e) => states.Add($"{e.Type}|{e.State}|groups={e.Groups.Count}|uniqueGroups={e.UniqueGroupCount}|limited={e.Limited}");
        /*
         * RecordGroupCollection is a one-shot cursor (its own fields walk the groups), so it is enumerated exactly once
         * per event here — and what is recorded is what the chart would have drawn: the validator-approved point count.
         */
        void OnData(DataPointEvent e) =>
            dataPoints.Add($"{e.Action}|points={(e.Iterator == null ? -1 : e.Iterator.Count())}" +
                $"|selected={e.Selected.Count}|selectedGroups={e.SelectedGroups.Count}");

        builder.EventsGenerationStatus += OnStatus;
        builder.EventsUpdateDataPoint += OnData;
        try
        {
            var all = WholeRange(rows);

            // 1) whole capture, every row (what select-all + "All" shows)
            var fullOptions = new GenerateStatsOptions { AllRanges = all, Source = "golden full" };
            foreach (var row in rows) fullOptions.Npcs.Add(row);
            builder.BuildTotalStats(fullOptions);

            var full = builder.GetLastStats()?.CombinedStats;
            Assert.IsNotNull(full, "the full build produced no board");

            Snapshot(snapshot, "full", full);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);

            // Invariants first: they say what a diff MEANS even if the golden itself is replaced wholesale.
            AssertInvariants(full);

            // 2) the pet/player breakdown, spelled out rather than only frozen
            var akira = full.StatsList.FirstOrDefault(s => s.Name == "Akira +Pets")
                ?? throw new AssertFailedException("no Akira +Pets row in StatsList");
            Assert.IsNotNull(akira, "Akira's own and his pet's damage aggregate under one row");
            Assert.IsTrue(akira.IsTopLevel);
            Assert.IsTrue(full.Children.TryGetValue("Akira +Pets", out var akiraKids));
            var kidNames = akiraKids!.Select(c => c.Name).ToList();
            CollectionAssert.Contains(kidNames, "Akira");
            CollectionAssert.Contains(kidNames, "Akira`s pet");
            Assert.AreEqual(akira.Total, akiraKids.Sum(k => k.Total), "an aggregate row is exactly the sum of its children");

            /*
             * A `+Pets` row exists ONLY where an owner is known. Kuro was charmed in this capture and no line names who
             * charmed it, so its swings stay on Kuro's own row: the fold never invents a master (and never mints a label
             * that would sit beside a real player's row).
             */
            Assert.IsTrue(full.StatsList.Any(s => s.Name == "Kuro"), "an owner-less name keeps its own row");
            Assert.IsFalse(full.StatsList.Any(s => s.Name == "Kuro +Pets"), "no aggregate row without an owner");

            // 3) Class column and the class map (frenzy verb = berserker WITHOUT an identity claim; a versioned
            //    class-spell cast claims both).
            Assert.AreEqual("Berserker", Row(full, "Corvyn").ClassName);
            Assert.AreEqual("Cleric", Row(full, "Bryn").ClassName);
            Assert.AreEqual("Druid", Row(full, "Vael").ClassName);
            Assert.AreEqual("Berserker", full.PlayerClasses["Corvyn"]);

            // 4) the operator's group numbers ride the rows they were assigned to.
            Assert.AreEqual(1, Row(full, "Akira +Pets").AssignedGroup);
            Assert.AreEqual(2, Row(full, "Bryn").AssignedGroup);

            // 5) the pane's time choosers: re-slice the RETAINED pool, no new selection, three windows. The numbers
            //    here are the contract for any windowed refresh.
            states.Clear();
            dataPoints.Clear();
            var narrow = new GenerateStatsOptions { AllRanges = all, MinSeconds = 0, MaxSeconds = 6, Source = "golden window 0..6" };
            builder.RebuildTotalStats(narrow, reset: true);
            Snapshot(snapshot, "window-0-6", builder.GetLastStats()?.CombinedStats);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);

            states.Clear();
            dataPoints.Clear();
            var middle = new GenerateStatsOptions { AllRanges = all, MinSeconds = 3, MaxSeconds = 9, Source = "golden window 3..9" };
            builder.RebuildTotalStats(middle, reset: true);
            Snapshot(snapshot, "window-3-9", builder.GetLastStats()?.CombinedStats);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);

            states.Clear();
            dataPoints.Clear();
            var wide = new GenerateStatsOptions { AllRanges = all, MinSeconds = -1, MaxSeconds = -1, Source = "golden widen back" };
            builder.RebuildTotalStats(wide, reset: true);
            Snapshot(snapshot, "window-widened", builder.GetLastStats()?.CombinedStats);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);

            // Widening from the retained pool must land back on the whole-capture answer.
            Assert.AreEqual(full.RaidStats.Total, builder.GetLastStats()!.CombinedStats!.RaidStats.Total,
                "the retained pool has to still hold everything after being re-sliced narrower");

            BoardGolden.CompareOrWrite(GoldenFile, ActualFile, "damage", snapshot.ToString());
        }
        finally
        {
            builder.EventsGenerationStatus -= OnStatus;
            builder.EventsUpdateDataPoint -= OnData;

            // Whatever failed, leave the board this run produced next to the golden: a structural assertion that
            // trips should still tell the reader what the row actually said.
            File.WriteAllText(BoardGolden.FixturePath(ActualFile), snapshot.ToString());
        }
    }


    // A name can live in any of the three views (aggregate rows only in StatsList, children in all of them), so a
    // lookup for an assertion asks all three rather than silently returning null on a row that exists.
    private static PlayerStats Row(CombinedStats stats, string name) =>
        (stats.ExpandedStatsList.FirstOrDefault(s => s.Name == name)
            ?? stats.StatsList.FirstOrDefault(s => s.Name == name)
            ?? stats.Children.Values.SelectMany(c => c).FirstOrDefault(s => s.Name == name))
        ?? throw new AssertFailedException($"no row named '{name}' on the board: " +
            $"[{string.Join(", ", stats.StatsList.Select(s => s.Name))}]");

    /*
     * Raid invariants that hold whatever the numbers are. Each one fails with a reason a reader can act on, which is
     * what a golden alone cannot say ("row 7 differs" does not mean "a row fell out of the aggregate").
     */
    private static void AssertInvariants(CombinedStats stats)
    {
        Assert.IsTrue(stats.RaidStats.Total > 0);

        var top = stats.StatsList.Where(s => s.IsTopLevel).ToList();
        Assert.AreEqual(top.Count, stats.StatsList.Count, "StatsList holds top-level rows only");
        Assert.AreEqual(stats.RaidStats.Total, top.Sum(s => s.Total), "the raid line is the sum of the displayed rows");

        // Display order IS total-descending in both lists; that is what the grid binds.
        BoardGolden.IsSortedByTotal(stats.StatsList, "StatsList");
        BoardGolden.IsSortedByTotal(stats.ExpandedStatsList, "ExpandedStatsList");

        /*
         * Rank is assigned by ONE walk of the expanded list (which interleaves pet children with the rows), so a
         * childless row that also sits in the expanded list carries THAT position, not its own list's — StatsList[5]
         * can read rank 7. Not visible in the grid (no Rank column) but `StatsFormatter` prints p.Rank for the overlay,
         * so it is frozen here rather than smoothed over: see docs/DesignNotes.md ("Which list ranks a row").
         */
        var expandedRanks = stats.ExpandedStatsList.Select(s => (int)s.Rank).ToList();
        CollectionAssert.AreEqual(Enumerable.Range(1, expandedRanks.Count).ToList(), expandedRanks,
            "the expanded list is ranked 1..N in its own order");

        // every child appears exactly once in the expanded list, under its parent's number, and shares add up
        foreach (var (parent, children) in stats.Children)
        {
            var parentRow = stats.StatsList.First(s => s.Name == parent);
            foreach (var child in children)
            {
                Assert.IsFalse(child.IsTopLevel, $"{child.Name} is folded under {parent}");
                Assert.AreEqual(1, stats.ExpandedStatsList.Count(s => s.Name == child.Name));
            }

            Assert.AreEqual(parentRow.Total, children.Sum(c => c.Total), $"{parent}'s children sum to it");
        }

        var shares = top.Sum(s => s.PercentOfRaid);
        Assert.AreEqual(100.0, shares, 0.5, "the % Total column accounts for the raid");

        foreach (var row in stats.ExpandedStatsList)
        {
            Assert.IsTrue(row.TotalSeconds > 0, $"{row.Name} reports seconds it was in the fight");
            Assert.AreEqual((double)row.Dps, Math.Round((double)row.Total / row.TotalSeconds), 1.0, $"{row.Name} Dps = Total / seconds");
        }
    }

    private static void Snapshot(StringBuilder sb, string section, CombinedStats? stats)
    {
        sb.Append("### ").Append(section).Append('\n');
        if (stats is null)
        {
            sb.Append("(no board)\n\n");
            return;
        }

        sb.Append("title\t").Append(stats.FullTitle).Append('\t').Append(stats.ShortTitle).Append('\n');
        sb.Append(BoardGolden.StatsLine("raid", stats.RaidStats)).Append('\n');
        sb.Append("uniqueClasses\t").Append(string.Join(",", stats.UniqueClasses.OrderBy(c => c, StringComparer.Ordinal))).Append('\n');

        /*
         * Three views of the same board, all frozen:
         *   list    — StatsList, the top-level rows (this is where an aggregate `X +Pets` row lives);
         *   flat    — ExpandedStatsList, the list a surface walks (a parent with children contributes its CHILDREN,
         *             not itself — which is why both are needed and why rank can differ between them);
         *   children— the tree with each child's share of its own parent.
         */
        foreach (var row in stats.StatsList)
        {
            sb.Append(BoardGolden.StatsLine("list", row)).Append('\n');
        }

        foreach (var (parent, children) in stats.Children.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var parentTotal = stats.StatsList.FirstOrDefault(s => s.Name == parent)?.Total ?? 0;
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                sb.Append("child\tparent=").Append(parent)
                  .Append("\torder=").Append(i)
                  .Append("\tname=").Append(child.Name)
                  .Append("\ttotal=").Append(child.Total)
                  .Append("\tshareOfParent=").Append(BoardGolden.F(parentTotal == 0 ? 0 : 100.0 * child.Total / parentTotal))
                  .Append('\n');
            }
        }

        foreach (var row in stats.ExpandedStatsList)
        {
            var tag = row.IsTopLevel ? "flat" : "flat-child";
            sb.Append(BoardGolden.StatsLine(tag, row)).Append('\n');

            // Sub-stat rows are the expanded spell/melee breakdown: the key IS the display grouping, so a change in
            // how Dd/Dot keys get built must show here.
            foreach (var sub in row.SubStats.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                sb.Append(BoardGolden.SubLine("sub", row.Name, sub)).Append('\n');
            }
        }

        foreach (var cls in stats.PlayerClasses.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append("class\t").Append(cls.Key).Append('\t').Append(cls.Value).Append('\n');
        }

        sb.Append('\n');
    }


    /*
     * Every column the Damage Summary grid binds (its MappingNames), plus the structural flags behind the tree.
     * Floats are rounded to what the grid shows, so a formatter change cannot make this golden churn.
     */
}
