using System.Collections.Concurrent;

using EQLogParser;

namespace EQLogParser;

// Headless runner for the (per-line) pipeline with the CombatCapture tap: feeds a log file through
// LogProcessor exactly as LogReader does, with no WPF in sight. Mirrors LogReader.HandleLine item
// shaping — only lines past the 28-char header with a parseable date are enqueued, Ts is the
// dotnet-epoch second for the line, IsMonitor is false. The result is the capture's fact tables
// plus a RegistrySeed'd timeline; the fight rows themselves are whatever the caller derives from
// them (FightProjection.Build is the call the app's DeriveEngine makes).
internal static class PipelineHarness
{
    private static EQDataStore? _dataStore;

    internal sealed record DeriveRunResult(
        DamageFactTable Facts,
        EntityTimeline Timeline,

        // Captured on every capture run, exactly as the app's DeriveEngine captures it, so a test that says
        // "the capture saw the same heals the healing board reads" is testing the wiring users get.
        HealFactTable HealFacts);

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

    // Mirror runs additionally feed fully-classified chat to the tap (R3 evidence). This is the
    // same seam the WPF app uses for ChatDB — no Core-side plumbing added for it.
    private sealed class CaptureSinks : IChatSink, ITriggerHook
    {
        private readonly CombatCapture _capture;

        public CaptureSinks(CombatCapture capture) => _capture = capture;

        public void Init()
        {
        }

        public void Add(ChatType chat) => _capture.HandleChat(chat);

        public void CheckQuickShare(ChatType chat, string action, double beginTime)
        {
        }
    }

    public static DeriveRunResult RunFileDerived(string path)
    {
        var (facts, heals, timeline) = RunCore(path);
        return new DeriveRunResult(facts, timeline, heals);
    }

    // onEvent observes every processed damage record from the test thread (same instant the
    // pipeline sees it) — debugging hook for live-state inspection of the pipeline.
    public static DeriveRunResult RunFileDerived(string path, Action<DamageProcessedEvent> onEvent)
    {
        var (facts, heals, timeline) = RunCore(path, onEvent);
        return new DeriveRunResult(facts, timeline, heals);
    }

    // CWD so EQDataStore's data/ lookup resolves (the test csproj copies the repo data/ into bin).
    // Public because tests that only touch EQDataStore (no log run) still need the host injection.
    internal static void EnsureDataStore()
    {
        Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
        if (_dataStore == null)
        {
            // Host injection mirroring App.xaml.cs (the app supplies resx labels; the default
            // lookup returns null, which would leave EQDataStore's class maps empty headless).
            CombatRecordLookup.ClassLabelByEnumName = ClassLabel;
            _dataStore = new EQDataStore();
            EQDataStore.Instance = _dataStore;
        }
    }

    // Canonical EQ2 class labels keyed by uppercased SpellClass enum names — the same resource
    // keys App.xaml.cs resolves through the resx ("WAR", "SHD", ...; "*_COLOR" returns null here).
    // The host contract is nullable-oblivious (CombatRecordLookup.ClassLabelByEnumName defaults to `=> null` and
    // EQDataStore tests the answer for emptiness), so returning null for a "*_COLOR" name is the documented answer,
    // not a possible-null slip: same pragma pair DamageLineParserTest uses for the host-injected lookups.
#pragma warning disable CS8603 // Possible null reference return.
    private static string ClassLabel(string resourceName) => resourceName switch
    {
        "WAR" => "Warrior",
        "CLR" => "Cleric",
        "PAL" => "Paladin",
        "RNG" => "Ranger",
        "SHD" => "Shadow Knight",
        "DRU" => "Druid",
        "MNK" => "Monk",
        "BRD" => "Bard",
        "ROG" => "Rogue",
        "SHM" => "Shaman",
        "NEC" => "Necromancer",
        "WIZ" => "Wizard",
        "MAG" => "Magician",
        "ENC" => "Enchanter",
        "BST" => "Beastlord",
        "BER" => "Berserker",
        _ => null
    };
#pragma warning restore CS8603 // Possible null reference return.

