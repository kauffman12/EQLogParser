namespace EQLogParser;

/*
 * A selection's order is not the grid's business, and the boards' title line proves when it was.
 *
 * Field report from `EQLogParser.log` (2026-10-08, a 998 MB capture): select all, and the name in the Damage Summary's
 * title changed on every gesture — "usually one of the first few in the list", never reliably the first. Legacy printed the
 * top row of the fight list 100% of the time. Two things were true at once: the builders re-sort the selection by `Id`
 * because a selection is counted in TIME order (row ids are handed out in the fight list's own display order,
 * `published[i].Id = i + 1`, so lowest id == first row), and the title was taken from `options.Npcs[0]` — the caller's RAW
 * input. Syncfusion enumerates `SelectedItems` in whatever sequence the gesture touched the rows, so the title was reading
 * selection mechanics instead of reading the list.
 *
 * The laws pinned here: the title is the first row of the fight list no matter what order the caller supplies; and the rest
 * of the board does not move at all when the order changes, because the sort was already doing its job.
 */
[TestClass]
[DoNotParallelize]
public class SelectionOrderTitleTest
{
    private const string Fixture = "damage-board.txt";

    [TestInitialize]
    public void Setup()
    {
        PipelineHarness.EnsureDataStore();
        DamageLineParser.ResetProcessState();
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();
        HealRecordSource.Current = null;
    }

    [TestCleanup]
    public void Cleanup()
    {
        DamageLineParser.ResetProcessState();
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();
    }

    // The production door for a click, same sequence the goldens use.
    private static (IReadOnlyList<Fight> Fights, TimeRange Range, List<(double, HealRecord)> Heals) Input(PipelineHarness.DeriveRunResult run)
    {
        ClassificationRules.Apply(run.Facts, run.Timeline);
        var index = new FightFactIndex();
        var rows = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
        Sectionizer.StampGroupIds(rows);

        var input = FightSummarySource.Build(rows, index, run.Facts);
        var range = new TimeRange();
        foreach (var row in input.Fights) range.Add(new TimeSegment(row.BeginDamageTime, row.LastDamageTime));

        // An EMPTY (not null) heal list is the derived door's own word for "this selection had no heals" - see
        // GenerateStatsOptions.Heals. The title law needs a board to be built, not healing to be counted.
        return (input.Fights, range, []);
    }

    private static GenerateStatsOptions Options(IReadOnlyList<Fight> fights, TimeRange range)
    {
        var options = new GenerateStatsOptions { AllRanges = range, Source = "order test" };
        foreach (var fight in fights) options.Npcs.Add(fight);
        return options;
    }

    /*
     * Scramble the input, keep the answer. `Build` returns the title plus a magnitude used as a canary: the point is that
     * ONLY determinism comes out of sorting, not different numbers, so a fix that made the title stable by changing what a
     * selection counts would fail here too.
     */
    private static (string Title, double Total) BuildTitleAndTotal(IReadOnlyList<Fight> fights, TimeRange range)
    {
        var builder = DamageStatsBuilder.Instance;
        builder.BuildTotalStats(Options(fights, range));

        var stats = builder.GetLastStats()?.CombinedStats;
        Assert.IsNotNull(stats, "the build produced no board at all");
        return (stats!.TargetTitle, stats.RaidStats.Total);
    }

    /*
     * The smallest selection that can tell the two readings apart: TWO rows whose names differ, so "the caller's first
     * element" and "the fight list's first row" are different words. (The fixture's whole-capture selection cannot see this —
     * its top and bottom rows are both `A training dummy`, which is why the first version of this test passed on the broken
     * code.) The rest of the list rides along in one order only to keep the canary total meaningful.
     */
    private static (List<Fight> Forward, List<Fight> Backward) DistinctEndPair(IReadOnlyList<Fight> fights)
    {
        var inListOrder = fights.OrderBy(f => f.Id).ToList();
        var top = inListOrder[0];
        var other = inListOrder.FirstOrDefault(f => f.Name != top.Name);
        Assert.IsNotNull(other, "the fixture needs two fight rows with different names to tell selection order apart");

        var forward = new List<Fight> { top, other };
        return (forward, new List<Fight> { other, top });
    }

