using System.Text;

namespace EQLogParser;

/*
 * The tanking board's golden: what the Tanking Summary actually displays, frozen as text.
 *
 * Same purpose as DamageBoardGoldenTest — a restructure of the builder (grouping by reference instead of copying every
 * block, one measurement applied to several rows instead of a walk per target) has to answer "did anyone's column
 * change?" without re-reading arithmetic. This board was worth freezing separately rather than trusting the damage
 * golden because it is built on DIFFERENT keys: the raid line counts the facts that LANDED ON our side, each row is
 * keyed by `record.Defender`, and there is no `X +Pets` fold at all — so a change to the shared parts (the block
 * grouping, UpdateDamageStats, the activity ranges) can leave the damage board byte-identical and move this one.
 *
 * What is frozen:
 *   - the raid line and every row: totals, Dps/Sdps, %, seconds, hits, max/best-second, the average and rate columns,
 *     % Rampage and Special (both bound by this grid), the Class column and the PlayerClasses map;
 *   - the sub-stat rows behind an expanded row (the spell/melee lines a defender took);
 *   - the DamageType dial — all / melee only / spell only — which is a REFRESH case, not a re-selection: the pane's
 *     toolbar re-slices the groups in hand through RebuildTotalStats, and the two halves must partition the whole;
 *   - the pane's time window applied to the retained pool, then widened back out;
 *   - the events (the "repaint me" sequence the chart consumes).
 *
 * Regenerating after an INTENTIONAL change:
 *     EQLP_GOLDEN_WRITE=1 dotnet test EQLogParser.Test --filter TankingBoardGolden
 *     cp EQLogParser.Test/bin/Debug/net10.0/mini-data/board/tanking-board.actual.txt \
 *        EQLogParser.Test/data/board/tanking-board.golden.txt
 */
[TestClass]
[DoNotParallelize]
public class TankingBoardGoldenTest
{
    private const string Fixture = "tanking-board.txt";
    private const string GoldenFile = "tanking-board.golden.txt";
    private const string ActualFile = "tanking-board.actual.txt";

    // The six DamageValidator knobs are process-static and other tests flip them; a golden needs one known setting.
    private bool _ass, _bane, _ds, _fb, _hs, _su;
    private Func<string, bool>? _originalIsValidClass;

    [TestInitialize]
    public void Setup()
    {
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

        CombatRecordLookup.IsValidClassName = _originalIsValidClass ?? (_ => false);
        DamageLineParser.ResetProcessState();
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();
    }

    /*
     * The tanking builder keeps no "last event" accessor of its own (its pane reads the event it is handed) and no assigned
     * group map — group numbers are a Damage Summary feature, so `group=0` on every line here is the board's real answer.
     */
    // The production door for a click: classify, project the rows, stamp sections, materialize both directions.
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
        foreach (var row in rows)
        {
            if (!double.IsNaN(row.BeginTankingTime)) range.Add(new TimeSegment(row.BeginTankingTime, row.LastTankingTime));
        }

