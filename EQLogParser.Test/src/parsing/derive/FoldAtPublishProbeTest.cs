using System.Diagnostics;
using System.Text.RegularExpressions;

using EQLogParser;

namespace EQLogParser;

/*
 * What "fold at publish" costs, measured on real captures - the number that decides whether layer 2 gets built. (2026-11)
 *
 * Layer 1 (docs/DesignNotes.md -> "Layer 1 is built: a grown row continues instead of re-walking") stopped re-MATERIALIZING the
 * records of a row that only grew. The sibling term is the builder's `walk` - counting those records into `PlayerStats` - and it is
 * untouched: a touched row still counts its whole night. Reusing whole rows instead was measured at ~20 % in raid content and ~35 %
 * in group content (docs/DesignNotes.md -> "How much a whole-row cache would actually save"), because the rows that change are the
 * ones with all-night history, while the saving available on a 5-second refresh is ~99 %. The proposal that reaches further carries
 * each row's accumulated CELLS and folds the new tail into them: `StatsUtil.MergeStatsRecursive` exists and is proven against one
 * uninterrupted count (`RecursiveFoldEquivalenceTest`), so the fold primitive is not the open question.
 *
 * The open question is the publish step, and it is what this probe answers. A cached cell may not BE the displayed row - the grid binds
 * `PlayerStats`, and mutating a carried object across passes is the failure that kills naive carry-forward - so every pass must produce
 * a FRESH tree out of `cell + tail`. That should cost something proportional to the cell's keys, ranges and children rather than to its
 * records... unless carrying the hit distribution turns out to be required, which is where the arms below part company.
 *
 * Three arms per row, over real published rows (the board comes from the real builder on a whole-capture selection):
 *   COLD  = count the row's R records through the walk's own calls into a fresh row - what happens today;
 *   FOLD  = merge the carried cell into a fresh row, then count the tail's N records and merge those too - what layer 2 would do;
 *   HIST  = copying the cell's raw hit amounts on top of FOLD - what layer 2 costs if the Hit Frequency chart has to survive a cached
 *           build, because `MergeStatsRecursive` carries NO histogram today (it merges counters, ranges, children, resists, specials and
 *           deaths; `Attempt`'s raw amount lists have no merge term at all).
 * The fold arm double-counts its tail by construction here - the carried cell is a whole-capture build, so that window is already inside
 * it. Nothing timed is inflated: node counts, range segments and tail length are what they would be for a cell closed before the window,
 * and N is exactly the increment.
 *
 * The recount mirror's fidelity is asserted rather than assumed: on a plain top-level row (no `+Pets` aggregate, no group header) COLD
 * must reproduce the published row's Total and Hits EXACTLY - otherwise this probe is timing a broken loop and says so out loud.
 *
 *   EQLP_FOLD_PUBLISH=local/logs/live/eqlog_Kizant_xegony-09-03-26.txt dotnet test --filter FoldAtPublish --logger "console;verbosity=detailed"
 *   EQLP_EMU=1 EQLP_FOLD_PUBLISH=local/logs/emu/eqlog_Roper_thj.txt ...       (group content: the shape fold may not pay for)
 *   EQLP_FOLD_WINDOW=5,30                                                     (seconds; default 5,30, taken MID-capture)
 *
 * Windows are anchored mid-capture on purpose: both reference captures end in a long idle stretch, so anchoring on the newest fact
 * reports a saving nobody will ever see in play - the mistake the ceiling probe documents.
 */
[TestClass]
[DoNotParallelize]
public class FoldAtPublishProbeTest
{
    private static readonly Regex WalkPhase = new(@"walk (\d+) ms", RegexOptions.Compiled);

    /* One row's measurement: the arms' times and allocations, and the shape that explains them. */
    private sealed class Row
    {
        public string Name = "";
        public long Records;          // R: everything the row owns today
        public long Tail;             // N: what the window adds
        public int Nodes;             // cells in the published tree (row + sub-stat rows, every depth)
        public int Segments;          // TimeRange segments a merge has to union
        public int Amounts;           // raw hit amounts held for the Hit Frequency chart
        public int ScratchNodes;      // cells whose best-second scratch is still open (a fold refuses those)
        public double ColdMs;
        public double FoldMs;
        public double MergeCellMs;    // fold arm split: carried cell -> fresh row
        public double CountTailMs;    // fold arm split: counting the N
        public long ColdBytes;
        public long FoldBytes;
        public double HistMs;
        public bool Aggregate;          // the records reached this row through its owner's `+Pets` row
        public bool FidelityChecked;
        public bool FidelityMatched;
    }