    [TestMethod]
    public void AScrambledSelectionStillTitlesOnTheFirstFightInTheList()
    {
        var run = PipelineHarness.RunFileDerived(BoardGolden.FixturePath(Fixture));
        var (fights, range, _) = Input(run);
        var (forwardOrder, backwardOrder) = DistinctEndPair(fights);

        var expected = forwardOrder[0].Name;
        var forward = BuildTitleAndTotal(forwardOrder, range);
        var backward = BuildTitleAndTotal(backwardOrder, range);

        StringAssert.Contains(forward.Title, expected, "the title names the first row of the list");
        Assert.AreEqual(forward.Title, backward.Title,
          $"the title must not depend on how the caller enumerated the selection (wanted {expected} both ways)");
        Assert.AreEqual(forward.Total, backward.Total, 0.001,
          "only the title's determinism changes: what a selection counts is the same set either way");
    }

    [TestMethod]
    public void AMultiRowSelectionSaysCombinedAndNamesTheTopRow()
    {
        var run = PipelineHarness.RunFileDerived(BoardGolden.FixturePath(Fixture));
        var (fights, range, _) = Input(run);
        var (forwardOrder, backwardOrder) = DistinctEndPair(fights);

        var (title, _) = BuildTitleAndTotal(backwardOrder, range);

        StringAssert.Contains(title, $"Combined ({forwardOrder.Count})", "legacy's combined-count wording survives");
        StringAssert.EndsWith(title, forwardOrder[0].Name,
          "and the name beside it is the fight list's first row, not the gesture's first click");
    }

    /*
     * The same law on the other two boards. They carry a copy of the same three lines of code, so a fix that landed in one
     * builder only would leave Tanking Summary reading selection mechanics while Damage Summary read the list.
     */
    [TestMethod]
    public void TankingTitlesTheSameWayUnderAReversedSelection()
    {
        var run = PipelineHarness.RunFileDerived(BoardGolden.FixturePath(Fixture));
        var (fights, range, _) = Input(run);

        static string? TitleOf(TankingStatsBuilder builder, GenerateStatsOptions options)
        {
            string? title = null;
            void OnStatus(StatsGenerationEvent e)
            {
                if (e.CombinedStats != null) title = e.CombinedStats.TargetTitle;
            }

            builder.EventsGenerationStatus += OnStatus;
            try { builder.BuildTotalStats(options); }
            finally { builder.EventsGenerationStatus -= OnStatus; }
            return title;
        }

        var (forwardOrder, backwardOrder) = DistinctEndPair(fights);
        var forward = TitleOf(TankingStatsBuilder.Instance, Options(forwardOrder, range));
        var backward = TitleOf(TankingStatsBuilder.Instance, Options(backwardOrder, range));

        StringAssert.Contains(forward!, forwardOrder[0].Name, "the top row of the list, named");

        Assert.IsNotNull(forward);
        Assert.AreEqual(forward, backward, "the tanking board's title is the top row of the list, whatever order it was handed");
    }

    [TestMethod]
    public void HealingTitlesTheSameWayUnderAReversedSelection()
    {
        var run = PipelineHarness.RunFileDerived(BoardGolden.FixturePath(Fixture));
        var (fights, range, heals) = Input(run);

        static string? TitleOf(HealingStatsBuilder builder, GenerateStatsOptions options)
        {
            string? title = null;
            void OnStatus(StatsGenerationEvent e)
            {
                if (e.CombinedStats != null) title = e.CombinedStats.TargetTitle;
            }

            builder.EventsGenerationStatus += OnStatus;
            try { builder.BuildTotalStats(options); }
            finally { builder.EventsGenerationStatus -= OnStatus; }
            return title;
        }

        var (forwardOrder, backwardOrder) = DistinctEndPair(fights);
        var forwardOptions = Options(forwardOrder, range);
        forwardOptions.Heals = heals;
        var backwardOptions = Options(backwardOrder, range);
        backwardOptions.Heals = heals;

        var forward = TitleOf(HealingStatsBuilder.Instance, forwardOptions);
        var backward = TitleOf(HealingStatsBuilder.Instance, backwardOptions);

        Assert.IsNotNull(forward);
        Assert.AreEqual(forward, backward, "the healing board's title is the top row of the list too");
    }
}
