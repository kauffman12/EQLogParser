using System.Diagnostics;

using EQLogParser;

namespace EQLogParser;

/*
 * What one damage-meter board costs, as the window it adds up grows — the question any faster refresh cadence has to answer,
 * because a meter reset at the start of a farm night repaints from EVERYTHING in that night, while a meter that expires on quiet
 * repaints from a few rows. Materialization and total are timed separately: materializing the facts behind the rows is the part
 * that scales with outcomes, and the rest (grouping, class rollups, sort) rides on top of it.
 *
 * The live-shaped windows here cost single-digit milliseconds; the pathological one is a window holding thousands of rows, which
 * is what a never-expiring meter accumulates over a long night. the derive cadence has to stay cheap relative to this.
 *
 * Runs only when EQLP_DERIVE_COST names a log:
 *   EQLP_DERIVE_COST=local/eqlog_Incogitable_xegony.txt dotnet test --filter MeterBoardCost --logger "console;verbosity=detailed"
 */
[TestClass]
[DoNotParallelize]
public class MeterBoardCostRealLogTest
{
    [TestMethod]
    public void MeterBoardCost_RealLog_WindowSweep()
    {
        var path = Resolve(Environment.GetEnvironmentVariable("EQLP_DERIVE_COST"));
        if (path is null) Assert.Inconclusive("set EQLP_DERIVE_COST=<log>");

        var run = PipelineHarness.RunFileDerived(path);
        var timeline = new EntityTimeline();
        var facts = run.Facts;
        var first = facts.Facts.Length > 0 ? facts.Facts[0].TimeS : 0;
        var last = facts.Facts.Length > 0 ? facts.Facts[^1].TimeS : 0;
        RegistrySeed.Apply(timeline, facts, first, last);
        ClassificationRules.Apply(facts, timeline, run.HealFacts);
        var index = new FightFactIndex(timeline);
        var rows = FightProjection.Build(facts, timeline, index.OnFact);
        Console.WriteLine($"[cost] {rows.Count} rows, {facts.Facts.Length:N0} facts");

        foreach (var (label, minutes) in new[] { ("0.5 min", 0.5), ("5 min", 5d), ("30 min", 30d), ("2 h", 120d), ("8 h", 480d), ("all", double.PositiveInfinity) })
        {
            var fromT = double.IsPositiveInfinity(minutes) ? double.NegativeInfinity : last - minutes * 60;
            var windowed = rows.Where(r => r.LastTime >= fromT).ToList();
            var hits = windowed.Sum(r => r.DamageHits + r.TankHits);

            for (var i = 0; i < 3; i++)
            {
                var materialize = Stopwatch.StartNew();
                var input = FightSummarySource.Build(windowed, index, facts, fromT, last);
                materialize.Stop();

                var board = Stopwatch.StartNew();
                var built = DerivedTotals.ForOverlay(windowed, index, facts, run.HealFacts, fromT, last);
                board.Stop();

                Console.WriteLine($"[cost] {label,8}: {windowed.Count,5} rows / {hits,9:N0} outcomes -> materialize {materialize.ElapsedMilliseconds,6} ms, "
                                  + $"whole board {board.ElapsedMilliseconds,6} ms ({built?.DamageStats?.StatsList?.Count ?? -1} players, "
                                  + $"{input.Fights.Count} fights)");
            }
        }
    }

    private static string? Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Path.IsPathRooted(path) || File.Exists(path)) return path;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, path);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