    [TestMethod]
    public void FoldAtPublish_WhatRepublishingACarriedCellCosts()
    {
        var path = Resolve(Environment.GetEnvironmentVariable("EQLP_FOLD_PUBLISH"));
        if (path is null) Assert.Inconclusive("set EQLP_FOLD_PUBLISH=<log>");

        var run = PipelineHarness.RunFileDerived(path!);
        var facts = run.Facts;
        if (facts.Facts.Length == 0) Assert.Inconclusive("the capture holds no damage facts");

        var timeline = new EntityTimeline();
        var first = facts.Facts[0].TimeS;
        var last = facts.Facts[^1].TimeS;
        RegistrySeed.Apply(timeline, facts, first, last);
        ClassificationRules.Apply(facts, timeline, run.HealFacts);
        var index = new FightFactIndex(timeline);
        var rows = FightProjection.Build(facts, timeline, index.OnFact);

        // One whole-capture selection: what a select-all builds, and where both arms get their records from.
        var input = FightSummarySource.Build(rows, index, facts, first, last);

        // Every materialized record filed under the name its own line named - the attribution the ceiling probe used too, which means
        // folding onto `X +Pets` is NOT modelled (a stated approximation: it moves records between two rows, it does not add any).
        var recordsByName = new Dictionary<string, List<DamageRecord>>(StringComparer.OrdinalIgnoreCase);
        long totalRecords = 0;
        foreach (var fight in input.Fights)
        {
            foreach (var block in fight.DamageBlocks)
            {
                foreach (var action in block.Actions)
                {
                    if (action is not DamageRecord record || string.IsNullOrEmpty(record.Attacker)) continue;
                    if (!recordsByName.TryGetValue(record.Attacker, out var list)) recordsByName[record.Attacker] = list = [];
                    list.Add(record);
                    totalRecords++;
                }
            }
        }

        // The real board, twice: the first build warms dictionaries and JIT, the second's trace line is the one read.
        DerivedTotals.ForOverlay(rows, index, facts, run.HealFacts, first, last);
        var overlay = DerivedTotals.ForOverlay(rows, index, facts, run.HealFacts, first, last);
        List<PlayerStats> published = overlay?.DamageStats?.StatsList ?? [];

        var cellsByName = new Dictionary<string, PlayerStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var stats in published)
        {
            if (!string.IsNullOrEmpty(stats.Name)) cellsByName.TryAdd(stats.Name, stats);
        }

        var walkMs = 0d;
        var traceLine = StatsBuildTrace.LastFinishedLineOf("damage");
        if (traceLine is not null)
        {
            var match = WalkPhase.Match(traceLine);
            if (match.Success) walkMs = double.Parse(match.Groups[1].Value);
        }

        Console.WriteLine($"[foldpub] {Path.GetFileName(path)}: {facts.Facts.Length:N0} facts, {rows.Count} fight rows, "
                          + $"{totalRecords:N0} materialized records over {recordsByName.Count} names");
        Console.WriteLine($"[foldpub] board built: {published.Count} published rows; builder walk = {walkMs:N0} ms "
                          + $"({(totalRecords > 0 ? walkMs * 1000.0 / totalRecords : 0):F2} us/record over the whole capture)");
        Console.WriteLine($"[foldpub] if every published row were carried as a cell: {StoreFootprint(cellsByName)}");

        // A published row is not automatically a cacheable cell: the fold refuses undrained best-second scratch (precondition 3 of the
        // cell design), and this counts what the real builder actually leaves behind on the rows the grid is binding right now.
        var undrainedRows = 0;
        long undrainedCells = 0, allCells = 0;
        foreach (var stats in published)
        {
            if (stats.BestSecTemp != 0) undrainedRows++;
            var probe = new Row();
            Shape(stats, probe);
            undrainedCells += probe.ScratchNodes;
            allCells += probe.Nodes;
        }

        Console.WriteLine($"[foldpub] cells needing a scratch drain before they may be carried: {undrainedRows}/{published.Count} rows, "
                          + $"{undrainedCells:N0}/{allCells:N0} cells (the fold's guard is recursive, so the drain has to be too)");

