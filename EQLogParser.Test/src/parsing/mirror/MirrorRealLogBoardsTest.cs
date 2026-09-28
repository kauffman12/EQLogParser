using System.Diagnostics;
using System.Reflection;

using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * The instrument for the last step of "replace the fight table": put both engines' BOARDS side by side over a
 * real capture, per raider, and count where they disagree.
 *
 * Everything else in this folder compares facts or fight rows. That is the wrong altitude for the decision that is
 * actually pending — whether the grids can be built from derived data — because a grid is per person: a raid of 30
 * scrolling rows, each with its own damage, attempts, overheal and activity window. Two tables can agree on total
 * damage while disagreeing about who did it, and the fight-row comparison cannot see that (a fight row's number is
 * summed over exactly the records its own blocks hold, so a misattribution moves between rows and leaves each row's
 * self-consistency intact). So this runs the real builders twice — once over legacy's Fight list, once over the
 * materialized derived one — and reports the per-column mismatch census.
 *
 * Runs only when EQLP_MIRROR_BOARDS names a log:
 *   EQLP_MIRROR_BOARDS=local/eqlog_Incogitable_xegony.txt dotnet test --filter Boards_RealLog --logger "console;verbosity=detailed"
 * It is slow (a full ingest of the capture plus four summary builds) and holds hundreds of MB, which is exactly what
 * a "select every fight in an eight-hour raid" click costs in the app, and why it is opt-in rather than CI.
 *
 * What it asserts, and why at that strength:
 *   - **Healing must match exactly.** Both boards run the same builder over what should be the same population —
 *     capture fidelity is pinned in MirrorHealCaptureTest, and nothing about a heal is interpreted differently by
 *     the two paths — so any difference here is a bug in the materialization seam (MirrorSummaryHeals), not a
 *     disagreement about classification.
 *   - **Damage/tanking agreement is measured, not demanded.** Legacy drops a record whose attacker it cannot place
 *     and folds a pet under an owner only if its registry learned the pair; the derived board reads the ownership
 *     word inside the line (ClassificationRules.OwnerInName). That gap is +19.5 % on mini-fight.txt and is the
 *     experiment, not an error — so this reports the census, fails only if it collapses to nothing, and prints the
 *     offenders for a human to read. The bar for "no regression" is recorded in docs as it moves.
 *
 * FIRST RUN, `eqlog_Incogitable_xegony.txt` (344 MB, eight hours, 261 named raiders): ingest 19.8 s;
 * legacy 4,471 fights and the projection 4,312 rows; 1,891,875 damage facts, 420,115 heal facts.
 *
 *   - **Healing: exact.** 403,742 heals inside the requested spans, 373 healers on each side, and ZERO mismatched
 *     (person, column) pairs across Total/Hits/SpellHits/Extra/Max/MaxPotentialHit. The record seam is not where the
 *     two engines differ — which is the point of making this one strict.
 *   - **Damage: renamed more than lost.** Raid total 578,233,842,799 → 580,856,919,600 (+0.45 %), rows 422 → 491,
 *     with 384 shared names; the 38 legacy-only entries (`Virul`, `Amengi`s warder`, …) and 107 derived-only ones
 *     (every one of them `X +Pets`) are two views of the same facts. The mechanism is `DamageStatsBuilder`
 *     `.UpdatePetMapping`, which folds by `record.AttackerOwner`: a derived record carries that from the line's own
 *     possessive word, so a ward legacy left as its own row arrives here under its owner with `+Pets`. Only 23 of the
 *     384 people both sides name move at all. `DiagnoseRealLogNames` answers "folded or lost" for any one of them.
 *   - **Tanking: the number was mostly not about people.** Legacy groups by `record.Defender` with no filter, so its
 *     7,114,675,399 of "damage taken" contains 5,120,176,037 across 80 PET rows and 410,382,351 across 108 names
 *     nobody ever identified. Cut both boards to the population the report is about: legacy 1,994,499,362 over 227
 *     rows against derived 1,880,948,516 over 214 — every derived name present on legacy's board (derivedOnly = 0),
 *     and only **10 of 214** people differing at all, the worst being five hits and 1.7 M on one raider, the size of a
 *     fight boundary falling on another second. What derived drops is the 13 NPC rows (`An abyssal terror`, `Bloose`,
 *     `Snack`, `Sabretooth tiger`): mobs taking hits inside a tanking window, which "list what the players received"
 *     has no use for.
 *
 * The `[boards] derived tanking facts with a … defender` print is what made this visible: split the tanking half by
 * what its defenders ARE and Incogitable reads 84,398 facts / 1.576 B Player against 18,022 / 304 M Unknown (and,
 * before the routing was fixed, 287,674 / 6.16 B Pet). The Unknown share stays in on purpose — it is the size of the
 * guess inside `EntityTimeline.IsRaidVictimAt`, whose sibling test measured what demanding proof instead would cost:
 * 90 % of the board gone, because an identity usually arrives later than the hits it describes.
 */
[TestClass]
[DoNotParallelize]
public class MirrorRealLogBoardsTest
{
    // The columns a damage or tanking grid binds, minus the derived ratios (each of which is computed FROM these).
    private static readonly PropertyInfo[] DamageFields =
    [
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.Total))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.Hits))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.MeleeHits))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.SpellHits))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.MeleeAttempts))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.Misses))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.Blocks))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.Dodges))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.Parries))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.RiposteHits))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.Absorbs))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.Invulnerable))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.CritHits))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.FlurryHits))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.RampageHits))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.Max))!,
    ];

    private static readonly PropertyInfo[] HealFields =
    [
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.Total))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.Hits))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.SpellHits))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.Extra))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.Max))!,
        typeof(PlayerSubStats).GetProperty(nameof(PlayerSubStats.MaxPotentialHit))!,
    ];

    private static long Field(PlayerStats stats, PropertyInfo field) => Convert.ToInt64(field.GetValue(stats)!);

    private static Dictionary<string, PlayerStats> By(CombinedStats? stats) =>
        (stats?.StatsList ?? []).GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());

    private static TimeRange WindowOf(IEnumerable<Fight> rows)
    {
        var range = new TimeRange();
        foreach (var row in rows)
        {
            if (!double.IsNaN(row.BeginTime) && !double.IsNaN(row.LastTime)) range.Add(new TimeSegment(row.BeginTime, row.LastTime));
        }

        return range;
    }

    private static CombinedStats? BuildDamage(IReadOnlyList<Fight> rows, TimeRange range)
    {
        var options = new GenerateStatsOptions { AllRanges = range, MinSeconds = 0 };
        foreach (var row in rows) options.Npcs.Add(row);
        DamageStatsBuilder.Instance.BuildTotalStats(options);
        return DamageStatsBuilder.Instance.GetLastStats()?.CombinedStats;
    }

    private static CombinedStats? BuildHeals(IReadOnlyList<Fight> rows, TimeRange range, List<(double, HealRecord)>? heals)
    {
        var options = new GenerateStatsOptions { AllRanges = range, MinSeconds = 0, Heals = heals };
        foreach (var row in rows) options.Npcs.Add(row);
        HealingStatsBuilder.Instance.BuildTotalStats(options);
        return HealingStatsBuilder.Instance.GetLastStats()?.CombinedStats;
    }

    private static CombinedStats? BuildTanking(IReadOnlyList<Fight> rows, TimeRange range)
    {
        var options = new GenerateStatsOptions { AllRanges = range, MinSeconds = 0 };
        foreach (var row in rows) options.Npcs.Add(row);

        CombinedStats? captured = null;
        void OnGeneration(StatsGenerationEvent e)
        {
            if (e.CombinedStats is not null) captured = e.CombinedStats;
        }

        TankingStatsBuilder.Instance.EventsGenerationStatus += OnGeneration;
        try
        {
            TankingStatsBuilder.Instance.BuildTotalStats(options);
        }
        finally
        {
            TankingStatsBuilder.Instance.EventsGenerationStatus -= OnGeneration;
        }

        return captured;
    }

    /*
     * One board's census: how many people appear on each side, and how many columns disagree on the people both
     * sides name. Returns the count of mismatched (person, column) pairs so a caller can watch that number across
     * captures; prints enough detail that the report is actionable without a debugger attached.
     */
    private static long ReportBoard(string board, Dictionary<string, PlayerStats> legacy, Dictionary<string, PlayerStats> derived,
        PropertyInfo[] fields, int examples)
    {
        var shared = legacy.Keys.Intersect(derived.Keys).ToList();
        var onlyLegacy = legacy.Keys.Except(derived.Keys).ToList();
        var onlyDerived = derived.Keys.Except(legacy.Keys).ToList();

        Console.WriteLine($"[boards] {board}: legacy={legacy.Count} derived={derived.Count} shared={shared.Count} "
                          + $"legacyOnly={onlyLegacy.Count} derivedOnly={onlyDerived.Count}");

        if (onlyLegacy.Count > 0)
            Console.WriteLine($"[boards] {board} legacyOnly: {string.Join(", ", onlyLegacy.Take(12))}"
                              + (onlyLegacy.Count > 12 ? " ..." : string.Empty));
        if (onlyDerived.Count > 0)
            Console.WriteLine($"[boards] {board} derivedOnly: {string.Join(", ", onlyDerived.Take(12))}"
                              + (onlyDerived.Count > 12 ? " ..." : string.Empty));

        var columnMismatch = new Dictionary<string, int>();
        var printed = 0;
        long total = 0;

        foreach (var name in shared)
        {
            foreach (var field in fields)
            {
                var l = Field(legacy[name], field);
                var d = Field(derived[name], field);
                if (l == d) continue;

                total++;
                columnMismatch[field.Name] = columnMismatch.GetValueOrDefault(field.Name) + 1;

                if (printed++ < examples)
                    Console.WriteLine($"[diff] {board} / {name} / {field.Name}: legacy={l} derived={d} ({d - l:+#;-#;0})");
            }
        }

        foreach (var (field, count) in columnMismatch.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"[boards] {board} mismatched people on {field}: {count} of {shared.Count}");

        return total;
    }

    [TestMethod]
    public void Boards_RealLog_PerRaiderParity()
    {
        var path = Environment.GetEnvironmentVariable("EQLP_MIRROR_BOARDS");
        if (string.IsNullOrEmpty(path)) Assert.Inconclusive("set EQLP_MIRROR_BOARDS=<path to log> to run");
        if (!File.Exists(path)) Assert.Fail($"EQLP_MIRROR_BOARDS points at nothing: {path}");

        // Only this log's records, or the healing comparison would include whatever other tests parsed.
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();

        var sw = Stopwatch.StartNew();
        var run = PipelineHarness.RunFileWithMirror(path);
        Console.WriteLine($"[boards] file={Path.GetFileName(path)} ingest={sw.ElapsedMilliseconds:N0} ms "
                          + $"legacyFights={run.Fights.Count} derivedRows={run.DerivedFights.Count} "
                          + $"damageFacts={run.Facts.Facts.Length:N0} healFacts={run.HealFacts.HealCount:N0}");

        var index = new MirrorDamageIndex(run.Timeline);
        var rows = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
        Sectionizer.StampGroupIds(rows);

        // The same thing the app does with a select-all click, hidden charm pets included.
        var selected = CharmPetRows.WithHiddenPets(rows, rows);
        var input = MirrorSummaryFights.Build(selected, index, run.Facts);
        Console.WriteLine($"[boards] projected={rows.Count} rows (pet rows hidden by the grid are added back: {selected.Count}), "
                          + $"materialized={input.Fights.Count}, skippedForNoFacts={input.WithoutDamage}");

        Assert.IsTrue(run.Fights.Count > 0, "legacy produced no fights from this log");
        Assert.IsTrue(rows.Count > 0, "the projection produced no rows from this capture");

        var legacyRange = WindowOf(run.Fights);
        var derivedRange = input.AllRanges;

        /*
         * Healing: strict. Same builder, same population by construction — a difference is a seam bug. Both doors
         * are given ONE window on purpose: the fight lists' spans differ by a row or two at the edges of an evening,
         * and a heal sitting in that sliver would be a legitimate difference of scope rather than the thing under
         * test (the record seam). Scope is reported separately below.
         */
        var healWindow = legacyRange;
        var derivedHealRecords = MirrorSummaryHeals.Materialize(run.HealFacts, healWindow);
        Console.WriteLine($"[boards] heal window = the legacy fight spans; materialized={derivedHealRecords.Count:N0} "
                          + $"of {run.HealFacts.HealCount:N0} captured heals (the rest fall outside those spans)");

        // The healer grouping does not consult the fight rows at all (only the grid title does), so passing each
        // side its own list changes nothing here — the window and the record source are the whole difference.
        var legacyHeals = By(BuildHeals(run.Fights, healWindow, null));
        var derivedHeals = By(BuildHeals(input.Fights, healWindow, derivedHealRecords));
        Assert.IsTrue(legacyHeals.Count > 0, "the healing board read nothing from the record store");
        CollectionAssert.AreEquivalent(legacyHeals.Keys.ToList(), derivedHeals.Keys.ToList());

        var healDiffs = ReportBoard("healing", legacyHeals, derivedHeals, HealFields, 20);
        Assert.AreEqual(0L, healDiffs, "a derived healing board disagrees with the store-built one — see [diff] lines");

        // Damage and tanking: census, because the two engines answer ownership differently on purpose.
        var legacyDamage = By(BuildDamage(run.Fights, legacyRange));
        var derivedDamage = By(BuildDamage(input.Fights, derivedRange));
        Assert.IsTrue(legacyDamage.Count > 0, "the damage board read nothing from legacy's fights");

        Console.WriteLine($"[boards] raid damage legacy={legacyDamage.Values.Sum(p => p.Total):N0} "
                          + $"derived={derivedDamage.Values.Sum(p => p.Total):N0}");
        var damageDiffs = ReportBoard("damage", legacyDamage, derivedDamage, DamageFields, 20);

        /*
         * What is actually inside the derived tanking half, by what the timeline says the DEFENDER is. The board is
         * "damage the raid took", so everything but Player should be noise — and noise here is not cosmetic: it is
         * damage attributed to a name that will never appear on the grid, inflating a raid total nobody can audit.
         */
        var byKind = new Dictionary<string, (long Facts, long Total)>();
        foreach (var row in selected)
        {
            foreach (var ordinal in index.TankingOrdinalsFor(row))
            {
                var fact = run.Facts.Facts[ordinal];
                var defender = run.Facts.NameOf(fact.DefIdx) ?? "<none>";
                var kind = run.Timeline.Identity(defender).ToString();
                var (count, sum) = byKind.GetValueOrDefault(kind);
                byKind[kind] = (count + 1, sum + fact.Total);
            }
        }

        foreach (var (kind, (count, sum)) in byKind.OrderByDescending(kv => kv.Value.Total))
            Console.WriteLine($"[boards] derived tanking facts with a {kind} defender: {count:N0} worth {sum:N0}");

        /*
         * Both boards, then BOTH cut to the people the tank report is about. Legacy's board has no such filter — it
         * groups by record.Defender whatever that name is, so pets and mobs sit on it (that is where its 7.11 B
         * lives) — and comparing a raid-taken total across two different populations would be the kind of number that
         * looks like a finding and means nothing. So: report each side's raw total, then census the intersection.
         */
        var legacyTanking = By(BuildTanking(run.Fights, legacyRange));
        var derivedTanking = By(BuildTanking(input.Fights, derivedRange));


        var byKindLegacy = (legacyTanking ?? [])
            .GroupBy(kv => run.Timeline.Identity(kv.Key).ToString())
            .ToDictionary(g => g.Key, g => (g.Count(), g.Sum(kv => kv.Value.Total)));
        foreach (var (kind, (count, sum)) in byKindLegacy.OrderByDescending(kv => kv.Value.Item2))
            Console.WriteLine($"[boards] legacy tank rows with a {kind} name: {count} worth {sum:N0}");

        var legacyVictims = (legacyTanking ?? []).Where(kv => run.Timeline.IsRaidVictimAt(kv.Key, double.PositiveInfinity))
                                                 .ToDictionary(kv => kv.Key, kv => kv.Value);
        var derivedVictims = (derivedTanking ?? []).Where(kv => run.Timeline.IsRaidVictimAt(kv.Key, double.PositiveInfinity))
                                                   .ToDictionary(kv => kv.Key, kv => kv.Value);
        Console.WriteLine($"[boards] raid damage taken BY PEOPLE legacy={legacyVictims.Values.Sum(p => p.Total):N0} "
                          + $"derived={derivedVictims.Values.Sum(p => p.Total):N0}");
        ReportBoard("tanking-people", legacyVictims, derivedVictims, DamageFields, 12);

        Console.WriteLine($"[boards] heal scope: raid healing legacy={legacyHeals.Values.Sum(p => p.Total):N0}, "
                          + $"derived spans cover {SumSpans(input.Fights):N0} s against legacy's {SumSpans(run.Fights):N0} s");
        Console.WriteLine($"[boards] raid damage taken legacy={legacyTanking?.Values.Sum(p => p.Total):N0} "
                          + $"derived={derivedTanking?.Values.Sum(p => p.Total):N0}");
        var tankingDiffs = ReportBoard("tanking", legacyTanking ?? [], derivedTanking ?? [], DamageFields, 20);

        // The same census restricted to names the mirror calls people at the end of the capture: the population the
        // derived board is defined over. What is left out of this line is legacy's pet and mob traffic.


        Console.WriteLine($"[boards] done in {sw.ElapsedMilliseconds:N0} ms — damage diffs={damageDiffs:N0}, "
                          + $"tanking diffs={tankingDiffs:N0}, healing diffs={healDiffs}");

        // Loose but not vacuous. The strict comparison above is reserved for healing, and the reason it cannot be
        // used here is measured in the lines above rather than argued: on Incogitable the two boards are ~25 %
        // differently NAMED — 107 "X +Pets" rows the derived side creates from ownership words legacy's registry
        // never learned, and 38 names legacy lists (pets, warders) that the derived side folds away — while the raid
        // total sits within 0.5 %. Renaming is the experiment; loss would not be, so the bar here is "the board is
        // still about the raid" and every name difference gets printed.
        Assert.IsTrue(derivedDamage.Keys.Count >= legacyDamage.Keys.Count * 0.9,
            "the derived damage board covers far fewer people than legacy's — that looks like loss, not renaming");
    }

    /*
     * "Where did this name's damage go?" — the follow-up question every census line raises, kept because it is the
     * only way to answer it without a debugger on a 344 MB capture.
     *
     *   EQLP_MIRROR_DIAG=Virul,Useless EQLP_MIRROR_BOARDS=<log> dotnet test --filter DiagnoseRealLogNames
     *
     * For each name it prints the legacy rows that carry her, the derived rows that do, every pooled name shaped
     * like hers (pets, warders, `+Pets` groupings) and what the registry thinks she is. Two very different findings
     * look identical in the census — "folded under another key" and "lost" — and only this tells them apart.
     */
    [TestMethod]
    public void DiagnoseRealLogNames()
    {
        var names = Environment.GetEnvironmentVariable("EQLP_MIRROR_DIAG");
        if (string.IsNullOrEmpty(names)) Assert.Inconclusive("set EQLP_MIRROR_DIAG=<name,name> (with EQLP_MIRROR_BOARDS=<log>) to run");

        var path = Environment.GetEnvironmentVariable("EQLP_MIRROR_BOARDS");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Inconclusive("EQLP_MIRROR_BOARDS must name a log for the census to diagnose");

        var wanted = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();

        var run = PipelineHarness.RunFileWithMirror(path);
        var index = new MirrorDamageIndex(run.Timeline);
        var rows = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
        Sectionizer.StampGroupIds(rows);
        var input = MirrorSummaryFights.Build(CharmPetRows.WithHiddenPets(rows, rows), index, run.Facts);

        foreach (var name in wanted)
        {
            Console.WriteLine($"[diag] === {name} :: verifiedPet={PlayerRegistry.Instance.IsVerifiedPet(name)} "
                              + $"ownerKnownFor={PlayerRegistry.Instance.GetPlayerFromPet(name)} "
                              + $"identity={run.Timeline.Identity(name)} "
                              + $"({run.Timeline.IdentityWithSource(name, out var source)}{source})");

            foreach (var row in run.Fights.Where(r => r.PlayerDamageTotals.ContainsKey(name) || r.PlayerTankTotals.ContainsKey(name)))
            {
                row.PlayerDamageTotals.TryGetValue(name, out var dealt);
                row.PlayerTankTotals.TryGetValue(name, out var taken);
                Console.WriteLine($"[diag] legacy row '{row.Name}' [{row.BeginTime:F0}->{row.LastTime:F0}] dealt={dealt?.Damage ?? 0:N0} "
                                  + $"taken={taken?.Damage ?? 0:N0} petOwner={dealt?.PetOwner ?? taken?.PetOwner}");
            }

            // A derived row is per encounter rather than per raider — the people inside it appear when the board
            // groups its records — so what a row can say here is its own span, its owner and its two totals.
            foreach (var row in input.Fights.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"[diag] derived row '{row.Name}' [{row.BeginTime:F0}->{row.LastTime:F0}] damage={row.DamageTotal:N0} "
                                  + $"taken={row.TankTotal:N0} charmOwnerAtStart={run.Timeline.OwnerOf(row.Name, row.BeginTime)}");

            // Every name in the capture that shares this one's shape: pet suffixes, the +Pets grouping, spellings.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fact in run.Facts.Facts)
            {
                foreach (var candidate in new[] { run.Facts.NameOf(fact.AtkIdx), run.Facts.NameOf(fact.DefIdx) })
                {
                    if (candidate is null || !candidate.Contains(name, StringComparison.OrdinalIgnoreCase) || !seen.Add(candidate)) continue;

                    Console.WriteLine($"[diag] pooled name '{candidate}' identity={run.Timeline.Identity(candidate)} "
                                      + $"ownerOf={run.Timeline.OwnerOf(candidate, double.PositiveInfinity)} "
                                      + $"verifiedPet={PlayerRegistry.Instance.IsVerifiedPet(candidate)}");
                }
            }
        }
    }

    // How much wall-clock the selected rows claim, which is what a window over them can hold.
    private static double SumSpans(IEnumerable<Fight> rows)
    {
        double total = 0;
        foreach (var row in rows)
        {
            if (!double.IsNaN(row.BeginTime) && !double.IsNaN(row.LastTime)) total += Math.Max(0, row.LastTime - row.BeginTime);
        }

        return total;
    }

}
