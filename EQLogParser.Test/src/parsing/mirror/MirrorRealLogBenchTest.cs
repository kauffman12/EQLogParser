using System.Diagnostics;

using EQLogParser.Mirror;

namespace EQLogParser;

// Opt-in performance bench for the mirror derive pipeline over a REAL log (field report: the WPF
// app captured 2.1M facts and the visible snapshot never arrived - the derive-phase cost at
// millions of facts had never been measured). Runs only when EQLP_MIRROR_BENCH names a log file:
//   EQLP_MIRROR_BENCH=local/eqlog_Kizant_xegony-selected2.txt dotnet test --filter Bench --logger "console;verbosity=detailed"
// Phases are timed separately (ingest, seed, classify, derive) because the app shows them as one
// opaque wait; the printout says which phase owns the wait.
[TestClass]
[DoNotParallelize]
public class MirrorRealLogBenchTest
{
    [TestMethod]
    public void Bench_RealLog_MirrorPhases()
    {
        var path = Environment.GetEnvironmentVariable("EQLP_MIRROR_BENCH");
        if (string.IsNullOrEmpty(path))
        {
            Assert.Inconclusive("set EQLP_MIRROR_BENCH=<path to log> to run");
        }

        PipelineHarness.EnsureDataStore();

        var selfMatch = System.Text.RegularExpressions.Regex.Match(
            Path.GetFileNameWithoutExtension(path), @"^eqlog_(.+?)_(?:.+?)(?:-[^-]*)?\.txt$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (selfMatch.Success)
        {
            ConfigUtil.PlayerName = selfMatch.Groups[1].Value;
        }

        DamageLineParser.ResetProcessState();
        PlayerRegistry.Instance.Clear();

        var priorInstance = FightManager.Instance;
        var priorParserFm = DamageLineParser.FightManager;
        var fm = new FightManager();
        FightManager.Instance = fm;
        DamageLineParser.FightManager = fm;

        var facts = new DamageFactTable(100_000);
        var mirror = new CombatMirror(facts);
        mirror.Start();

        var totalSw = Stopwatch.StartNew();
        try
        {
            long lines = 0;
            double firstTs = double.NaN, lastTs = double.NaN;

            var ingestSw = Stopwatch.StartNew();
            using (var items = new System.Collections.Concurrent.BlockingCollection<LogReaderItem>(
                       new System.Collections.Concurrent.ConcurrentQueue<LogReaderItem>(), 100_000))
            using (var processor = new LogProcessor(path, new MirrorChatSink(mirror), new NoOpHook()))
            {
                processor.LinkTo(items);

                var batch = new List<LogReaderItem>(5000);
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length <= 28) continue;
                    var dt = DateUtil.ParseStandardDate(line);
                    if (dt == DateTime.MinValue) continue;
                    var ts = DateUtil.ToDotNetSeconds(dt);
                    if (double.IsNaN(firstTs)) firstTs = ts;
                    lastTs = ts;
                    lines++;
                    batch.Add(new LogReaderItem(line, ts, false));
                    if (batch.Count >= 5000)
                    {
                        foreach (var item in batch) items.Add(item);
                        batch.Clear();
                    }
                }
                foreach (var item in batch) items.Add(item);
                items.CompleteAdding();

                var drainCap = TimeSpan.FromSeconds(Math.Max(60, new FileInfo(path).Length / (1024.0 * 1024) * 2));
                var completion = processor.Completion;
                if (completion is not null)
                {
                    try
                    {
                        if (!completion.Wait(drainCap))
                        {
                            Assert.Fail($"pipeline did not drain within {drainCap.TotalSeconds:F0}s");
                        }
                    }
                    catch (AggregateException ae) when (ae.InnerException is not TimeoutException)
                    {
                        throw ae.InnerException ?? ae;
                    }
                }
            }
            ingestSw.Stop();

            if (!double.IsNaN(lastTs)) DamageLineParser.CheckSlainQueue(lastTs + 1);

            var captured = (long)facts.FactCount + facts.DeathCount + facts.TauntCount
                         + facts.IdentityEventCount + facts.EvidenceCount;

            Console.WriteLine($"[bench] file={Path.GetFileName(path)} lines={lines:N0} bytes={new FileInfo(path).Length:N0}");
            Console.WriteLine($"[bench] ingest: {ingestSw.ElapsedMilliseconds:N0} ms  captured={captured:N0} " +
                              $"(damage={facts.FactCount:N0} death={facts.DeathCount:N0} taunt={facts.TauntCount:N0} " +
                              $"identity={facts.IdentityEventCount:N0} evidence={facts.EvidenceCount:N0})");

            mirror.Stop();

            var timeline = new EntityTimeline();

            var seedSw = Stopwatch.StartNew();
            RegistrySeed.Apply(timeline, facts, firstTs, lastTs);
            seedSw.Stop();

            var classSw = Stopwatch.StartNew();
            ClassificationRules.Apply(facts, timeline);
            classSw.Stop();

            var deriveSw = Stopwatch.StartNew();
            var derived = FightDeriver.Derive(facts);
            deriveSw.Stop();

            totalSw.Stop();
            Console.WriteLine($"[bench] seed={seedSw.ElapsedMilliseconds:N0} ms  classify={classSw.ElapsedMilliseconds:N0} ms  " +
                              $"derive={deriveSw.ElapsedMilliseconds:N0} ms  fights={derived.Count:N0}");
            Console.WriteLine($"[bench] TOTAL wall={totalSw.ElapsedMilliseconds:N0} ms");
        }
        finally
        {
            mirror.Stop();
            FightManager.Instance = priorInstance;
            DamageLineParser.FightManager = priorParserFm;
            Console.Out.Flush();
        }
    }

    private sealed class MirrorChatSink : IChatSink
    {
        private readonly CombatMirror _mirror;
        public MirrorChatSink(CombatMirror mirror) => _mirror = mirror;
        public void Init() { }
        public void Add(ChatType chat) => _mirror.HandleChat(chat);
    }

    private sealed class NoOpHook : ITriggerHook
    {
        public void CheckQuickShare(ChatType chat, string action, double beginTime) { }
    }
}
