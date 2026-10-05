using System.Diagnostics;
using EQLogParser;

namespace EQLogParser;

/*
 * A GATED COST PROBE, not a CI assertion: how much of what a live-tail pass produces is actually new to the screen?
 *
 * Why this exists before any "update the UI only when changed" code. Every completed pass raises `Derived`, and the fight
 * list answers by throwing away all its rows and rebuilding them (`FightTable.OnDerived`), while the Names pane re-runs a
 * classification from zero. If most passes change nothing visible, an equality gate buys real time; if almost every pass
 * changes something, a gate is overhead and the right answer is an INCREMENTAL row update (touch only the rows that moved).
 * Which of those it is cannot be argued from the code: during active raiding nearly every fact moves a total, while
 * heal-only stretches and mob-on-mob traffic change no displayed number at all.
 *
 * The tail is simulated honestly — the capture is replayed as growing PREFIXES of the real file, so parsing and the rule book
 * see only what has "arrived", exactly as a live session does — and each prefix is projected through the same entry point the
 * engine uses. Comparison covers the fields a surface READS, for the rows the grid shows (RaidPet rows are hidden by
 * CharmPetRows.Visible): both damage totals, hit counts, end time (the duration column), dead + end-reason + charm, group id,
 * and the two direction windows the live-meter rule reads.
 *
 * Two extra columns decide whether a gate is safe rather than merely cheap:
 *   CONTROL - the same prefix projected TWICE. Identical input must produce identical content, or an equality gate would fire
 *             on its own noise. (Pet claims are parse side effects — see PipelineHarness's measured residue — so this column is
 *             expected to be NON-zero, and that itself is a finding worth printing.)
 *   DIGEST  - the cheap stamp a gate would hold instead of a deep compare (row count + integer sums, no strings), checked
 *             against the deep comparison. A digest with FALSE NEGATIVES cannot gate anything; one with only false positives
 *             can, because its worst case is a repaint that was needed anyway. Costs of both are timed so the trade is visible.
 *
 * Run it:
 *   EQLP_LIVE_TAIL_PROBE=local/logs/live/eqlog_Incogitable_xegony.txt EQLP_LIVE_TAIL_PASSES=12 \
 *     dotnet test EQLogParser.Test/EQLogParser.Test.csproj --no-build --filter LiveTailChangeProbe --logger "console;verbosity=detailed"
 *
 * Once the answer is known this file goes away and the numbers move to docs/DesignNotes.md (AGENTS: "A gated real-log test is
 * disposable"). It asserts only that it ran, because what it produces is a decision input, not a behaviour contract.
 */
[TestClass]
public class LiveTailChangeProbeTest
{
    // Field names in the exact order ContentOf writes them, so a change can be attributed to a column.
    private static readonly string[] Fields =
    [
        "LastTime", "DamageTotal", "DamageToOwner", "DamageByOwner", "TankTotal", "TankHits", "DamageHits",
        "Dead", "EndReason", "CharmedOwned", "GroupId", "LastDamageTime", "LastTankingTime",
    ];

