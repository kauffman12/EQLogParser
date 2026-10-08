using System.Text;

namespace EQLogParser;

/*
 * The healing board's golden: what the Healing Summary displays, frozen as text.
 *
 * Healing is built differently from the other two boards and that difference is the reason this file exists rather than
 * a third section of the damage golden: the records do not come from fight rows at all (a heal belongs to no fight), so
 * the builder takes a TIME WINDOW over the capture's heal rows, validates them (the 7-second group-AE / MGB rule needs
 * the previous seconds, which is why a window is walked in two passes), and keys its rows by HEALER with a second tree
 * keyed by the name that got healed (`SubStats2`). Restructuring the shared parts — record walking, `UpdateHealStats`,
 * the per-name registry reads — has to leave this board's bytes alone too, including which heals are refused.
 *
 * What is frozen:
 *   - the raid line and every healer row (totals, Dps/Sdps, %, seconds, hits, the Extra column the healing grid binds);
 *   - the spell lines behind a healer (`SubStats`), the healed-by tree (`SubStats2`) and its spell leaves;
 *   - the Class column and the PlayerClasses map;
 *   - the settings that change WHICH heals count (swarm-pet heals), rebuilt the way the settings dialog does it;
 *   - the pane's time window over the retained pool and widening back out;
 *   - the events, including the one `PopulateHealing` raises for the "who healed me" board.
 *
 * Regenerating after an INTENTIONAL change:
 *     EQLP_GOLDEN_WRITE=1 dotnet test EQLogParser.Test --filter HealingBoardGolden
 *     cp EQLogParser.Test/bin/Debug/net10.0/mini-data/board/healing-board.actual.txt \
 *        EQLogParser.Test/data/board/healing-board.golden.txt
 */
[TestClass]
[DoNotParallelize]
public class HealingBoardGoldenTest
{
    private const string Fixture = "healing-board.txt";
    private const string GoldenFile = "healing-board.golden.txt";
    private const string ActualFile = "healing-board.actual.txt";

    private bool _aoe, _swarm;
    private Func<string, bool>? _originalIsValidClass;

    [TestInitialize]
    public void Setup()
    {
        PipelineHarness.EnsureDataStore();
        _originalIsValidClass = CombatRecordLookup.IsValidClassName;
        CombatRecordLookup.IsValidClassName = name => EQDataStore.Instance.IsValidClassName(name);

        DamageLineParser.ResetProcessState();
        HealingLineParser.ClearCaches();   // the heal repeat-store is process state: re-parsing a fixture would double the board
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();
        HealRecordSource.Current = null;

        _aoe = AppSettings.IsAoEHealingEnabled;
        _swarm = AppSettings.IsHealingSwarmPetsEnabled;
        AppSettings.IsAoEHealingEnabled = true;
        AppSettings.IsHealingSwarmPetsEnabled = true;
    }

