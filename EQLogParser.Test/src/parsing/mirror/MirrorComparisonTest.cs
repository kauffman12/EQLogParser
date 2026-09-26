using System.Diagnostics;

using EQLogParser.Mirror;

namespace EQLogParser;

// Phase 1 exit checks (design doc §7): on the committed fixtures the derived pipeline must match
// the current one — fight count and boundaries 100 %, totals exact — and the comparison report
// must be legible. The large real-log runs (344 MB / 3.6M lines) happen locally via the
// EQLP_MIRROR_LOG env var; no log files are committed.
[TestClass]
public class MirrorComparisonTest
{
    private static string MiniFightPath => Path.Combine(AppContext.BaseDirectory, "mini-data", "mirror", "mini-fight.txt");
    private static string GapFightPath => Path.Combine(AppContext.BaseDirectory, "mini-data", "mirror", "gap-fight.txt");
    private static string ReportDir => Path.Combine(AppContext.BaseDirectory, "mirror-reports");

    [TestMethod]
    public void MiniFight_DerivedMatchesCurrentPipeline()
    {
        Assert.IsTrue(File.Exists(MiniFightPath), $"missing fixture: {MiniFightPath}");

        var run = PipelineHarness.RunFileWithMirror(MiniFightPath);
        Assert.IsTrue(run.Facts.FactCount > 0, "no damage facts captured — mirror tap not wired?");
        Assert.AreEqual(1, run.Facts.DeathCount, "expected exactly one slain fact on this fixture");

        var report = MirrorComparison.Compare(run.Fights, run.DerivedFights, logName: "mini-fight.txt", factCount: run.Facts.FactCount);
        report.WriteAll(ReportDir, "mini-fight");
        Console.WriteLine(report.ToText());

        Assert.IsTrue(report.CountsMatch, $"fight count mismatch current={report.CurrentFightCount} derived={report.DerivedFightCount}\n{report.ToText()}");
        Assert.IsTrue(report.AllMatch, $"derived fights diverge from the current pipeline:\n{report.ToText()}");
    }

    [TestMethod]
    public void GapFight_SlainSplitsAndSectionizeAddsDivider()
    {
        Assert.IsTrue(File.Exists(GapFightPath), $"missing fixture: {GapFightPath}");

        var run = PipelineHarness.RunFileWithMirror(GapFightPath);
        var report = MirrorComparison.Compare(run.Fights, run.DerivedFights, logName: "gap-fight.txt", factCount: run.Facts.FactCount);
        report.WriteAll(ReportDir, "gap-fight");

        // two instances of the same boss across a 5-minute gap: the current pipeline closes the
        // first on slain and opens a second; the derived one must do exactly the same
        Assert.AreEqual(2, run.DerivedFights.Count, $"expected 2 derived fights:\n{report.ToText()}");
        Assert.IsTrue(run.DerivedFights.All(f => f.Dead), "both instances are slain — both must be dead");
        Assert.IsTrue(report.CountsMatch && report.AllMatch, $"gap fixture diverged:\n{report.ToText()}");

        // sectioning: the 295 s gap (12:00:05 -> 12:05:00) exceeds the 120 s group timeout
        var sections = Sectionizer.Sectionize(run.DerivedFights);
        Assert.AreEqual(2, sections.Count, "expected two sections");
        Assert.AreEqual(0, sections[0].InactiveGaps.Count, "first section has no preceding gap");
        // one divider for the 5-minute gap (12:00:05 last hit -> 12:05:00 first hit)
        CollectionAssert.AreEqual(new double[] { 295 }, sections[1].InactiveGaps.Select(g => g.To - g.From).ToArray());
    }

    [TestMethod]
    public void Perf_FixtureScale_DeriveAndMirrorOverhead()
    {
        var path = MiniFightPath;
        Assert.IsTrue(File.Exists(path), $"missing fixture: {path}");

        // A/B: run without the mirror, then with it + derive. The §6 targets (< 300 ms full derive,
        // < 2 % overhead) are judged on the large log — at fixture scale these are sanity bounds.
        var sw = Stopwatch.StartNew();
        _ = PipelineHarness.RunFile(path);
        var baselineMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var run = PipelineHarness.RunFileWithMirror(path);
        var withMirrorMs = sw.ElapsedMilliseconds;

        var facts = run.Facts;
        sw.Restart();
        _ = FightDeriver.Derive(facts);
        var deriveMs = sw.ElapsedMilliseconds;

        Console.WriteLine($"[perf] {Path.GetFileName(path)}: baseline={baselineMs}ms withMirror={withMirrorMs}ms derive={deriveMs}ms facts={facts.FactCount}");

        Assert.IsTrue(deriveMs < 300, $"full derive took {deriveMs} ms — §6 target is < 300 ms even at fixture scale");
        Assert.IsTrue(withMirrorMs < baselineMs + Math.Max(50, baselineMs / 2),
            $"mirror overhead too large: baseline={baselineMs}ms withMirror={withMirrorMs}ms");
    }

    [TestMethod]
    public void RealLog_Comparison_WhenEnvSet()
    {
        var path = Environment.GetEnvironmentVariable("EQLP_MIRROR_LOG");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Console.WriteLine("[mirror] EQLP_MIRROR_LOG not set (or missing) — skipping the real-log comparison run.");
            return;
        }

        var sw = Stopwatch.StartNew();
        var run = PipelineHarness.RunFileWithMirror(path);
        var wallMs = sw.ElapsedMilliseconds;

        var report = MirrorComparison.Compare(run.Fights, run.DerivedFights, logName: Path.GetFileName(path), factCount: run.Facts.FactCount);
        var baseName = $"real-{Path.GetFileNameWithoutExtension(path)}";
        report.WriteAll(ReportDir, baseName);

        var factBytes = run.Facts.EstimatedBytes;
        Console.WriteLine($"[mirror] {Path.GetFileName(path)}: {wallMs}ms wall, {run.Facts.FactCount} facts ({factBytes / 1024 / 1024} MB buffer), " +
            $"fights current={report.CurrentFightCount} derived={report.DerivedFightCount}, matched={report.MatchedFights}/{Math.Max(report.CurrentFightCount, report.DerivedFightCount)}");
        Console.WriteLine(report.ToText());

        Assert.Inconclusive("manual run complete — inspect the report in " + ReportDir);
    }
}
