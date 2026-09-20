namespace EQLogParser;

// Phase 0 smoke tests: the current per-line pipeline runs headlessly (no WPF) on Linux and
// produces the expected fight for a small fixture. The fixture (mini-data/mirror/mini-fight.txt)
// is hand-synthesized in EQ log format with agreed test names — real log files are never
// committed. Exact-value pinning against the derived pipeline is Phase 1's comparison report;
// here we assert structure and parity only.
// (assembly-level DoNotParallelize lives in src/control/fct/AssemblySettings.cs)
[TestClass]
public class MirrorPipelineSmokeTest
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "mini-data", "mirror", "mini-fight.txt");

    [TestMethod]
    public void RunFile_MiniFight_SlainFightIsClosedWithPlayerTotals()
    {
        Assert.IsTrue(File.Exists(FixturePath), $"missing fixture: {FixturePath}");

        var result = PipelineHarness.RunFile(FixturePath);
        Assert.IsTrue(result.Fights.Count > 0, "no fights produced — check harness bootstrap output");

        var priest = result.Fights.FirstOrDefault(f => f.Name?.Equals("an ice giant priest", StringComparison.OrdinalIgnoreCase) is true);
        Assert.IsNotNull(priest, $"no slain fight among: {string.Join(", ", result.Fights.Select(f => $"'{f.Name}' (dead={f.Dead}, total={f.DamageTotal})"))}");

        // "An ice giant priest was slain by Ammeren!" closes the fight.
        Assert.IsTrue(priest.Dead, "slain fight should be marked dead");
        Assert.IsTrue(priest.DamageTotal > 0, "expected damage on the slain fight");
        Assert.IsTrue(priest.PlayerDamageTotals.ContainsKey("Ammeren"), $"expected Ammeren in {string.Join(", ", priest.PlayerDamageTotals.Keys)}");

        // The window also has first-person and pet damage; the fight must have a begin time.
        Assert.IsFalse(double.IsNaN(priest.BeginDamageTime), "begin damage time should be set");
    }

    [TestMethod]
    public void RunFile_MiniFight_PipelineEventsMatchDirectParse()
    {
        // Phase 0 invariant: the task-based LogProcessor path must raise exactly the damage
        // events a direct synchronous parse of every line raises — no drops, no duplicates.
        var lines = File.ReadAllLines(FixturePath)
            .Where(l => l.Length > 28 && DateUtil.ParseStandardDate(l) != DateTime.MinValue)
            .ToList();

        int directCount = 0;
        DamageLineParser.ResetProcessState();
        void OnDirect(DamageProcessedEvent _) => directCount++;
        DamageLineParser.EventsDamageProcessed += OnDirect;
        try
        {
            foreach (var line in lines)
            {
                var ld = new LineData { Action = line[27..], BeginTime = DateUtil.ToDotNetSeconds(DateUtil.ParseStandardDate(line)) };
                ld.Split = ld.Action.Split(' ');
                _ = DamageLineParser.Process(ld) || HealingLineParser.Process(ld);
            }
        }
        finally
        {
            DamageLineParser.EventsDamageProcessed -= OnDirect;
        }

        var pipelineCount = 0;
        void OnPipeline(DamageProcessedEvent _) => pipelineCount++;
        DamageLineParser.EventsDamageProcessed += OnPipeline;
        try
        {
            PipelineHarness.RunFile(FixturePath);
        }
        finally
        {
            DamageLineParser.EventsDamageProcessed -= OnPipeline;
        }

        Assert.AreEqual(directCount, pipelineCount,
            $"pipeline raised {pipelineCount} damage events, direct parse raised {directCount} — the task path must not lose or add records");
        Assert.IsTrue(pipelineCount > 0, "expected at least one damage event on this fixture");
    }

    [TestMethod]
    public void RunFile_MiniFight_WritesJsonSnapshot()
    {
        var result = PipelineHarness.RunFile(FixturePath);
        var json = PipelineHarness.ToJson(result);
        var outPath = Path.Combine(AppContext.BaseDirectory, "mirror-snapshot-mini-fight.json");
        File.WriteAllText(outPath, json);

        Console.WriteLine(json);
        Assert.IsTrue(json.Contains("an ice giant priest", StringComparison.OrdinalIgnoreCase));
    }
}
