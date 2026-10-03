using System.Diagnostics;

using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * What the incremental derive pass is worth, on a capture big enough for the number to mean something.
 *
 * Runs only when EQLP_MIRROR_INCREMENT names a log:
 *   EQLP_MIRROR_INCREMENT=local/logs/live/eqlog_Incogitable_xegony.txt dotnet test --filter IncrementalPassIsCheaperThanARebuild --logger "console;verbosity=detailed"
 *   (EMU captures under local/logs/emu/ also need EQLP_EMU=1 - see MirrorRealLogBoardsTest's header)
 *
 * It separates the two halves of a derive pass, because they respond to different things:
 *
 *   CLASSIFY — EntityTimeline rebuilt from every fact and heal. NOT incremental: the rules count edges over the whole
 *   capture, and a count is not something you add to a previous answer without carrying that answer's intermediate
 *   tables. It is also the thing whose verdicts force a rebuild when they move.
 *
 *   PROJECT — the fold in FightProjection, which is what continues. Its cost per pass should stay flat in capture size
 *   while a rebuild grows with it.
 *
 * The tail is simulated by re-arriving the capture's own LAST facts one hour further along the clock: new ordinals in
 * new seconds, same names and labels, so the fold sees real content at real volume without the classification gaining a
 * single new verdict about anybody. If it ever does gain one, the gate refuses the carry and the assertion says so — the
 * safe direction, and a message that reads as "this row of numbers measured a full pass" rather than a wrong speedup.
 */
[TestClass]
public class MirrorIncrementBenchmarkTest
{
    private const long RestampS = 3_600;

    [TestMethod]
    public void IncrementalPassIsCheaperThanARebuild()
    {
        var log = Resolve(Environment.GetEnvironmentVariable("EQLP_MIRROR_INCREMENT"));
        if (log is null) Assert.Inconclusive("set EQLP_MIRROR_INCREMENT=local/eqlog_….txt to run this");

        var load = Stopwatch.StartNew();
        var run = PipelineHarness.RunFileWithMirror(log);
        var facts = run.Facts;
        var heals = run.HealFacts;
        var total = facts.FactCount;
        Console.WriteLine($"[increment] {log}: {total:N0} damage facts, {heals.HealCount:N0} heals, loaded in {load.ElapsedMilliseconds} ms");

        static (long Classify, long Project, int Rows) FullPass(DamageFactTable f, HealFactTable h)
        {
            var w = Stopwatch.StartNew();
            var timeline = BuildTimeline(f, h);
            var classify = w.ElapsedMilliseconds;
            w.Restart();
            var rows = FightProjection.Build(f, timeline);
            return (classify, w.ElapsedMilliseconds, rows.Count);
        }

        FullPass(facts, heals);                       // warm-up: JIT and first-touch pages are not the cost being reported
        var rebuild = FullPass(facts, heals);
        Console.WriteLine($"[increment] FULL pass: classify {rebuild.Classify} ms + project {rebuild.Project} ms " +
                          $"= {rebuild.Classify + rebuild.Project} ms over {rebuild.Rows} rows");

        foreach (var share in new[] { 0.001, 0.01, 0.05 })
        {
            var k = Math.Max(1, (int)(total * share));
            var cache = new FightProjection.FightProjectionCache();
            cache.Project(facts, BuildTimeline(facts, heals));           // the pass that fills the carried state

            /*
             * A tick with nothing new. Classification is still replayed in full here (the rules are aggregate — R7 counts
             * attack edges over the whole capture, R15 counts heal casters — so it has no incremental form yet); what this
             * line separates is the two halves: `classify` is that replay, and the number after `+` is the projection, which
             * is what continuation made cheap.
             */
            var idleTimeline = BuildTimeline(facts, heals, out var idleSeedMs, out var idleRulesMs);
            var idleWatch = Stopwatch.StartNew();
            var idleRows = cache.Project(facts, idleTimeline).Count;
            var idleMs = idleWatch.ElapsedMilliseconds;
            Assert.IsTrue(cache.LastPassContinued, "an unchanged capture must be a continuation");

            ArriveAgain(facts, k);
            var tailTimeline = BuildTimeline(facts, heals, out var tailSeedMs, out var tailRulesMs);
            var tailWatch = Stopwatch.StartNew();
            var tailRows = cache.Project(facts, tailTimeline);
            var tailMs = tailWatch.ElapsedMilliseconds;
            var carried = cache.LastPassContinued;

            Console.WriteLine($"[increment] +{k:N0} facts ({share:P1} of the capture): {(carried ? "continued" : "REBUILT")} — " +
                              $"projection {tailMs} ms + classify {tailSeedMs + tailRulesMs} ms (seed {tailSeedMs}, rules {tailRulesMs}); " +
                              $"quiet tick projection {idleMs} ms + classify {idleSeedMs + idleRulesMs} ms; " +
                              $"full rebuild {rebuild.Classify + rebuild.Project} ms (project {rebuild.Project}); rows {tailRows.Count} vs {idleRows}");

            /*
             * A live raid tail adds a few hundred to a few thousand facts between refreshes, and at those volumes the pass
             * continues. The 5 % line is not a live tail: duplicating ninety thousand hits re-writes the classifier's edge
             * counts, so a new verdict really does arrive and rebuilding is the CORRECT answer — which is why this line
             * reports rather than demands. What every share must satisfy is that the list ends up identical to a rebuild.
             */
            if (share <= 0.01)
                Assert.IsTrue(carried,
                    $"the gate refused a carry at {share:P1} new facts — live-tail volume, no new verdict about anybody — " +
                    "so this line measured a full pass and the feature is not working");

            // The continued list must be what a rebuild of the same facts and verdicts produces — asserted here at
            // capture scale because the fixtures in FightProjectionIncrementTest are too small to catch a missed
            // hundred thousand.
            var reference = FightProjection.Build(facts, BuildTimeline(facts, heals));
            Assert.AreEqual(reference.Count, tailRows.Count, $"{share:P1}: rows differ from a rebuild");
        }
    }

    /*
     * The harness runs with its bin directory as CWD (that is where EQDataStore looks for data/), so a relative
     * "local/….txt" from the command line has to be found by walking up to the repo instead.
     */
    private static string? Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (File.Exists(path)) return path;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, Path.GetFileName(path));
            if (File.Exists(candidate)) return candidate;
            var local = Path.Combine(dir.FullName, "local", Path.GetFileName(path));
            if (File.Exists(local)) return local;
        }

        return null;
    }

    private static EntityTimeline BuildTimeline(DamageFactTable facts, HealFactTable heals)
      => BuildTimeline(facts, heals, out _, out _);

    // Split so the report says which half of a pass costs what: `seed` is the known-player/known-npc seed,
    // `rules` is the aggregate rule book replayed over the whole capture (the part with no incremental form yet).
    private static EntityTimeline BuildTimeline(DamageFactTable facts, HealFactTable heals,
                                                out long seedMs, out long rulesMs)
    {
        var w = Stopwatch.StartNew();
        var timeline = new EntityTimeline();
        RegistrySeed.Apply(timeline, facts, 0, double.MaxValue);
        seedMs = w.ElapsedMilliseconds;
        w.Restart();
        ClassificationRules.Apply(facts, timeline, heals);
        rulesMs = w.ElapsedMilliseconds;
        return timeline;
    }

    // Re-write the last `count` facts as fresh arrivals, one hour on. Sequence numbers continue above everything the
    // capture used, so each copy is a new event to both the fold and the digest.
    private static void ArriveAgain(DamageFactTable table, int count)
    {
        /*
         * Snapshot the length first: appending grows the very count the loop is written against, so reading
         * `table.FactCount` in the condition would re-arrive facts forever (measured: it grows the array until the
         * capacity doubles past int and Array.Resize throws).
         */
        var through = table.FactCount;
        var seq = 2_000_000_000;
        for (var o = through - count; o < through; o++)
        {
            ref readonly var f = ref table.Facts[o];
            table.AddFact(new DamageFact(seq++, f.TimeS + RestampS, f.AtkIdx, f.DefIdx,
                                         f.Total, f.TypeId, f.Flags, f.ModMask, f.SubIdx));
        }
    }
}