    private static (DamageFactTable, HealFactTable, EntityTimeline) RunCore(string path, Action<DamageProcessedEvent>? onEvent = null)
    {
        EnsureDataStore();

        // EMU captures (local/logs/emu/) parse with the app's EnableEmuParsing switch on: DamageLineParser carries a
        // second grammar for them - Heroes Forge `(Owner: X)` lines, old-EMU `scores a critical hit! (N)` pairing,
        // absorbed-damage shapes. EQLP_EMU=1 marks this run's file as one, exactly like settings.txt does in the app
        // (MainWindow reads it once before any parse). Restored with the other process state at the end of this
        // method: live-format logs MISPARSE when the flag is left on.
        var priorEmu = AppSettings.IsEmuParsingEnabled;
        AppSettings.IsEmuParsingEnabled = Environment.GetEnvironmentVariable("EQLP_EMU") == "1";

        // The app derives the local player from the log filename (eqlog_(Player)_(Server).txt);
        // do the same so You-mapping and the R0-local rule behave as they will in production.
        // Fixture names never match, so synthetic runs keep PlayerName untouched.
        var selfMatch = System.Text.RegularExpressions.Regex.Match(
            Path.GetFileNameWithoutExtension(path), @"^eqlog_(.+?)_.+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (selfMatch.Success) ConfigUtil.PlayerName = selfMatch.Groups[1].Value;

        // Clear parser state left by other tests in this process (assembly is serialized, not isolated).
        DamageLineParser.ResetProcessState();

        // The registry is a process-lifetime singleton the parsers read for every name lookup;
        // without this, verifications from earlier tests' logs leak into this run's routing
        // (measured: the same real log compared as 4473 vs 4274 current fights across two runs).
        // Same pattern LineParsersTest uses. In tests ConfigUtil.ServerName is empty, so Clear()
        // does not Save() anything.
        PlayerRegistry.Instance.Clear();

        Action<DamageProcessedEvent>? observer = null;
        if (onEvent is not null)
        {
            observer = e => onEvent(e);
            DamageLineParser.EventsDamageProcessed += observer;
        }

        var facts = new DamageFactTable(100_000);
        var heals = new HealFactTable(facts);
        var timeline = new EntityTimeline();
        var capture = new CombatCapture(facts, heals);
        capture.Start();

        using var items = new BlockingCollection<LogReaderItem>(new ConcurrentQueue<LogReaderItem>(), 100_000);
        // capture came from `new` three lines up: an `is not null` check here would not just be
        // dead, it poisons the flow analysis and makes every later `capture.` a CS8602.
        using var processor = new LogProcessor(path, new CaptureSinks(capture), new NoOpSinks());
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
        // Cap scales with file size: fixtures get 60 s, a 588 MB log gets ~20 — a hard failure
        // here means the pipeline stalled, not that we ran out of patience.
        var drainCap = TimeSpan.FromSeconds(Math.Max(60, new FileInfo(path).Length / (1024.0 * 1024) * 2));
        var completion = processor.Completion;
        if (completion is not null)
        {
            try
            {
                if (!completion.Wait(drainCap))
                    throw new TimeoutException($"pipeline did not drain within {drainCap.TotalSeconds:F0}s ({path})");
            }
            catch (AggregateException ae) when (ae.InnerException is not TimeoutException)
            {
                throw ae.InnerException ?? ae;
            }
        }

        capture.Stop();

        // Identity evidence for the rules (and report context): the registry's own knowledge with
        // evidence times, applied to the timeline the caller receives. Tests that want classified
        // verdicts still call ClassificationRules.Apply themselves over this seed.
        SeedIdentity(timeline, facts, firstTs, lastTs);

        if (observer is not null) DamageLineParser.EventsDamageProcessed -= observer;
        DamageLineParser.ResetProcessState();
        AppSettings.IsEmuParsingEnabled = priorEmu;

        processor.Dispose();

        return (facts, heals, timeline);
    }

    // Registry end-state + evidence times as manual identity assignments. Strengths stay below the
    // Phase 2 rule tiers so rule output overrides this seed when both are present (R10 > R2 > …).
    // Cold/warm registry semantics live in Core so the app session and headless runs cannot drift.
    private static void SeedIdentity(EntityTimeline timeline, IFactTable facts, double logStartS, double logEndS)
        => RegistrySeed.Apply(timeline, facts, logStartS, logEndS);

}
