using System.Diagnostics;

using EQLogParser;

namespace EQLogParser;

/*
 * What the record→DataPoint seam costs on the exact path the field log reports: materialize a whole-capture selection,
 * then walk it the way the damage board does (StatsBuildTrace's `walk` stage) and the way an open chart does (the same
 * iterator, aggregated by hand). Both numbers come from ONE enumeration shape, so a change to `RecordGroupCollection`
 * shows up here before anybody has to restart WPF to find it.
 *
 * The allocation column is the point, not a footnote: the collection hands out a `DataPoint` per record, and a 4.6 M-record
 * selection turns "one object per record" into hundreds of megabytes of garbage inside a single UI-thread pass. `bytes/record`
 * is printed next to the wall time for that reason; the walk's own share is on the trace line.
 *
 * Runs only when EQLP_BOARD_WALK names a log:
 *   EQLP_BOARD_WALK=local/logs/live/eqlog_Kizant_xegony-09-03-26.txt dotnet test --filter BoardWalkCost --logger "console;verbosity=detailed"
 */
[TestClass]
[DoNotParallelize]
public class BoardWalkCostRealLogTest
{
    [TestMethod]
    public void BoardWalkCost_RealLog_SelectAll()
    {
        var path = Resolve(Environment.GetEnvironmentVariable("EQLP_BOARD_WALK"));
        if (path is null) Assert.Inconclusive("set EQLP_BOARD_WALK=<log>");

        var run = PipelineHarness.RunFileDerived(path);
        var timeline = new EntityTimeline();
        var facts = run.Facts;
        var first = facts.Facts.Length > 0 ? facts.Facts[0].TimeS : 0;
        var last = facts.Facts.Length > 0 ? facts.Facts[^1].TimeS : 0;
        RegistrySeed.Apply(timeline, facts, first, last);
        ClassificationRules.Apply(facts, timeline, run.HealFacts);
        var index = new FightFactIndex(timeline);
        var rows = FightProjection.Build(facts, timeline, index.OnFact);

        var fromT = double.NegativeInfinity;

        // Everything the capture ever saw: the same selection the field log's `Select All` produces.
        var materialize = Stopwatch.StartNew();
        var input = FightSummarySource.Build(rows, index, facts, fromT, last);
        materialize.Stop();

        var damageRecords = input.Fights.Sum(static f => f.DamageBlocks.Sum(static b => b.Actions.Count));
        var tankRecords = input.Fights.Sum(static f => f.TankingBlocks.Sum(static b => b.Actions.Count));
        Console.WriteLine($"[walk] {path} | {facts.Facts.Length:N0} facts, {rows.Count} rows -> "
                          + $"{input.Fights.Count} fights / {damageRecords:N0} damage + {tankRecords:N0} taken records | materialize {materialize.ElapsedMilliseconds:N0} ms");

        // The way the builder walks it: every DamageBlock of the selection, one flat list in time order.
        var blocks = new List<ActionGroup>();
        foreach (var fight in input.Fights) blocks.AddRange(fight.DamageBlocks);
        blocks.Sort((a, b) => a.BeginTime.CompareTo(b.BeginTime));
        var groups = new List<List<ActionGroup>> { blocks };

        for (var pass = 1; pass <= 3; pass++)
        {
            // (1) The seam alone: what a chart's AddDataPoints pays per record (aggregate by name, keep no records).
            var sweep = Stopwatch.StartNew();
            var allocated0 = GC.GetTotalAllocatedBytes(precise: true);
            long seen = 0;
            double sum = 0;
            var perName = new Dictionary<string, double>();
            foreach (var point in new DamageGroupCollection(groups))
            {
                var name = point.PlayerName ?? point.Name;
                perName.TryGetValue(name, out var had);
                perName[name] = had + point.Total;
                sum += point.Total;
                seen++;
            }

            var sweepMs = sweep.ElapsedMilliseconds;
            var sweptBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated0;
            Console.WriteLine($"[walk] pass {pass} chart-shaped seam walk: {seen:N0} records in {sweepMs:N0} ms "
                              + $"({seen / Math.Max(1, sweepMs / 1000.0):N0} rec/s) | allocated {sweptBytes / 1024.0 / 1024.0:N1} MB "
                              + $"= {sweptBytes / (double)Math.Max(1, seen):N1} B/record | {perName.Count} names, sum {sum:N0}");
        }

        for (var pass = 1; pass <= 2; pass++)
        {
            // (2) The real board, through the door MainWindow uses: stages and per-stage allocation come from StatsBuildTrace.
            var allocated0 = GC.GetTotalAllocatedBytes(precise: true);
            var board = Stopwatch.StartNew();
            var options = new GenerateStatsOptions { Source = $"cost probe pass {pass}" };
            options.Npcs.AddRange(input.Fights);
            options.AllRanges = input.AllRanges;
            options.MinSeconds = 0;
            CombinedStats? built = null;
            void Captured(StatsGenerationEvent e) => built = e.CombinedStats ?? built;
            DamageStatsBuilder.Instance.EventsGenerationStatus += Captured;
            DamageStatsBuilder.Instance.BuildTotalStats(options);
            DamageStatsBuilder.Instance.EventsGenerationStatus -= Captured;
            board.Stop();

            if (pass == 2)
            {
                /* The hit-distribution histograms: filled per sub-stat row on EVERY build, read by exactly one window (HitFreqChart). */
                long histEntries = 0, histRows = 0;
                foreach (var row in built?.ExpandedStatsList ?? [])
                {
                    histEntries += row.CritFreqValues.Count + row.NonCritFreqValues.Count;
                    histRows++;
                    foreach (var sub in row.SubStats)
                    {
                        histEntries += sub.CritFreqValues.Count + sub.NonCritFreqValues.Count;
                        histRows++;
                    }
                }

                Console.WriteLine($"[walk] pass {pass} hit-frequency histograms: {histEntries:N0} entries over {histRows:N0} rows "
                                  + $"(~{histEntries * 40.0 / 1048576.0:N0} MB of dictionary retained) | read only by HitFreqChart");
            }
            var boardBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated0;

            Console.WriteLine($"[walk] pass {pass} damage board: {board.ElapsedMilliseconds:N0} ms total, "
                              + $"{boardBytes / 1024.0 / 1024.0:N1} MB allocated | {StatsBuildTrace.LastFinishedLineOf("damage")}");
        }

        // Tanking on the same rows: it walks the other ordinal set through the same seam.
        var tankAlloc0 = GC.GetTotalAllocatedBytes(precise: true);
        var tankBoard = Stopwatch.StartNew();
        var tankOptions = new GenerateStatsOptions { Source = "cost probe tanking" };
        tankOptions.Npcs.AddRange(input.Fights);
        tankOptions.AllRanges = input.AllRanges;
        tankOptions.MinSeconds = 0;
        TankingStatsBuilder.Instance.BuildTotalStats(tankOptions);
        tankBoard.Stop();
        Console.WriteLine($"[walk] tanking board: {tankBoard.ElapsedMilliseconds:N0} ms, "
                          + $"{(GC.GetTotalAllocatedBytes(precise: true) - tankAlloc0) / 1024.0 / 1024.0:N1} MB allocated | "
                          + $"{StatsBuildTrace.LastFinishedLineOf("tanking")}");

        Console.WriteLine($"[walk] gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} "
                          + $"| heap now {GC.GetTotalMemory(false) / 1024.0 / 1024.0:N0} MB");
    }

    private static string? Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Path.IsPathRooted(path) || File.Exists(path)) return path;
        foreach (var dir in new[] { "local", "local/logs/live", "../../local/logs/live" })
        {
            var candidate = Path.Combine(dir, Path.GetFileName(path));
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
