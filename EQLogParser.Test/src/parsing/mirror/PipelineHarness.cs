using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQLogParser;

// Headless runner for the current (per-line) pipeline: feeds a log file through LogProcessor
// exactly as LogReader does, with no WPF in sight. Mirrors LogReader.HandleLine item shaping —
// only lines past the 28-char header with a parseable date are enqueued, Ts is the dotnet-epoch
// second for the line, IsMonitor is false. Collects fights via FightManager events.
internal static class PipelineHarness
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static EQDataStore? _dataStore;

    internal sealed record RunResult(IReadOnlyList<Fight> Fights);

    // Side channels are inert in headless runs: no chat archive, no trigger evaluation.
    private sealed class NoOpSinks : IChatSink, ITriggerHook
    {
        public void Init()
        {
        }

        public void Add(ChatType chat)
        {
        }

        public void CheckQuickShare(ChatType chat, string action, double beginTime)
        {
        }
    }

    public static RunResult RunFile(string path)
    {
        // CWD so EQDataStore's data/ lookup resolves (the test csproj copies the repo data/ into bin).
        Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
        if (_dataStore == null)
        {
            _dataStore = new EQDataStore();
            EQDataStore.Instance = _dataStore;
        }

        // Clear parser state left by other tests in this process (assembly is serialized, not isolated).
        DamageLineParser.ResetProcessState();

        // Pin the managers for the duration of this run and restore whatever preceded it: parser
        // statics (DamageLineParser.FightManager) and the default singleton are process-global and
        // other test classes set/leak them. Note: singletons the parsers read for names
        // (PlayerRegistry, ConfigUtil) still accumulate across runs — acceptable for Phase 0/1,
        // revisit if comparisons show warm-state drift.
        var priorInstance = FightManager.Instance;
        var priorParserFm = DamageLineParser.FightManager;
        var fm = new FightManager();
        FightManager.Instance = fm;
        DamageLineParser.FightManager = fm;

        var fights = new List<Fight>();
        void Collect(Fight f)
        {
            lock (fights)
            {
                fights.Add(f);
            }
        }

        fm.EventsNewFight += Collect;
        fm.EventsNewNonTankingFight += Collect;

        using var items = new BlockingCollection<LogReaderItem>(new ConcurrentQueue<LogReaderItem>(), 100_000);
        using var processor = new LogProcessor(path, new NoOpSinks(), new NoOpSinks());
        processor.LinkTo(items);

        const int batchSize = 5000;
        var batch = new List<LogReaderItem>(batchSize);
        double lastTs = double.NaN;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length <= 28) continue;
            var dt = DateUtil.ParseStandardDate(line);
            if (dt == DateTime.MinValue) continue;
            var ts = DateUtil.ToDotNetSeconds(dt);
            lastTs = ts;
            batch.Add(new LogReaderItem(line, ts, false));
            if (batch.Count >= batchSize)
            {
                foreach (var item in batch) items.Add(item);
                batch.Clear();
            }
        }

        foreach (var item in batch) items.Add(item);
        items.CompleteAdding();

        // Wait for a full drain before Dispose (which would otherwise race the consumer and drop
        // the tail). Also surfaces consumer exceptions that LogProcessor only logs in-app.
        var completion = processor.Completion;
        if (completion is not null)
        {
            try
            {
                if (!completion.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException($"pipeline did not drain within 60s ({path})");
            }
            catch (AggregateException ae) when (ae.InnerException is not TimeoutException)
            {
                throw ae.InnerException ?? ae;
            }
        }

        // A slain line only flushes when a later-timestamped record calls CheckSlainQueue — in the
        // app that's just the next line of an ongoing log. End-of-file logs never get it, so we
        // simulate exactly one second of follow-up here.
        if (!double.IsNaN(lastTs))
        {
            DamageLineParser.CheckSlainQueue(lastTs + 1);
        }

        DamageLineParser.ResetProcessState();
        DamageLineParser.FightManager = priorParserFm;
        FightManager.Instance = priorInstance;

        processor.Dispose();

        List<Fight> snapshot;
        lock (fights)
        {
            snapshot = [.. fights];
        }

        return new RunResult(snapshot);
    }

    // Phase 0 milestone: dump current-pipeline fight state as JSON for eyeballing and, in Phase 1,
    // as the "current" side of the comparison report.
    public static string ToJson(RunResult result)
    {
        var snapshot = result.Fights.Select(f => new
        {
            f.Name,
            f.Dead,
            f.DamageTotal,
            f.DamageHits,
            f.TankTotal,
            f.TankHits,
            f.BeginDamageTime,
            f.LastDamageTime,
            Players = f.PlayerDamageTotals.ToDictionary(kv => kv.Key, kv => new { kv.Value.Damage, kv.Value.PetOwner })
        });

        return JsonSerializer.Serialize(snapshot, JsonOpts);
    }
}