    [TestMethod]
    public void Probe_WhatEachLiveTailPassActuallyChanges()
    {
        var path = Environment.GetEnvironmentVariable("EQLP_LIVE_TAIL_PROBE");
        if (string.IsNullOrEmpty(path))
        {
            Assert.Inconclusive("set EQLP_LIVE_TAIL_PROBE=<log> to replay the capture as growing prefixes");
        }
        if (!File.Exists(path)) Assert.Fail($"EQLP_LIVE_TAIL_PROBE points at nothing: {path}");

        var passes = 12;
        if (int.TryParse(Environment.GetEnvironmentVariable("EQLP_LIVE_TAIL_PASSES"), out var p) && p >= 2) passes = p;

        var bounds = PrefixBounds(path, passes);
        var dir = Path.Combine(Path.GetTempPath(), "eq-tail-probe");
        Directory.CreateDirectory(dir);
        var stem = Path.GetFileNameWithoutExtension(path);

        Dictionary<string, string>? prevContent = null;
        var prevDigest = 0L;
        var noop = 0;
        var changedCounts = new List<int>();
        var fieldMovers = new Dictionary<string, int>();
        long contentMs, digestMs, compareMs, passMs;
        contentMs = digestMs = compareMs = passMs = 0;
        var digestMissed = 0;
        var digestExtra = 0;

        Console.WriteLine($"[tail] {Path.GetFileName(path)} replayed as {passes} growing prefixes " +
                          $"({new FileInfo(path).Length / 1_048_576:N0} MB)");

        for (var k = 1; k <= passes; k++)
        {
            var prefix = Path.Combine(dir, $"{stem}-tail{k:D2}.txt");
            CopyPrefix(path, bounds[k], prefix);

            var sw = Stopwatch.StartNew();
            var run = PipelineHarness.RunFileDerived(prefix);
            var (rows, _, _) = DeriveOver(run.Facts, run.Timeline);
            sw.Stop();
            passMs += sw.ElapsedMilliseconds;
            File.Delete(prefix);

            var visible = rows.Count(r => !r.RaidPet);

            var c1 = Stopwatch.StartNew();
            var content = ContentOf(rows);
            c1.Stop();
            contentMs += c1.ElapsedMilliseconds;

            var c2 = Stopwatch.StartNew();
            var digest = DigestOf(rows);
            c2.Stop();
            digestMs += c2.ElapsedMilliseconds;

            // Control: identical input, run again. Any difference is drift in the pipeline, not news on screen.
            var controlNote = "";
            if (k == 1 || k == passes)
            {
                CopyPrefix(path, bounds[k], prefix);
                var controlRun = PipelineHarness.RunFileDerived(prefix);
                var (controlRows, _, _) = DeriveOver(controlRun.Facts, controlRun.Timeline);
                File.Delete(prefix);
                controlNote = $" | CONTROL same input twice: {DiffCount(content, ContentOf(controlRows))} rows differ";
            }

            if (prevContent is null)
            {
                Console.WriteLine($"[tail] pass {k,2}: facts {run.Facts.FactCount,9:N0} visible rows {visible,5:N0}" +
                  $" | first pass | parse+project {sw.ElapsedMilliseconds,5:N0} ms" +
                  $" | content {c1.ElapsedMilliseconds,3} ms, digest {c2.ElapsedMilliseconds,3} ms{controlNote}");
                prevContent = content;
                prevDigest = digest;
                continue;
            }

            var cw = Stopwatch.StartNew();
            var diff = DiffOf(content, prevContent, fieldMovers);
            cw.Stop();
            compareMs += cw.ElapsedMilliseconds;

            var touched = diff.Changed + diff.Added + diff.Removed;
            var contentChanged = touched > 0;
            var digestChanged = digest != prevDigest;
            if (contentChanged && !digestChanged) digestMissed++;
            if (!contentChanged && digestChanged) digestExtra++;
            if (contentChanged) changedCounts.Add(touched); else noop++;

            Console.WriteLine($"[tail] pass {k,2}: facts {run.Facts.FactCount,9:N0} visible rows {visible,5:N0}" +
              $" | vs previous: changed {diff.Changed,5} added {diff.Added,4} removed {diff.Removed,3}" +
              $" => {(contentChanged ? "CHANGED" : "NO-OP")} (digest said {(digestChanged ? "changed" : "same")})" +
              $" | parse+project {sw.ElapsedMilliseconds,5:N0} ms | content {c1.ElapsedMilliseconds,3} ms," +
              $" digest {c2.ElapsedMilliseconds,3} ms, compare {cw.ElapsedMilliseconds,3} ms{controlNote}");

            prevContent = content;
            prevDigest = digest;
        }

        var compared = passes - 1;
        Console.WriteLine($"[tail] SUMMARY over {compared} comparisons: NO-OP {noop} ({100.0 * noop / compared:0.#} %)," +
                          $" CHANGED {changedCounts.Count}");
        if (changedCounts.Count > 0)
        {
            var sorted = changedCounts.OrderBy(x => x).ToList();
            Console.WriteLine($"[tail]   rows touched per changed pass: min {sorted[0]:N0}," +
                              $" median {sorted[sorted.Count / 2]:N0}, max {sorted[^1]:N0} (of ~{prevContent?.Count:N0} visible rows)");
            foreach (var (label, test) in new (string, Func<int, bool>)[]
            {
                ("1-5", n => n <= 5), ("6-25", n => n is > 5 and <= 25), ("26-100", n => n is > 25 and <= 100), (">100", n => n > 100),
            })
            {
                Console.WriteLine($"[tail]     touched {label,6}: {changedCounts.Count(test)} passes");
            }

            Console.WriteLine("[tail]   which columns moved (row-instances where the field differed):");
            foreach (var pair in fieldMovers.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"[tail]     {pair.Value,7:N0} x {pair.Key}");
        }

        Console.WriteLine($"[tail] COST of deciding: mean pass {passMs / passes:0.#} ms; per decision:" +
                          $" cheap digest {digestMs / (double)passes:0.##} ms, deep content build {contentMs / (double)passes:0.##} ms," +
                          $" compare {compareMs / (double)Math.Max(1, compared):0.##} ms");
        Console.WriteLine($"[tail] DIGEST verdict: {digestMissed} false negatives" +
          $" ({(digestMissed == 0 ? "a cheap stamp could gate" : "a cheap stamp CANNOT gate — deep compare required")})," +
          $" {digestExtra} false positives (harmless: one extra repaint)");

        Assert.IsTrue(compared > 0, "the probe needs at least two prefixes to compare");
    }

