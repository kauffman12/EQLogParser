namespace EQLogParser;

/*
 * What a healing block is allowed to hold.
 *
 * The board's retained pool is segment → block → actions, and every consumer reads it at that granularity: `RecordGroupCollection`
 * (the chart's points, stamped with the BLOCK's BeginTime), this builder's re-window pass (`block.BeginTime` against the chosen window),
 * and the rollups. That granularity is load-bearing in a way that is easy to miss: **a `HealRecord` carries no time of its own** — the
 * block IS its second. So bundling is only sound because every record in a block shares that exact second, and these tests hold that.
 *
 * The shape used to be one `ActionGroup` (plus the backing array its `Actions` list allocated on first Add) per heal LINE: measured on
 * `eqlog_Kizant_xegony-09-03-26.txt`, **2,643,370 blocks holding 2,643,370 actions**, where those heals live in only **10,153 distinct
 * seconds** — 2,238 ms and 766 MB allocated per whole-capture build, versus **1,409 ms and 503 MB** bundled (the A/B lives in
 * docs/DesignNotes.md). The healing golden proves the figures did not move; this file proves the pool's shape is what its readers assume.
 */
[TestClass]
[DoNotParallelize]
public class HealingBlockShapeTest
{
    private const string Fixture = "healing-board.txt";

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

    private sealed record Board(List<List<ActionGroup>> Groups, int Actions, long BoardTotal, Dictionary<double, int> InputHealsPerSecond);

    private static Board BuildBoard()
    {
        var run = PipelineHarness.RunFileDerived(BoardGolden.FixturePath(Fixture));
        ClassificationRules.Apply(run.Facts, run.Timeline);
        var index = new FightFactIndex();
        var rows = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
        Sectionizer.StampGroupIds(rows);

        var input = FightSummarySource.Build(rows, index, run.Facts);
        var range = new TimeRange();
        foreach (var fight in input.Fights) range.Add(new TimeSegment(fight.BeginDamageTime, fight.LastDamageTime));

        // The same source the derived board feeds the builder (see HealingStatsBuilder's window phase): a materialized, time-ascending list.
        var heals = HealSummarySource.Materialize(run.HealFacts, range);
        var perSecond = new Dictionary<double, int>();
        foreach (var heal in heals)
        {
            perSecond.TryGetValue(heal.Item1, out var n);
            perSecond[heal.Item1] = n + 1;
        }

        var options = new GenerateStatsOptions { AllRanges = range, Source = "block shape test", Heals = heals };
        foreach (var fight in input.Fights) options.Npcs.Add(fight);

        var builder = HealingStatsBuilder.Instance;
        List<List<ActionGroup>>? groups = null;
        var actions = 0;
        long boardTotal = -1;

        void OnStatus(StatsGenerationEvent e)
        {
            if (e.State != "COMPLETED") return;
            groups = [.. e.Groups];
            actions = e.Groups.Sum(g => g.Sum(b => b.Actions.Count));
            boardTotal = e.CombinedStats?.RaidStats.Total ?? -1;
        }

        builder.EventsGenerationStatus += OnStatus;
        try { builder.BuildTotalStats(options); }
        finally { builder.EventsGenerationStatus -= OnStatus; }

        Assert.IsNotNull(groups, "the build reported no completed board");
        return new Board(groups!, actions, boardTotal, perSecond);
    }

    /*
     * A block's BeginTime is what the chart stamps its points with and what a re-window filters on, so two laws follow from it: a second
     * is ONE block (a split second would survive a re-window as half its heals), and no block carries more records at its second than the
     * capture has heals at that second — which is what a wrong merge would do, silently moving heals into a foreign second.
     */
    [TestMethod]
    public void ABlockIsExactlyOneSecond()
    {
        var board = BuildBoard();
        Assert.IsTrue(board.Actions > 0, "the fixture must produce healed records for the shape to mean anything");

        var usedPerSecond = new Dictionary<double, int>();
        foreach (var segment in board.Groups)
        {
            var previous = double.NaN;
            foreach (var block in segment)
            {
                Assert.IsTrue(block.Actions.Count > 0, "an empty block is a second that healed nothing, kept anyway");
                Assert.IsFalse(!double.IsNaN(previous) && block.BeginTime == previous,
                  $"a second was split across two blocks at {block.BeginTime}");
                Assert.IsTrue(double.IsNaN(previous) || block.BeginTime > previous,
                    "blocks are handed out in time order within a segment");
                previous = block.BeginTime;

                usedPerSecond.TryGetValue(block.BeginTime, out var used);
                usedPerSecond[block.BeginTime] = used + block.Actions.Count;
            }
        }

        foreach (var (second, used) in usedPerSecond)
        {
            Assert.IsTrue(board.InputHealsPerSecond.TryGetValue(second, out var available),
              $"a block claims second {second}, which the healed input never contained");
            Assert.IsTrue(used <= available,
              $"second {second} holds {used} record(s) but only {available} heal(s) happened then");
        }
    }

    [TestMethod]
    public void BundlingNeverLosesAHeal()
    {
        var board = BuildBoard();

        var inBlocks = board.Groups.Sum(g => g.Sum(b => b.Actions.Count));
        Assert.AreEqual(board.Actions, inBlocks, "the count the board reported is the count the pool holds");

        // The pool the panes read and the figures they were built from must agree, record for record: the board's rows are summed
        // from these same actions, so a bundling bug that dropped or double-counted one shows up here as well as in the golden.
        // The raid row sums the same actions the pool holds.
        var summed = board.Groups.Sum(g => g.Sum(b => b.Actions.Sum(a => (long)((HealRecord)a).Total)));
        Assert.AreEqual(board.BoardTotal, summed,
          "the pool holds a different total than the board reports");
    }

    /*
     * Why bundling is worth anything: on a real capture one second carries many heals (2.6 M records over 10 k seconds), so there must be
     * FEWER blocks than actions. If this fixture ever stops exercising that, say so rather than pinning a shape one-block-per-record
     * would satisfy too.
     */
    [TestMethod]
    public void ASecondWithSeveralHealsIsOneBlock()
    {
        var board = BuildBoard();
        var blocks = board.Groups.Sum(g => g.Count);

        Assert.IsTrue(blocks <= board.Actions, "blocks are seconds, so they cannot outnumber records");
        if (blocks == board.Actions)
        {
            Assert.Inconclusive($"this fixture puts every heal in its own second ({board.Actions}); the bundling law needs a capture with " +
                                "same-second heals — see the real-log numbers in docs/DesignNotes.md before concluding it is unexercised");
        }

        Assert.IsTrue(board.Groups.SelectMany(g => g).Max(b => b.Actions.Count) > 1,
          "blocks outnumber nothing yet the totals differ");
    }
}