        return range;
    }

    [TestMethod]
    public void TankingBoard_FullBuild_MatchesGolden()
    {
        var run = PipelineHarness.RunFileDerived(BoardGolden.FixturePath(Fixture));
        var rows = Rows(run);

        var builder = TankingStatsBuilder.Instance;
        var states = new List<string>();
        var dataPoints = new List<string>();
        var snapshot = new StringBuilder();

        StatsGenerationEvent? last = null;
        void OnStatus(StatsGenerationEvent e)
        {
            states.Add($"{e.Type}|{e.State}|groups={e.Groups.Count}|uniqueGroups={e.UniqueGroupCount}|limited={e.Limited}");
            if (e.CombinedStats is not null) last = e;
        }
        void OnData(DataPointEvent e) =>
            dataPoints.Add($"{e.Action}|points={(e.Iterator == null ? -1 : e.Iterator.Count())}" +
                $"|selected={e.Selected.Count}");

        builder.EventsGenerationStatus += OnStatus;
        builder.EventsUpdateDataPoint += OnData;
        try
        {
            var all = WholeRange(rows);
            Assert.IsTrue(all.TimeSegments.Count > 0, "the fixture must produce damage taken (rows with tanking seconds)");

            // 1) whole capture, every row, no damage-type filter.
            var fullOptions = new GenerateStatsOptions { AllRanges = all, Source = "tanking golden full" };
            fullOptions.Npcs.AddRange(rows);
            builder.BuildTotalStats(fullOptions);

            var full = last?.CombinedStats;
            Assert.IsNotNull(full, "the full build produced no board");

            Snapshot(snapshot, "full", full);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);
            AssertInvariants(full, rows);

            /*
             * Capture the NUMBERS now. CombinedStats is not a snapshot of state: its RaidStats IS the builder's own
             * `_raidTotals` object, which the next pass zeroes and refills, so a `full` held across a rebuild reads the
             * newest pass. The golden's text is immune (it was rendered before the next pass ran); an assertion is not —
             * and for the refresh work this aliasing is the thing to know: re-presenting mutates what every pane holds.
             */
            var fullTotal = full.RaidStats.Total;

            /*
             * 2) the DamageType dial. The toolbar re-slices the groups the builder kept — this is the refresh case, and it
             * is a PARTITION: melee + spell is the unfiltered board, because StatsUtil.IsMelee sorts every record into one
             * of the two and never both.
             */
            states.Clear();
            dataPoints.Clear();
            builder.RebuildTotalStats(new GenerateStatsOptions { DamageType = 1, Source = "tanking golden melee" });
            var melee = last?.CombinedStats;
            var meleeTotal = melee?.RaidStats.Total ?? -1;
            Snapshot(snapshot, "type-melee", melee);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);

            states.Clear();
            dataPoints.Clear();
            builder.RebuildTotalStats(new GenerateStatsOptions { DamageType = 2, Source = "tanking golden spell" });
            var spell = last?.CombinedStats;
            var spellTotal = spell?.RaidStats.Total ?? -1;
            Snapshot(snapshot, "type-spell", spell);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);

            Assert.IsNotNull(melee);
            Assert.IsNotNull(spell);
            Assert.AreEqual(fullTotal, meleeTotal + spellTotal,
                "melee and spell are a partition of the unfiltered tanking board");

            /*
             * 3) the time window over the RETAINED pool, then widened back out: what the pane's sliders do without asking
             * the capture again, and the property any re-presentable pool has to keep.
             */
            states.Clear();
            dataPoints.Clear();
            builder.RebuildTotalStats(new GenerateStatsOptions { DamageType = 0, MinSeconds = 0, MaxSeconds = 6, Source = "tanking golden window 0..6" });
            Snapshot(snapshot, "window-0-6", last?.CombinedStats);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);

            states.Clear();
            dataPoints.Clear();
            builder.RebuildTotalStats(new GenerateStatsOptions { DamageType = 0, MinSeconds = -1, MaxSeconds = -1, Source = "tanking golden widen back" });
            Snapshot(snapshot, "window-widened", last?.CombinedStats);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);

            Assert.AreEqual(fullTotal, last!.CombinedStats!.RaidStats.Total,
                "widening from the retained pool has to land back on the whole-capture answer");

            BoardGolden.CompareOrWrite(GoldenFile, ActualFile, "tanking", snapshot.ToString());
        }
        finally
        {
            builder.EventsGenerationStatus -= OnStatus;
            builder.EventsUpdateDataPoint -= OnData;
            File.WriteAllText(BoardGolden.FixturePath(ActualFile), snapshot.ToString());
        }
    }

    /*
     * Invariants: each says what a diff MEANS, so the golden does not have to be the only reader.
     */
    private static void AssertInvariants(CombinedStats stats, IReadOnlyList<Fight> rows)
    {
        Assert.IsTrue(stats.RaidStats.Total > 0);

        BoardGolden.IsSortedByTotal(stats.StatsList, "StatsList");

        // Rank here is one walk of StatsList (this board has no pet children to interleave).
        var ranks = stats.StatsList.Select(s => (int)s.Rank).ToList();
        CollectionAssert.AreEqual(Enumerable.Range(1, ranks.Count).ToList(), ranks, "the tanking rows are ranked 1..N");

        /*
         * No folding on this board: a pet being beaten on is its own row, because TankingStatsBuilder keys by record.Defender
         * and never asks who owns the name (see DesignNotes — adding a +Pets fold here would be a new decision, not a cleanup).
         */
        Assert.AreEqual(0, stats.Children.Count, "the tanking board folds nothing into an aggregate row");

        /*
         * Who can BE a row here is a routing decision, not an accident: the tanking half of a row holds the facts that
         * landed on our side, and `IsRaidVictimAt` excludes pets on purpose (a pet being bitten is not its owner's damage
         * taken, and adding it would move every tank column by whatever the raid's summons ate — see DesignNotes,
         * "Damage our own side earns nothing"). Mobs are excluded for the opposite reason: a mob taking our hits is the
         * raid's OUTPUT, so it rides DamageBlocks. Both halves of that are frozen by the fixture.
         */
        Assert.IsFalse(stats.StatsList.Any(s => s.Name.EndsWith("`s pet", StringComparison.Ordinal)),
            "a pet being bitten is not raid damage taken (no pet row on this board)");
        Assert.IsTrue(stats.StatsList.All(s => !s.Name.StartsWith("a ", StringComparison.OrdinalIgnoreCase) &&
                                               !s.Name.StartsWith("an ", StringComparison.OrdinalIgnoreCase) &&
                                               !s.Name.StartsWith("the ", StringComparison.OrdinalIgnoreCase)),
            "damage dealt TO a mob belongs to the damage board, not the tanking one");

        // Every fact of every selected row lands on the board exactly once: this is what catches a block dropped while grouping.
        var selectedTankTotal = rows.Sum(r => r.TankTotal);
        Assert.AreEqual(selectedTankTotal, stats.RaidStats.Total,
            "the raid line is the sum of the materialized damage taken (a lost block shows up here)");

        // the raid line is also the sum of the displayed rows
        Assert.AreEqual(stats.RaidStats.Total, stats.StatsList.Sum(s => s.Total), "the raid line is the sum of the rows");

        foreach (var row in stats.StatsList)
        {
            Assert.AreEqual(row.Total, row.SubStats.Sum(s => s.Total), $"{row.Name}'s sub-stat rows sum to its total");

            /*
             * A row exists because SOMETHING landed on the name — which is not the same as `Hits` being non-zero: an
             * INVULNERABLE outcome carries no number and is not one of the hit types, so a player whose whole row is that
             * one line reads total 0, hits 0, invm 1 (Corvyn in this fixture). "An outcome taken with no number is still an
             * outcome" — see FightSummarySourceTest.
             */
            Assert.IsTrue(row.Total > 0 || row.MeleeAttempts > 0,
                $"{row.Name} reports neither damage nor an attempt — it has no reason to be a row");

            // Dps divides the ROW's own active seconds; Sdps divides the RAID's (CalculateRates' two denominators).
            Assert.AreEqual((double)row.Dps, Math.Round((double)row.Total / row.TotalSeconds), 1.0,
                $"{row.Name} Dps = Total / its own seconds");
            Assert.AreEqual((double)row.Sdps, Math.Round((double)row.Total / stats.RaidStats.TotalSeconds), 1.0,
                $"{row.Name} Sdps = Total / the raid's seconds");
        }

        // The class column is a registry read keyed by the row's name; the map holds the same answer.
        foreach (var row in stats.StatsList)
        {
            if (!string.IsNullOrEmpty(row.ClassName))
            {
                Assert.AreEqual(row.ClassName, stats.PlayerClasses[row.OrigName], $"{row.Name}: row and class map agree");
            }
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
        sb.Append(BoardGolden.StatsLine("raid", stats.RaidStats)).Append(BoardGolden.ExtraColumns(stats.RaidStats)).Append('\n');
        sb.Append("uniqueClasses\t").Append(string.Join(",", stats.UniqueClasses.OrderBy(c => c, StringComparer.Ordinal))).Append('\n');

        foreach (var row in stats.StatsList)
        {
            sb.Append(BoardGolden.StatsLine("list", row)).Append(BoardGolden.ExtraColumns(row)).Append('\n');

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
}
