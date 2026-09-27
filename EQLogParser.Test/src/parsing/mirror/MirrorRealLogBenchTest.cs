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

        // Same regex as PipelineHarness.RunCore. An earlier version ended in \.txt$ while reading
        // the name WITHOUT extension, so it never matched: PlayerName stayed empty, every record
        // kept "You" instead of the character name, and legacy+mirror both under-counted together
        // (222 vs the real 261 on the 09-17 capture) - two consistent numbers is not proof.
        var selfMatch = System.Text.RegularExpressions.Regex.Match(
            Path.GetFileNameWithoutExtension(path), @"^eqlog_(.+?)_.+$",
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

        // Legacy count inside the same run: "derived == current" here is what MirrorComparisonTest
        // asserts end-to-end; seeing both numbers side by side says whether a difference is parser
        // variance across processes or a deriver gap (learned the hard way on the 09-17 capture).
        var legacyFights = 0;
        fm.EventsNewFight += _ => Interlocked.Increment(ref legacyFights);

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
            var rules = ClassificationRules.Apply(facts, timeline);
            classSw.Stop();

            PrintNamePoolReport(facts);
            PrintCharmReport(facts, rules.Charms);
            PrintProjectionReport(facts, timeline);

            var deriveSw = Stopwatch.StartNew();
            var derived = FightDeriver.Derive(facts);
            deriveSw.Stop();

            totalSw.Stop();
            Console.WriteLine($"[bench] seed={seedSw.ElapsedMilliseconds:N0} ms  classify={classSw.ElapsedMilliseconds:N0} ms  " +
                              $"derive={deriveSw.ElapsedMilliseconds:N0} ms  fights={derived.Count:N0}  legacy-fights={legacyFights:N0}");
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

    /*
     * Charm accounting over the real capture: how many windows the sightings collapse into, who owns
     * them, what closed them, and how much damage moves from an NPC row to a pet row. The percentage is
     * the number that matters when a charm rule changes — it is what that rule is worth in HP, not how
     * many lines it touches (25 windows in a night is nothing; the credit inside them is the fight).
     */
    /*
     * The name pool's health over a real capture. It used to report how much of the pool was ONE entity spelled two
     * ways (facts arrive capitalized via ParserUtil's CapitalizeFirst, evidence lines keep what EQ wrote), which
     * measured 285 of 2,721 on Incogitable and 40 of 558 on 9-18-22; the pool has since merged them and the size came
     * down by exactly those amounts.
     *
     * The two numbers that matter now are both zeros: no stored string may still start lower-case
     * (every entry went through CapitalizeFirst, so a row's name does not depend on which line arrived first), and no
     * two entries may be the same name in different cases (one entity, one id). `pool=` is the cost line, read
     * against the pre-change measurements above.
     */
    private static void PrintNamePoolReport(DamageFactTable facts)
    {
        var names = facts.InternedNames;
        var nonCanonical = 0;
        foreach (var n in names)
            if (n.Length > 0 && char.IsLower(n[0])) nonCanonical++;

        var ignoreCaseDistinct = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase).Count;

        Console.WriteLine($"[names] pool={names.Count:N0} non-canonical={nonCanonical} " +
                          $"case-collisions={names.Count - ignoreCaseDistinct}");
    }

    private static void PrintCharmReport(DamageFactTable facts, List<CharmWindow> charms)
    {
        if (charms.Count == 0)
        {
            Console.WriteLine("[charm] no charm windows in this capture");
            return;
        }

        ulong total = 0;
        foreach (var f in facts.Facts) total += f.Total;

        // Summed by hand: LINQ's Sum has no ulong overload, and the ambiguity resolves to decimal.
        ulong credited = 0;
        var lines = 0;
        var sameName = 0;
        foreach (var w in charms)
        {
            credited += w.CreditedTotal;
            lines += w.FactCount;
            sameName += w.SameNameFactCount;
        }

        Console.WriteLine($"[charm] windows={charms.Count} sightings={charms.Sum(static w => w.Starts)} " +
                          $"owned={charms.Count(static w => w.Owned)} ownerless={charms.Count(static w => !w.Owned)}");
        foreach (var r in Enum.GetValues<CharmEndReason>())
        {
            var n = charms.Count(w => w.Reason == r);
            if (n > 0) Console.WriteLine($"[charm]   closed by {r}: {n}");
        }
        Console.WriteLine($"[charm] credited inside windows={credited:N0} of {total:N0} " +
                          $"({(total == 0 ? 0 : 100.0 * credited / total):F4}% of log damage)  lines={lines:N0} " +
                          $"ambiguous same-name lines={sameName:N0} ({(lines == 0 ? 0 : 100.0 * sameName / lines):F1}%)");

        // One entry per mob NAME — the roll-up the pet list shows, six charms of one mob type as one row.
        var byName = charms.GroupBy(static w => w.Name, StringComparer.OrdinalIgnoreCase)
                           .Select(g => (Name: g.Key, Windows: g.Count(), Sightings: g.Sum(static w => w.Starts),
                                         Damage: g.Aggregate(0UL, static (acc, w) => acc + w.CreditedTotal),
                                         Owners: string.Join(",", g.Select(static w => w.Owner ?? "unowned").Distinct()),
                                         Closes: string.Join(",", g.Select(static w => w.Reason.ToString()).Distinct())))
                           .OrderByDescending(static e => e.Damage).Take(12);
        foreach (var e in byName)
        {
            Console.WriteLine($"[charm]   {e.Name,-36} windows={e.Windows} sightings={e.Sightings} " +
                              $"dmg={e.Damage:N0} owners=[{e.Owners}] closes=[{e.Closes}]");
        }
    }

    /*
     * The DISPLAY list over the same classification, counted by how each row ended. This is where a charm rule
     * becomes visible UI state: "Charmed" rows are mob encounters the raid finished by taking them (counted as
     * deaths), and a Gap count that shrinks while Charmed grows is the point of the boundary — those rows were
     * previously indistinguishable from "we stopped hitting it for five minutes".
     */
    private static void PrintProjectionReport(DamageFactTable facts, EntityTimeline timeline)
    {
        var rows = FightProjection.Build(facts, timeline);
        // The displayed list is not the projection: CharmPetRows keeps a charmed mob's own rows off it (a pet has
        // no fight row), and that difference is what a user reads as "fights".
        Console.WriteLine($"[rows] list={rows.Count:N0}  visible={CharmPetRows.Visible(rows).Count:N0}"
                          + $"  charmed-owned={rows.Count(static r => r.CharmedOwned)}");
        foreach (var g in rows.GroupBy(static r => r.EndReason).OrderByDescending(static g => g.Count()))
            Console.WriteLine($"[rows]   ended {g.Key}: {g.Count()}");
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
