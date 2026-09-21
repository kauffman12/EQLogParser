using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

using EQLogParser.Mirror;

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

    // Phase 1: the same run with the CombatMirror tap active and the derivation executed.
    // Fights are creation-ordered (EventsNewFight only) — the natural pairing for the derived list.
    internal sealed record MirrorRunResult(
        IReadOnlyList<Fight> Fights,
        IReadOnlyList<Fight> NonTankingFights,
        IReadOnlyList<DerivedFight> DerivedFights,
        DamageFactTable Facts,
        EntityTimeline Timeline);

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
        var (fights, _, _, _, _) = RunCore(path, withMirror: false);
        return new RunResult(fights);
    }

    public static MirrorRunResult RunFileWithMirror(string path)
    {
        var (fights, nonTanking, derived, facts, timeline) = RunCore(path, withMirror: true);
        return new MirrorRunResult(fights, nonTanking, derived, facts, timeline);
    }

    private static (List<Fight>, List<Fight>, List<DerivedFight>, DamageFactTable, EntityTimeline) RunCore(string path, bool withMirror)
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
        var nonTanking = new List<Fight>();
        void Collect(Fight f)
        {
            lock (fights)
            {
                fights.Add(f);
            }
        }

        void CollectNonTanking(Fight f)
        {
            lock (nonTanking)
            {
                nonTanking.Add(f);
            }
        }

        // EventsNewFight fires once per creation (TryAdd) — creation order, the pairing key for
        // the derived list. The old RunResult also included the non-tanking events (duplicates),
        // which the comparison must not do.
        fm.EventsNewFight += Collect;
        fm.EventsNewNonTankingFight += CollectNonTanking;

        DamageFactTable? facts = null;
        EntityTimeline? timeline = null;
        CombatMirror? mirror = null;
        if (withMirror)
        {
            facts = new DamageFactTable(100_000);
            timeline = new EntityTimeline();
            mirror = new CombatMirror(facts);
            mirror.Start();
        }

        using var items = new BlockingCollection<LogReaderItem>(new ConcurrentQueue<LogReaderItem>(), 100_000);
        using var processor = new LogProcessor(path, new NoOpSinks(), new NoOpSinks());
        processor.LinkTo(items);

        const int batchSize = 5000;
        var batch = new List<LogReaderItem>(batchSize);
        double firstTs = double.NaN;
        double lastTs = double.NaN;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length <= 28) continue;
            var dt = DateUtil.ParseStandardDate(line);
            if (dt == DateTime.MinValue) continue;
            var ts = DateUtil.ToDotNetSeconds(dt);
            if (double.IsNaN(firstTs)) firstTs = ts;
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

        mirror?.Stop();

        List<DerivedFight> derived = [];
        if (withMirror && facts is not null && timeline is not null)
        {
            // Phase 1 bootstrap seed (Phase 2 rules replace this): the registry's own knowledge —
            // player-side names with their evidence times, so ingest-time replay matches what
            // IsPetOrPlayerOrMerc answered at each line.
            SeedIdentity(timeline, facts, firstTs, lastTs);
            derived = FightDeriver.Derive(facts, timeline);
        }

        DamageLineParser.ResetProcessState();
        DamageLineParser.FightManager = priorParserFm;
        FightManager.Instance = priorInstance;

        processor.Dispose();

        List<Fight> snapshot;
        List<Fight> nonTankingSnapshot;
        lock (fights)
        {
            snapshot = [.. fights];
            nonTankingSnapshot = [.. nonTanking];
        }

        return (snapshot, nonTankingSnapshot, derived, facts ?? new DamageFactTable(1), timeline ?? new EntityTimeline());
    }

    // Registry end-state + evidence times as manual identity assignments. Strengths stay below the
    // Phase 2 rule tiers so rule output overrides this seed when both are present (R10 > R2 > …).
    private const int SeedStrengthVerified = 8;
    private const int SeedStrengthYou = 10;

    private static void SeedIdentity(EntityTimeline timeline, IFactTable facts, double logStartS, double logEndS)
    {
        var registry = PlayerRegistry.Instance;

        foreach (var kv in registry.GetVerifiedPlayerTimes())
        {
            // evidence time inside this log -> ingest-replay; outside (persisted warm state or
            // user-set "now") -> retroactive over the whole log, same as Contains at ingest time
            var eff = !double.IsNaN(logStartS) && kv.Value >= logStartS && kv.Value <= logEndS ? kv.Value : double.NegativeInfinity;
            timeline.SetIdentity(kv.Key, IdentityKind.Player, SeedStrengthVerified, "RegistrySeed", eff);
        }

        // pets carry no evidence time — retroactive (documented approximation: a pet verified
        // mid-log is slightly earlier here than at ingest time in the current pipeline)
        foreach (var pet in registry.GetVerifiedPets())
        {
            timeline.SetIdentity(pet, IdentityKind.Player, SeedStrengthVerified, "RegistrySeed", double.NegativeInfinity);
        }

        var player = ConfigUtil.PlayerName;
        if (!string.IsNullOrEmpty(player))
        {
            timeline.SetIdentity(player, IdentityKind.Player, SeedStrengthYou, "You");
        }

        // mercs have no enumeration or times — check every name the facts touched, end-state only
        foreach (var name in facts.InternedNames)
        {
            if (registry.IsMerc(name))
            {
                timeline.SetIdentity(name, IdentityKind.Merc, SeedStrengthVerified, "RegistrySeed", double.NegativeInfinity);
            }
        }
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