    // The engine's own projection entry point. No board is built here — only the rows and the fields a surface reads matter.
    private static (List<DerivedFight> Rows, FightFactIndex Index, HashSet<int> Claimed) DeriveOver(
      DamageFactTable facts, EntityTimeline timeline)
    {
        var index = new FightFactIndex(timeline);
        var claimed = new HashSet<int>();
        var rows = FightProjection.Build(facts, timeline, (fact, ordinal, owner, target) =>
        {
            claimed.Add(ordinal);
            index.OnFact(fact, ordinal, owner, target);
        });
        return (rows, index, claimed);
    }

    /*
     * What the grid would show, keyed by name + begin time — the pair the fight list already uses to put a selection back
     * (FightKey: the row number cannot be it). RaidPet rows are left out because the grid does not show them: their movement
     * must not make a pass look changed when nothing on screen moved.
     */
    private static Dictionary<string, string> ContentOf(List<DerivedFight> rows)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (r.RaidPet) continue;
            map[$"{r.Name}\u0001{r.BeginTime:0.###}"] = string.Join('\u0002',
              $"{r.LastTime:0.###}", r.DamageTotal, r.DamageToOwner, r.DamageByOwner, r.TankTotal, r.TankHits, r.DamageHits,
              r.Dead ? 1 : 0, (int)r.EndReason, r.CharmedOwned ? 1 : 0, r.GroupId,
              $"{r.LastDamageTime:0.###}", $"{r.LastTankingTime:0.###}");
        }
        return map;
    }

    // The stamp a gate would hold: integers only, no strings, one walk over the rows.
    private static long DigestOf(List<DerivedFight> rows)
    {
        long visible = 0, damage = 0, tank = 0, dead = 0;
        foreach (var r in rows)
        {
            if (r.RaidPet) continue;
            visible++;
            damage += r.DamageTotal + r.DamageToOwner;
            tank += r.TankTotal;
            if (r.Dead) dead++;
        }

        var hash = new HashCode();
        hash.Add(visible);
        hash.Add(damage);
        hash.Add(tank);
        hash.Add(dead);
        return hash.ToHashCode();
    }

    private static int DiffCount(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        var (changed, added, removed) = DiffOf(a, b, null);
        return changed + added + removed;
    }

    private static (int Changed, int Added, int Removed) DiffOf(
      Dictionary<string, string> now, Dictionary<string, string> before, Dictionary<string, int>? fieldMovers)
    {
        var changed = 0;
        var added = 0;

        foreach (var pair in now)
        {
            if (!before.TryGetValue(pair.Key, out var old))
            {
                added++;
                continue;
            }
            if (old == pair.Value) continue;

            changed++;
            if (fieldMovers is null) continue;

            var oldParts = old.Split('\u0002');
            var newParts = pair.Value.Split('\u0002');
            for (var i = 0; i < Fields.Length && i < oldParts.Length && i < newParts.Length; i++)
            {
                if (oldParts[i] == newParts[i]) continue;
                fieldMovers.TryGetValue(Fields[i], out var n);
                fieldMovers[Fields[i]] = n + 1;
            }
        }

        var removed = before.Keys.Count(key => !now.ContainsKey(key));
        return (changed, added, removed);
    }

    /*
     * Byte offset just past the line ending each prefix: two sequential scans (count lines, then walk to every
     * lines/passes boundary), because boundaries cannot be placed until the count is known. A byte copy per pass beats a
     * 2.5-million-line ReadLine loop by an order of magnitude — the probe must not be dominated by its own file work.
     */
    private static long[] PrefixBounds(string path, int passes)
    {
        var bounds = new long[passes + 1];
        long lines = 0;
        var buffer = new byte[1 << 20];

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        {
            while (true)
            {
                var read = fs.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                for (var i = 0; i < read; i++) if (buffer[i] == (byte)'\n') lines++;
            }
        }

        var per = Math.Max(1L, lines / passes);
        var found = 0;
        long pos = 0;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        {
            while (found < passes)
            {
                var read = fs.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                for (var i = 0; i < read && found < passes; i++)
                {
                    pos++;
                    if (buffer[i] != (byte)'\n' || pos % per != 0) continue;
                    bounds[++found] = pos;
                }
                if (read < buffer.Length) break;
            }
        }

        // A short file (or a boundary that fell inside the last block) still gets monotonic prefixes, ending at the whole file.
        var end = new FileInfo(path).Length;
        bounds[passes] = end;
        for (var k = passes - 1; k >= 1; k--)
            bounds[k] = bounds[k] > 0 && bounds[k] < bounds[k + 1] ? bounds[k] : Math.Max(1, bounds[k + 1] * k / passes);

        return bounds;
    }

    private static void CopyPrefix(string source, long bytes, string target)
    {
        using var inF = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var outF = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var buffer = new byte[1 << 20];
        long left = bytes;
        while (left > 0)
        {
            var want = (int)Math.Min(buffer.Length, left);
            var read = inF.Read(buffer, 0, want);
            if (read <= 0) break;
            outF.Write(buffer, 0, read);
            left -= read;
        }
    }
}