    [TestCleanup]
    public void Cleanup()
    {
        AppSettings.IsAoEHealingEnabled = _aoe;
        AppSettings.IsHealingSwarmPetsEnabled = _swarm;
        CombatRecordLookup.IsValidClassName = _originalIsValidClass ?? (_ => false);
        DamageLineParser.ResetProcessState();
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();
    }

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
            // A row nobody hit reports "never happened" as NaN, and a NaN span welds itself into whatever it touches —
            // silently shrinking the healing window until half the heals are outside it. Guarded, not smoothed over.
            if (!double.IsNaN(row.BeginDamageTime) && !double.IsNaN(row.LastDamageTime))
            {
                range.Add(new TimeSegment(row.BeginDamageTime, row.LastDamageTime));
            }
        }

        return range;
    }

    [TestMethod]
    public void HealingBoard_FullBuild_MatchesGolden()
    {
        var run = PipelineHarness.RunFileDerived(BoardGolden.FixturePath(Fixture));
        var rows = Rows(run);
        var all = WholeRange(rows);

        // The derived door: heals windowed by the selection's own span, because a heal belongs to no fight.
        var heals = HealSummarySource.Materialize(run.HealFacts, all);
        Assert.IsTrue(heals.Count > 0, "the fixture must produce heal records");

        var builder = HealingStatsBuilder.Instance;
        var states = new List<string>();
        var dataPoints = new List<string>();
        StatsGenerationEvent? last = null;
        var snapshot = new StringBuilder();

        void OnStatus(StatsGenerationEvent e)
        {
            states.Add($"{e.Type}|{e.State}|groups={e.Groups.Count}|limited={e.Limited}");
            if (e.CombinedStats is not null) last = e;
        }

        void OnData(DataPointEvent e) =>
            dataPoints.Add($"{e.Action}|points={(e.Iterator == null ? -1 : e.Iterator.Count())}|selected={e.Selected.Count}");

        builder.EventsGenerationStatus += OnStatus;
        builder.EventsUpdateDataPoint += OnData;
        try
        {
            // 1) the whole window, every row in the selection.
            var fullOptions = new GenerateStatsOptions { AllRanges = all, Heals = heals, Source = "healing golden full" };
            fullOptions.Npcs.AddRange(rows);
            builder.BuildTotalStats(fullOptions);

            var full = last?.CombinedStats;
            Assert.IsNotNull(full, "the full build produced no board");
            Snapshot(snapshot, "full", full);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);

            // Capture numbers now: RaidStats is the builder's own live row and the next pass refills it (see the tanking golden).
            var fullTotal = full.RaidStats.Total;
            var fullHealers = full.StatsList.Count;
            AssertInvariants(full);

            /*
             * 2) heals refused by the settings dialog. Toggling "heal swarm pets" cannot be done by re-slicing — the
             *    validation happens while the window is built — so the panes rebuild from the source, which is exactly what
             *    this calls: the pet being healed disappears, and every OTHER number has to stand.
             */
            states.Clear();
            dataPoints.Clear();
            AppSettings.IsHealingSwarmPetsEnabled = false;
            builder.BuildTotalStats(new GenerateStatsOptions { AllRanges = all, Heals = heals, Source = "healing golden no swarm pets" });
            var noPets = last?.CombinedStats;
            Snapshot(snapshot, "swarm-pets-off", noPets);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);

            Assert.IsNotNull(noPets);
            AppSettings.IsHealingSwarmPetsEnabled = true;

            /*
             * 4) the pane's time sliders over the RETAINED pool (RebuildTotalStats: same groups, narrower seconds), then
             *    widened back out. The widening is the property a re-presentable pool has to keep.
             */
            states.Clear();

            /*
             * 3) `PopulateHealing` is a DIFFERENT board over the same pool: rows keyed by the name that got healed (the
             *    Tanking Summary's "Healed" column reads it). It mutates the CombinedStats handed in, so it runs before the window steps, which re-slice the pool in place.
             */
            states.Clear();
            dataPoints.Clear();
            var widened = last!.CombinedStats!;
            var received = builder.PopulateHealing(widened);
            Snapshot(snapshot, $"populate-healing={received}", widened);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);
            Assert.IsTrue(received, "PopulateHealing needs the pool this build just made");
            dataPoints.Clear();
            builder.RebuildTotalStats(new GenerateStatsOptions { AllRanges = all, Heals = heals, MinSeconds = 0, MaxSeconds = 6, Source = "healing golden window 0..6" });
            Snapshot(snapshot, "window-0-6", last?.CombinedStats);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);

            /*
             * Widening needs a BUILD on this board, unlike the other two: HealingStatsBuilder re-slices `_healingGroups`
             * in place (only FireChartEvent's `reset` restores `_allHealingGroups`), so a narrower window throws away the
             * blocks and a wider one afterwards finds nothing. Documented here as text rather than fixed on the way past —
             * but it belongs to the refresh work: "re-present what we kept" only works if narrowing did not delete it.
             */
            states.Clear();
            dataPoints.Clear();
            builder.BuildTotalStats(new GenerateStatsOptions { AllRanges = all, Heals = heals, Source = "healing golden widen (rebuilt)" });
            Snapshot(snapshot, "window-widened", last?.CombinedStats);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);

            Assert.AreEqual(fullTotal, last!.CombinedStats!.RaidStats.Total,
                "widening from the retained pool has to land back on the whole-capture answer");
            Assert.AreEqual(fullHealers, last.CombinedStats!.StatsList.Count, "and the same healers");

            /*
             * An OBSERVATION this golden records rather than blesses: HealingSummary's sliders call RebuildTotalStats with
             * no `Heals`, and a null list means "every heal in the open capture". So after a slider move over a derived
             * SELECTION, the board's scope is the store rather than the span that was clicked — visible here because the
             * fixture's out-of-fight heals come back. Pinning it as text (not as law) keeps it findable for the refresh work,
             * where "which records does a re-present use?" has exactly one answer.
             */
            states.Clear();
            dataPoints.Clear();
            builder.BuildTotalStats(new GenerateStatsOptions { AllRanges = all, Source = "healing golden slider without a list" });
            Snapshot(snapshot, "no-heal-list-store-wide", last?.CombinedStats);
            BoardGolden.SnapshotEvents(snapshot, states, dataPoints);


            BoardGolden.CompareOrWrite(GoldenFile, ActualFile, "healing", snapshot.ToString());
        }
        finally
        {
            builder.EventsGenerationStatus -= OnStatus;
            builder.EventsUpdateDataPoint -= OnData;
            File.WriteAllText(BoardGolden.FixturePath(ActualFile), snapshot.ToString());
        }
    }

    private static void AssertInvariants(CombinedStats stats)
    {
        Assert.IsTrue(stats.RaidStats.Total > 0);
        BoardGolden.IsSortedByTotal(stats.StatsList, "StatsList");

        var ranks = stats.StatsList.Select(s => (int)s.Rank).ToList();
        CollectionAssert.AreEqual(Enumerable.Range(1, ranks.Count).ToList(), ranks, "the healing rows are ranked 1..N");

        Assert.AreEqual(stats.RaidStats.Total, stats.StatsList.Sum(s => s.Total), "the raid line is the sum of the rows");
        Assert.AreEqual(0, stats.Children.Count, "the healing board folds nothing into an aggregate row");

        foreach (var row in stats.StatsList)
        {
            Assert.AreEqual(row.Total, row.SubStats.Sum(s => s.Total), $"{row.Name}'s spell lines sum to its total");
            Assert.AreEqual(row.Total, row.SubStats2.Sum(s => s.Total), $"{row.Name}'s healed-by lines sum to its total");

            foreach (var healed in row.SubStats2)
            {
                Assert.AreEqual(healed.Total, healed.SubSubStats.Sum(s => s.Total),
                    $"{row.Name} -> {healed.Name}: the spell lines sum to that patient's total");
            }

            Assert.AreEqual((double)row.Dps, Math.Round((double)row.Total / row.TotalSeconds), 1.0,
                $"{row.Name} Dps = Total / its own seconds");
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
                sb.Append(BoardGolden.SubLine("spell", row.Name, sub)).Append('\n');
            }

            /*
             * The second tree: who this healer healed, and with what. This is the shape nothing else on the board shows,
             * and the one most likely to move if a window's two passes ever change what they hand the counters.
             */
            foreach (var healed in row.SubStats2.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                sb.Append(BoardGolden.SubLine("healed", row.Name, healed)).Append('\n');
                foreach (var leaf in healed.SubSubStats.OrderBy(s => s.Key, StringComparer.Ordinal))
                {
                    sb.Append(BoardGolden.SubLine("  spell-of", healed.Name, leaf)).Append('\n');
                }
            }
        }

        foreach (var cls in stats.PlayerClasses.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append("class\t").Append(cls.Key).Append('\t').Append(cls.Value).Append('\n');
        }

        sb.Append('\n');
    }
}