        // Warm both arms: they are hot loops, and a cold JIT would read as cost.
        var warm = Count([]);
        var warmTarget = new PlayerStats { Name = "warm" };
        StatsUtil.MergeStatsRecursive(warmTarget, warm);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        long[] scratch = [];
        // Several anchors, because a capture's middle can be idle: one anchor is a degenerate sample waiting to be misread as a shape.
        foreach (var fraction in Anchors())
        {
            var mid = facts.Facts[(int)Math.Clamp((long)(facts.Facts.Length * fraction), 0L, facts.Facts.Length - 1)].TimeS;
            foreach (var windowSeconds in Windows())
            {
            var from = mid - windowSeconds;

            // The increment, by name, taken off the block's own second (a record carries no timestamp of its own).
            var tails = TailsByName(input, from, mid);

            var measured = new List<Row>();
            long touchedNames = 0, touchedRecords = 0, tailRecords = 0, unmatchedNames = 0, unmatchedRecords = 0;

            foreach (var (name, tail) in tails)
            {
                touchedNames++;
                tailRecords += tail.Count;
                var owned = recordsByName[name];
                var recordCount = owned.Count;
                touchedRecords += recordCount;

                // A summon's records live in its OWNER's row (`X +Pets`), exactly as the damage builder folds them; matching only the bare
                // name would report "no row of its own" for every pet and make the coverage look far worse than it is.
                var cell = (PlayerStats?)null;
                var viaAggregate = false;
                if (cellsByName.TryGetValue(name, out var own))
                {
                    cell = own;
                }
                else if (cellsByName.TryGetValue($"{name} +Pets", out var folded))
                {
                    cell = folded;
                    viaAggregate = true;
                }
                else
                {
                    unmatchedNames++;
                    unmatchedRecords += recordCount;
                    continue;
                }

                var m = new Row { Name = name, Records = recordCount, Tail = tail.Count, Aggregate = viaAggregate };
                Shape(cell, m);

                // What a carry costs that the arms cannot show on their own: closing the running second of every cell of a row that is
                // currently bound to the grid. The drain is recursive because the fold's guard is: a sub-stat row holds one too.
                Drain(cell);

                var coldWatch = Stopwatch.StartNew();
                var coldAlloc = GC.GetAllocatedBytesForCurrentThread();
                var recounted = Count(owned);
                coldWatch.Stop();
                m.ColdMs = coldWatch.Elapsed.TotalMilliseconds;
                m.ColdBytes = GC.GetAllocatedBytesForCurrentThread() - coldAlloc;

                // Fidelity: a plain top-level row must come back identical, or this mirror is not the walk.
                if (!cell.IsGroupHeader && !viaAggregate
                    && string.Equals(cell.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    m.FidelityChecked = true;
                    m.FidelityMatched = recounted.Total == cell.Total && recounted.Hits == cell.Hits;
                }

                var foldWatch = Stopwatch.StartNew();
                var foldAlloc = GC.GetAllocatedBytesForCurrentThread();
                var lap = Stopwatch.StartNew();
                var republished = new PlayerStats { Name = name };
                StatsUtil.MergeStatsRecursive(republished, cell);       // the publish step: carried cell -> fresh tree
                m.MergeCellMs = lap.Elapsed.TotalMilliseconds;
                var tailRow = Count(tail);                              // count only the increment
                StatsUtil.MergeStatsRecursive(republished, tailRow);
                lap.Stop();
                m.CountTailMs = lap.Elapsed.TotalMilliseconds - m.MergeCellMs;
                foldWatch.Stop();
                m.FoldMs = foldWatch.Elapsed.TotalMilliseconds;
                m.FoldBytes = GC.GetAllocatedBytesForCurrentThread() - foldAlloc;

                // What the histogram would ADD, if a cached build had to keep the distribution: copying every amount once.
                if (m.Amounts > 0)
                {
                    if (scratch.Length < m.Amounts) scratch = new long[m.Amounts];
                    var histWatch = Stopwatch.StartNew();
                    var copy = new long[m.Amounts];
                    Buffer.BlockCopy(scratch, 0, copy, 0, m.Amounts * sizeof(long));
                    GC.KeepAlive(copy);
                    histWatch.Stop();
                    m.HistMs = histWatch.Elapsed.TotalMilliseconds;
                }

                measured.Add(m);
            }

            Report(fraction, windowSeconds, touchedNames, touchedRecords, tailRecords, unmatchedNames, unmatchedRecords,
                   totalRecords, walkMs, measured);
            }
        }

        Assert.IsTrue(recordsByName.Count > 0 && published.Count > 0, "no records and no published board came out of the capture");
    }

