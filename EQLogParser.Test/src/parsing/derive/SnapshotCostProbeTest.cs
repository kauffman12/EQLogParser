using System.Diagnostics;

using EQLogParser;

namespace EQLogParser;

/*
 * What a carried cell COSTS to take — the number step 2 of the fold-at-publish plan needs (2026-11).
 *
 * `StatsSnapshot` (docs/DesignNotes.md → "The copy a carried cell needs, and the second it has to be taken at") is the boundary that
 * unblocked layer 2: a published row cannot be cached because it still holds its running best-second scratch and the display model had no
 * copy path. Both are answered, so the remaining question is price, because a snapshot is not free the way a fold is not free: it copies
 * every cell, dictionary, range and raw hit amount under a row, and the copy is then RETAINED as the carried cell (so its bytes are the
 * retention cost of carrying names across passes, not just a per-pass tax).
 *
 * Inputs are real: the board comes from the real builder over a whole-capture selection, so every row measured here is one the grid bound.
 * Two arms per touched row:
 *   SNAPSHOT = `StatsSnapshot.Take(row)` — step 2's cost per touched name, and its retention;
 *   RECOUNT  = counting that row's whole record list through the walk's own calls — what step 2 stops paying.
 * The ratio is the decision: a snapshot must be far cheaper than the recount it replaces, or carrying cells is a wash.
 *
 *   EQLP_SNAPSHOT_COST=local/logs/live/eqlog_Kizant_xegony-09-03-26.txt dotnet test --filter SnapshotCost --logger "console;verbosity=detailed"
 *   EQLP_EMU=1 EQLP_SNAPSHOT_COST=local/logs/emu/eqlog_Roper_thj.txt ...      (group content: 42k rows, the thin-row shape)
 *
 * Rounds are ALTERNATED per row rather than run in two blocks: doing every snapshot first and every recount second lets the second arm
 * enjoy warm dictionaries and reads as a smaller ratio than exists (the artifact that let `BoardFanout.MinOutcomes` ship at 500,000).
 */
[TestClass]
[DoNotParallelize]
public class SnapshotCostProbeTest
{
    private sealed class Row
    {
        public string Name = "";
        public long Records;
        public int Cells;
        public int Segments;
        public int Amounts;
        public double SnapshotMs;
        public long SnapshotBytes;
        public double RecountMs;
        public bool FidelityMatched;
    }

    [TestMethod]
    public void SnapshotCost_WhatACarriedCellCostsToTake()
    {
        var path = Resolve(Environment.GetEnvironmentVariable("EQLP_SNAPSHOT_COST"));
        if (path is null) Assert.Inconclusive("set EQLP_SNAPSHOT_COST=<log>");

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
        var input = FightSummarySource.Build(rows, index, facts, first, last);

        var recordsByName = new Dictionary<string, List<DamageRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fight in input.Fights)
        {
            foreach (var block in fight.DamageBlocks)
            {
                foreach (var action in block.Actions)
                {
                    if (action is not DamageRecord record || string.IsNullOrEmpty(record.Attacker)) continue;
                    if (!recordsByName.TryGetValue(record.Attacker, out var list)) recordsByName[record.Attacker] = list = [];
                    list.Add(record);
                }
            }
        }

        DerivedTotals.ForOverlay(rows, index, facts, run.HealFacts, first, last);      // warm-up build
        var overlay = DerivedTotals.ForOverlay(rows, index, facts, run.HealFacts, first, last);
        var published = overlay?.DamageStats?.StatsList ?? [];
        Assert.IsTrue(published.Count > 0, "the board produced no rows, so this probe would measure nothing");

        var measured = new List<Row>();
        foreach (var stats in published)
        {
            if (stats.IsGroupHeader || string.IsNullOrEmpty(stats.Name)) continue;
            if (!recordsByName.TryGetValue(stats.Name, out var owned) || owned.Count == 0) continue;

            var m = new Row { Name = stats.Name, Records = owned.Count, Cells = CountCells(stats), Segments = CountSegments(stats), Amounts = CountAmounts(stats) };

            // Alternate the arms (and take the better of two laps per arm) so neither one runs on a warmer cache than the other.
            for (var lap = 0; lap < 2; lap++)
            {
                var snapWatch = Stopwatch.StartNew();
                var snapAlloc = GC.GetAllocatedBytesForCurrentThread();
                var copy = StatsSnapshot.Take(stats);
                snapWatch.Stop();
                var snapMs = snapWatch.Elapsed.TotalMilliseconds;
                var snapBytes = GC.GetAllocatedBytesForCurrentThread() - snapAlloc;

                var recountWatch = Stopwatch.StartNew();
                _ = Count(owned);   // the work is the measurement; the row it builds is compared by the other probe
                recountWatch.Stop();
                var lapRecountMs = recountWatch.Elapsed.TotalMilliseconds;

                if (lap == 0 || snapMs < m.SnapshotMs) { m.SnapshotMs = snapMs; m.SnapshotBytes = snapBytes; }
                if (lap == 0 || lapRecountMs < m.RecountMs) m.RecountMs = lapRecountMs;

                // A snapshot that changed the answer is not a copy, whatever it costs. The drain is allowed to move BestSec and nothing else.
                m.FidelityMatched = copy.Total == stats.Total && copy.Hits == stats.Hits && copy.BestSecTemp == 0 && copy.SubStats.Count == stats.SubStats.Count;
            }

            measured.Add(m);
        }

        var mismatches = measured.Count(static m => !m.FidelityMatched);
        Assert.AreEqual(0, mismatches, $"{mismatches} of {measured.Count} snapshots failed to reproduce the row they copied");

        var rows2 = measured.Count;
        var snapshotMs = measured.Sum(static m => m.SnapshotMs);
        var recountMs = measured.Sum(static m => m.RecountMs);
        var bytes = measured.Sum(static m => m.SnapshotBytes);
        var amounts = measured.Sum(static m => m.Amounts);
        var cells = measured.Sum(static m => m.Cells);

        var worst = measured.OrderByDescending(static m => m.SnapshotMs).First();
        var fattest = measured.OrderByDescending(static m => m.SnapshotBytes).First();

        Console.WriteLine($"[snap] {Path.GetFileName(path)}: {rows2} published rows measured, {measured.Sum(static m => m.Records):N0} records behind them");
        Console.WriteLine($"[snap]   snapshot: {snapshotMs:F1} ms total, mean {snapshotMs / Math.Max(1, rows2) * 1000:F1} us/row"
            + $" | {bytes / 1048576.0:F1} MB copied ({bytes / Math.Max(1, rows2):N0} B/row) — this is ALSO the retained cost of carrying these names");
        Console.WriteLine($"[snap]   recount : {recountMs:F0} ms total for the same rows — what step 2 stops paying");
        Console.WriteLine($"[snap]   ratio   : a snapshot costs {(recountMs > 0 ? snapshotMs / recountMs * 100 : 0):F3} % of the recount it replaces");
        Console.WriteLine($"[snap]   carried : {cells:N0} cells, {measured.Sum(static m => m.Segments):N0} range segments,"
            + $" {amounts:N0} raw hit amounts ({amounts * 8 / 1048576.0:F1} MB of them — the amounts dominate)");
        Console.WriteLine($"[snap]   slowest : {worst.Name} {worst.SnapshotMs * 1000:F0} us ({worst.Cells} cells, {worst.Records:N0} records);"
            + $" fattest: {fattest.Name} {fattest.SnapshotBytes / 1024.0:F0} KB");
        var builderLine = StatsBuildTrace.LastFinishedLineOf("damage") ?? "(no trace line)";
        Console.WriteLine($"[snap]   builder : {builderLine}");

        Assert.IsTrue(rows2 > 3, "the sample is too thin to say anything about cost");
    }

    // The walk's own calls, as the other probe's mirror has them: row + sub-stat row + the raw amount.
    private static PlayerStats Count(List<DamageRecord> records)
    {
        var row = new PlayerStats { Name = "probe" };
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            StatsUtil.UpdateDamageStats(row, record, newFrame: i == 0);
            var sub = row.SubStatOf(record.SubType, record.Type);
            StatsUtil.UpdateDamageStats(sub, record, newFrame: i == 0);
            if (record.Total > 0) sub.RecordHitTotal(record.Total, isCrit: false);
        }

        return row;
    }

    private static int CountCells(PlayerSubStats node)
    {
        var n = 1;
        foreach (var child in node.SubSubStats) n += CountCells(child);
        if (node is not PlayerStats row) return n;
        foreach (var child in row.SubStats) n += CountCells(child);
        foreach (var child in row.SubStats2) n += CountCells(child);
        if (row is GroupEntry group)
        {
            foreach (var member in group.Members) n += CountCells(member);
            foreach (var child in group.Children) n += CountCells(child);
        }

        return n;
    }

    private static int CountSegments(PlayerStats row) => row.Ranges.TimeSegments.Count + row.AllRanges.TimeSegments.Count;

    private static int CountAmounts(PlayerSubStats node)
    {
        var n = node.RawHitTotals;
        foreach (var child in node.SubSubStats) n += CountAmounts(child);
        if (node is not PlayerStats row) return n;
        foreach (var child in row.SubStats) n += CountAmounts(child);
        foreach (var child in row.SubStats2) n += CountAmounts(child);
        return n;
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