    /* What one window bought: the two arms, where the fold's time goes, who is covered, and whether the mirror can be trusted.
     * R and N are summed over the MEASURED rows only; the coverage line says what share of touched names could not be measured at all,
     * because a name with no row of its own has nothing to carry. */
    private static void Report(double anchor, double windowSeconds, long touchedNames, long touchedRecords, long tailRecords,
                               long unmatchedNames, long unmatchedRecords, long totalRecords, double walkMs, List<Row> rows)
    {
        Console.WriteLine();
        Console.WriteLine($"[foldpub] @{anchor:P0} W={windowSeconds:0.#}s: {touchedNames} names touched (R={touchedRecords:N0}, N={tailRecords:N0}); "
                          + $"{rows.Count} of them measured, {unmatchedNames} own no row of their own "
                          + $"({unmatchedRecords:N0} records, {(totalRecords > 0 ? unmatchedRecords * 100.0 / totalRecords : 0):F1}%)");
        if (rows.Count == 0)
        {
            Console.WriteLine("[foldpub]   nothing measurable (no touched name owns a published row)");
            return;
        }

        double cold = 0, fold = 0, mergeCell = 0, countTail = 0, hist = 0;
        long coldBytes = 0, foldBytes = 0, amounts = 0, checkedRows = 0, matched = 0, measuredR = 0, measuredN = 0, aggregates = 0;
        foreach (var r in rows)
        {
            cold += r.ColdMs; fold += r.FoldMs; mergeCell += r.MergeCellMs; countTail += r.CountTailMs; hist += r.HistMs;
            coldBytes += r.ColdBytes; foldBytes += r.FoldBytes; amounts += r.Amounts;
            measuredR += r.Records; measuredN += r.Tail;
            if (r.Aggregate) aggregates++;
            if (!r.FidelityChecked) continue;
            checkedRows++;
            if (r.FidelityMatched) matched++;
        }

        // The mirror counts with the walk's own calls but without its per-record bookkeeping (key composition, identity memoization,
        // block iteration), so it runs an order of magnitude cheaper than the builder. The prize is therefore quoted twice: against the
        // arms (a floor) and against the builder's measured walk rate (what layer 2 would actually stop paying).
        var mirrorNsPerRecord = measuredR > 0 ? cold * 1e6 / measuredR : 0;
        var builderNsPerRecord = totalRecords > 0 ? walkMs * 1e6 / totalRecords : 0;

        Console.WriteLine($"[foldpub]   COLD  recount        {cold,9:N0} ms | {Mb(coldBytes),8} | {measuredR:N0} records "
                          + $"({mirrorNsPerRecord:F0} ns/record in the mirror)");
        Console.WriteLine($"[foldpub]   FOLD  publish + tail {fold,9:N2} ms | {Mb(foldBytes),8}"
                          + $"  (publishing the carried cell {mergeCell:N2} ms = {mergeCell * 100.0 / Math.Max(1e-9, fold):F0}% of it,"
                          + $" counting N={measuredN:N0} in {countTail:N2} ms; {aggregates} rows reached through a `+Pets` row)");
        Console.WriteLine($"[foldpub]   SAVING vs the arms   {(cold - fold) * 100.0 / Math.Max(1e-9, cold),6:F1}% of the recount"
                          + "   [bar: whole-row reuse saves ~20% raid / ~35% group; a live refresh's available saving is ~99%]");
        Console.WriteLine($"[foldpub]   + histogram carry    {hist,9:N2} ms for {amounts:N0} amounts"
                          + $"  -> fold would cost {fold + hist:N2} ms, saving {(cold - fold - hist) * 100.0 / Math.Max(1e-9, cold):F1}%");
        if (builderNsPerRecord > 0)
        {
            var realWalkMs = measuredR * builderNsPerRecord / 1e6;
            Console.WriteLine($"[foldpub]   same R at the builder's own walk rate ({builderNsPerRecord:F0} ns/record): {realWalkMs:N0} ms of a "
                              + $"{walkMs:N0} ms walk, against {(fold + hist) * builderNsPerRecord / Math.Max(1e-9, mirrorNsPerRecord):N0} ms"
                              + " of publish+tail priced at the same rate - the publish term does not scale with records");
        }

        var ordered = rows.OrderBy(r => r.Records).ToList();
        Console.WriteLine("[foldpub]   per row, thinnest -> fattest:");
        foreach (var r in PrintSample(ordered))
        {
            Console.WriteLine($"[foldpub]     {r.Name,-20} R={r.Records,9:N0} N={r.Tail,6:N0} cells={r.Nodes,5} segs={r.Segments,5} amt={r.Amounts,8:N0}"
                              + $"  cold {r.ColdMs,8:F2} ms -> fold {r.FoldMs * 1000,8:F0} us (publish {r.MergeCellMs * 1000,6:F0} us)"
                              + $"  x{(r.FoldMs > 1e-9 ? r.ColdMs / r.FoldMs : 0):F0}");
        }

        Console.WriteLine($"[foldpub]   recount == published cell on {matched}/{checkedRows} plain rows (the mirror's own gate)");
        if (checkedRows > 0)
        {
            Assert.IsTrue(matched * 4 >= checkedRows,
                $"the recount mirror disagrees with the real builder on {checkedRows - matched}/{checkedRows} plain rows; "
                + "this probe would be timing the wrong work");
        }
    }

    private static IEnumerable<Row> PrintSample(List<Row> ordered)
        => ordered.Count <= 10 ? ordered : ordered.Take(3).Concat([ordered[ordered.Count / 2]]).Concat(ordered.TakeLast(6));

    /* The walk's own per-record body for one row: the row, its sub-stat row, and the raw amount the chart may later want. */
    private static PlayerStats Count(IReadOnlyList<DamageRecord> records)
    {
        var stats = new PlayerStats { Name = "recount" };
        foreach (var record in records)
        {
            var subStats = stats.SubStatOf(record.SubType, record.Type);
            var critHits = subStats.CritHits;
            StatsUtil.UpdateDamageStats(stats, record, false, false);
            StatsUtil.UpdateDamageStats(subStats, record, false, false);
            if (record.Total > 0) subStats.RecordHitTotal(record.Total, isCrit: subStats.CritHits > critHits);
        }

        Drain(stats);   // the whole tree, not just the top row: the fold's guard is per level
        return stats;
    }

    /* The increment of a window, per name: a record's second is its block's, so the block decides. */
    private static Dictionary<string, List<DamageRecord>> TailsByName(SummaryInput input, double from, double to)
    {
        var tails = new Dictionary<string, List<DamageRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fight in input.Fights)
        {
            foreach (var block in fight.DamageBlocks)
            {
                if (block.BeginTime < from || block.BeginTime > to) continue;
                foreach (var action in block.Actions)
                {
                    if (action is not DamageRecord record || string.IsNullOrEmpty(record.Attacker)) continue;
                    if (!tails.TryGetValue(record.Attacker, out var list)) tails[record.Attacker] = list = [];
                    list.Add(record);
                }
            }
        }

        return tails;
    }

    /* Close every running second in a tree, the way a carry would have to before the fold will accept it. */
    private static void Drain(PlayerSubStats node)
    {
        StatsUtil.CloseBestSecond(node);
        foreach (var child in node.SubSubStats) Drain(child);
        if (node is not PlayerStats player) return;
        foreach (var child in player.SubStats) Drain(child);
        foreach (var child in player.SubStats2) Drain(child);
    }

    /* How big a carried cell is: cells, range segments and raw amounts - the three things a publish has to reproduce. */
    private static void Shape(PlayerSubStats node, Row into)
    {
        into.Nodes++;
        into.Segments += node.Ranges?.TimeSegments.Count ?? 0;
        into.Amounts += node.RawHitTotals;
        if (node.BestSecTemp != 0) into.ScratchNodes++;

        foreach (var child in node.SubSubStats) Shape(child, into);
        if (node is not PlayerStats player) return;
        foreach (var child in player.SubStats) Shape(child, into);
        foreach (var child in player.SubStats2) Shape(child, into);
    }

    /* What keeping every published row's cell would retain. ~512 B per cell is loose on purpose: what decides here is that the amounts
     * grow with RECORDS while cells and segments grow with KEYS. */
    private static string StoreFootprint(IReadOnlyDictionary<string, PlayerStats> cells)
    {
        long nodes = 0, segments = 0, amounts = 0;
        foreach (var cell in cells.Values)
        {
            var probe = new Row();
            Shape(cell, probe);
            nodes += probe.Nodes; segments += probe.Segments; amounts += probe.Amounts;
        }

        return $"{nodes:N0} cells / {segments:N0} time segments / {amounts:N0} raw amounts => ~{Mb(nodes * 512L + amounts * 8L)} carried";
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:F1} MB";

    /* Where in the capture to take the window, as a share of the fact index (not of clock time: a night's idle hours are most of it). */
    private static double[] Anchors()
    {
        var raw = Environment.GetEnvironmentVariable("EQLP_FOLD_ANCHORS");
        if (string.IsNullOrWhiteSpace(raw)) return [0.25d, 0.5d, 0.75d];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(double.Parse).ToArray();
    }

    private static double[] Windows()
    {
        var raw = Environment.GetEnvironmentVariable("EQLP_FOLD_WINDOW");
        if (string.IsNullOrWhiteSpace(raw)) return [5d, 30d];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(double.Parse).ToArray();
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
