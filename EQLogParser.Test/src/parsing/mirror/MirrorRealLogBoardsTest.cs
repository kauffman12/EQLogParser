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
 *   - **Damage: renamed more than lost.** Raid total 578,233,842,799 → 581,343,395,690 (+0.54 %), rows 422 → 437,
 *     with 374 shared names; the 48 legacy-only entries (`Virul`, `Amengi`s warder`, …) and 63 derived-only ones
 *     (every one of them `X +Pets`) are two views of the same facts. The mechanism is `DamageStatsBuilder`
 *     `.UpdatePetMapping`, which folds by `record.AttackerOwner`: a derived record carries that from the line's own
 *     possessive word, so a ward legacy left as its own row arrives here under its owner with `+Pets`. Only 22 of the
 *     374 people both sides name move at all. `DiagnoseRealLogNames` answers "folded or lost" for any one of them.
 *   - **Tanking: legacy's number was mostly not about people.** Legacy groups by `record.Defender` with no filter, so
 *     its 7,114,675,399 of "damage taken" is 4,823,236,582 across 21 names the classification calls NPC, 428,146,449
 *     across 75 pets and 27,895,838 across 48 names nobody ever placed. Cut both boards to the population the report
 *     is about — legacy 1,863,292,368 over 211 rows against derived **1,850,853,404 over 166**, sharing 165 — and only
 *     **3 of 165** people differ on Total at all. The 46 legacy-only rows are names no rule placed (`Batvar`, `Worthless`,
 *     `Boner`), and derived adds exactly one (`Rallosian Goblin Defender`, also unplaced): the two boards disagree about
 *     the residue, not about the raid.
 *
 * The `[boards] derived tanking facts with a … defender` print is what made this visible: split the tanking half by what
 * its defenders ARE and Incogitable reads 91,036 facts / 1.6389 B on Players and 9,480 / 196.4 M on Mercs against **86 /
 * 15.6 M Unknown** — no Pet rows at all, which is the routing (`RaidSide`) doing its job. That 86 is also the whole cost
 * of `EntityTimeline.IsRaidVictimAt` being exclusion rather than proof (0.85 % of the board), and `HitByNpcCensusTest`
 * is where the idea that "a mob hitting it proves it is a player" could add anything to it was measured and found dead.
 *
 * Every number above is measured on `Classified(run)`, the timeline the app derives against. The first run of this test
 * used the timeline `PipelineHarness` hands back, which carries registry seeds only: on that state R6 (npcs.txt), R14
 * (article shape) and R15 (healed by our side) have placed nothing, "unplaced defenders" reads 1,525,786 facts instead
 * of 4,323, and every identity-dependent figure in the comparison is fiction. Classify before asking an identity
 * question — AGENTS.md keeps that law because it cost a whole rule proposal.
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

    /*
     * The classification the product runs before it derives anything: MirrorSession builds a fresh timeline, seeds it
     * from the registry and replays the rule table inside every derive. PipelineHarness hands back a timeline with the
     * registry seeds ONLY, which is fine for questions about facts and wrong for questions about identity — measured
     * on Incogitable, "how many damage facts have a defender nothing has placed" reads 1,525,786 on a seeded-only
     * timeline against 4,323 once the rules have run, because that is how many mobs R6/R14/R15 place out of the way.
     * Anything that touches IsRaidVictimAt, pet folding or charm ownership has to be measured on this, not on the seed.
     */
    private static EntityTimeline Classified(PipelineHarness.MirrorRunResult run)
    {
        var timeline = new EntityTimeline();
        var first = run.Facts.Facts.Length > 0 ? run.Facts.Facts[0].TimeS : 0;
        var last = run.Facts.Facts.Length > 0 ? run.Facts.Facts[^1].TimeS : 0;

        RegistrySeed.Apply(timeline, run.Facts, first, last);
        ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
        return timeline;
    }

    /* The test host's working directory is the output folder when it runs a rebuilt assembly, so a repo-relative log
     * path only works if it is walked up to. Absolute paths pass straight through. (This file used to assume the
     * invocation's directory, which made the same command work or skip depending on whether a rebuild happened.) */
    private static string? Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || File.Exists(path)) return path;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, path);
            if (File.Exists(candidate)) return candidate;
        }

        return path;
    }

    private static long Field(PlayerStats stats, PropertyInfo field) => Convert.ToInt64(field.GetValue(stats)!);

    /* The person a damage row reports on. `X +Pets` IS X - the builder folds an owner's own hits into that aggregate
     * once it knows her summons - and any other row reports on whoever its name says. */
    private static string PersonOf(string rowName) =>
        rowName.EndsWith(" +Pets", StringComparison.Ordinal) ? rowName[..^" +Pets".Length] : rowName;

    /* The people a board is a report about, by the mirror's own final verdict: Player and Merc only. Pet rows are
     * covered through PersonOf, and a name no rule placed is not evidence about the raid either way (it is still
     * printed in the raw census above, where the reader can see what it did). */
    private static HashSet<string> RaidSidePeople(Dictionary<string, PlayerStats> board, EntityTimeline timeline) =>
        board.Keys.Select(PersonOf)
            .Where(n => timeline.Identity(n) is IdentityKind.Player or IdentityKind.Merc)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
        var path = Resolve(Environment.GetEnvironmentVariable("EQLP_MIRROR_BOARDS"));
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

        /*
         * Timed in the order a user feels them: classify+project is the wait before the fight list can be drawn, and
         * materialize is what the WORST click costs — select everything and build a board over an evening. The
         * projection is what the list itself needs; materialization never runs for the list alone.
         */
        var step = Stopwatch.StartNew();
        var timeline = Classified(run);
        var index = new MirrorDamageIndex(timeline);
        var rows = FightProjection.Build(run.Facts, timeline, index.OnFact);
        Sectionizer.StampGroupIds(rows);
        Console.WriteLine($"[boards] classify+project={step.ElapsedMilliseconds:N0} ms for {rows.Count} fight rows");
        step.Restart();

        // The same thing the app does with a select-all click, hidden charm pets included.
        var selected = CharmPetRows.WithHiddenPets(rows, rows);
        var input = MirrorSummaryFights.Build(selected, index, run.Facts);
        Console.WriteLine($"[boards] projected={rows.Count} rows (pet rows hidden by the grid are added back: {selected.Count}), "
                          + $"materialized={input.Fights.Count}, skippedForNoFacts={input.WithoutDamage}, "
                          + $"materialize={step.ElapsedMilliseconds:N0} ms");

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
         * Population, not names, because row names are not comparable between the two engines. Two reasons, both
         * measured: legacy groups by record.Attacker whatever that name is, so its hostile-name list (npcs.txt) puts
         * NPCs on its damage board - `Elmara Emberclaw` and `Dhakka Nogg` on eqlog_Kizant_xegony-09-08-24.txt, both
         * Npc:R6-npcdb, dealing ~31 M TO the raid - while the derived side files that on the mob's row. And a PERSON is
         * listed as `X +Pets` whenever the builder knows her summons: DamageStatsBuilder folds an owner's own hits into
         * that aggregate and demotes her plain row below top level, and the derived side knows far more owners than the
         * legacy registry ever learned because it reads the line's own possessive word (on eqlog_Kizant_xegony.txt
         * every one of legacy's 8 person rows arrives as `X +Pets`). So this asks the question that is actually about
         * loss: is there a raid member legacy lists who has no row of her own in the derived board under EITHER name?
         */
        var legacyPeople = RaidSidePeople(legacyDamage, timeline);
        var derivedPeople = RaidSidePeople(derivedDamage, timeline);
        var absentPeople = legacyPeople.Where(p => !derivedPeople.Contains(p)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"[boards] damage population (people, +Pets counted as the person): legacy={legacyPeople.Count} "
                          + $"derived={derivedPeople.Count} shared={legacyPeople.Count(p => derivedPeople.Contains(p))} "
                          + $"absentFromDerived={absentPeople.Count}"
                          + (absentPeople.Count > 0 ? $": {string.Join(", ", absentPeople.Take(15))}" : ""));

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
                var kind = timeline.Identity(defender).ToString();
                var (count, sum) = byKind.GetValueOrDefault(kind);
                byKind[kind] = (count + 1, sum + fact.Total);
            }
        }

        foreach (var (kind, (count, sum)) in byKind.OrderByDescending(kv => kv.Value.Total))
            Console.WriteLine($"[boards] derived tanking facts with a {kind} defender: {count:N0} worth {sum:N0}");

        /*
         * Every fact the walk never announces, priced by which rule refused it. FightProjection has four `continue`s
         * before the sink and each one is a decision about what a meter is allowed to forget: self damage, spell
         * feedback (a verb sitting in the attacker field), raid-on-raid with no pet or charmed mob at the far end,
         * and two mobs on each other. The fifth line is NOT a drop — it is the bucket the unplaced-victim branch now
         * admits, kept because it measures how much of "damage our people received" rests on EXCLUSION (whatever takes
         * a mob's hits is one of ours) rather than on evidence. Before that branch existed this bucket was dropped as
         * mob-on-mob noise and the tank board lost raiders wholesale: on this capture `Worthless` 1,479,310,
         * `Boner` 1,389,581, `Morris` 1,360,995, and 80 of Ddread's 85 incoming facts.
         */
        var unannounced = new Dictionary<string, (long Facts, long Total)>();
        var unplacedDefenders = new Dictionary<string, long>();
        void Drop(string bucket, DamageFact f)
        {
            var (count, sum) = unannounced.GetValueOrDefault(bucket);
            unannounced[bucket] = (count + 1, sum + f.Total);
        }

        for (var i = 0; i < run.Facts.Facts.Length; i++)
        {
            var f = run.Facts.Facts[i];
            if (f.AtkIdx == f.DefIdx) { Drop("self damage", f); continue; }

            var atk = run.Facts.NameOf(f.AtkIdx);
            var def = run.Facts.NameOf(f.DefIdx);
            if (atk is null || def is null) continue;

            var atkSide = FightProjection.SideAt(timeline, atk, f.TimeS);
            var defSide = FightProjection.SideAt(timeline, def, f.TimeS);

            if (atkSide != FightProjection.Side.Player && ClassificationRules.IsSelfTargetDamageSpell(atk))
            {
                Drop("spell feedback (a verb in the attacker field)", f); continue;
            }

            if (atkSide == FightProjection.Side.Player && defSide == FightProjection.Side.Player)
            {
                if ((!timeline.IsCharmedAt(def, f.TimeS) && !timeline.IsOurPetAt(def, f.TimeS)) || timeline.IsCharmedAt(atk, f.TimeS))
                    Drop("friendly fire", f);
                continue;
            }

            if (atkSide == FightProjection.Side.Npc && defSide == FightProjection.Side.Npc)
            {
                if (!timeline.IsCharmedAt(atk, f.TimeS)) Drop("mob on mob", f);
            }

            // Not a drop: what the tank board now owes to EXCLUSION rather than proof, which is the number worth
            // watching across captures (it is how much of "damage our people received" is decided by reasoning
            // about who takes a mob's hits instead of by evidence that the victim is one of us).
            if (atkSide == FightProjection.Side.Npc && defSide == FightProjection.Side.Unknown && !timeline.IsCharmedAt(atk, f.TimeS))
            {
                Drop($"admitted: mob on unplaced -> final {timeline.Identity(def)}", f);
                unplacedDefenders[def] = unplacedDefenders.GetValueOrDefault(def) + f.Total;
            }
        }

        foreach (var (bucket, (count, sum)) in unannounced.OrderByDescending(kv => kv.Value.Total))
            Console.WriteLine($"[boards] {bucket}: {count:N0} facts worth {sum:N0}");

        var rosterInBucket = unplacedDefenders.Keys.Count(n => PlayerRegistry.Instance.IsVerifiedPlayer(n));
        Console.WriteLine($"[boards] unplaced defenders hit by a mob: {unplacedDefenders.Count} names, "
                          + $"{rosterInBucket} of them in the player list; top: "
                          + string.Join(", ", unplacedDefenders.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} {kv.Value:N0}")));

        /*
         * Both boards, then BOTH cut to the people the tank report is about. Legacy's board has no such filter — it
         * groups by record.Defender whatever that name is, so pets and mobs sit on it (that is where its 7.11 B
         * lives) — and comparing a raid-taken total across two different populations would be the kind of number that
         * looks like a finding and means nothing. So: report each side's raw total, then census the intersection.
         */
        var legacyTanking = By(BuildTanking(run.Fights, legacyRange));
        var derivedTanking = By(BuildTanking(input.Fights, derivedRange));


        var byKindLegacy = (legacyTanking ?? [])
            .GroupBy(kv => timeline.Identity(kv.Key).ToString())
            .ToDictionary(g => g.Key, g => (g.Count(), g.Sum(kv => kv.Value.Total)));
        foreach (var (kind, (count, sum)) in byKindLegacy.OrderByDescending(kv => kv.Value.Item2))
            Console.WriteLine($"[boards] legacy tank rows with a {kind} name: {count} worth {sum:N0}");

        var legacyVictims = (legacyTanking ?? []).Where(kv => timeline.IsRaidVictimAt(kv.Key, double.PositiveInfinity))
                                                 .ToDictionary(kv => kv.Key, kv => kv.Value);
        var derivedVictims = (derivedTanking ?? []).Where(kv => timeline.IsRaidVictimAt(kv.Key, double.PositiveInfinity))
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
        Assert.IsTrue(absentPeople.Count == 0,
            $"{absentPeople.Count} raid-side people legacy lists have no damage row at all in the derived board: "
            + string.Join(", ", absentPeople.Take(15)));
    }

    /*
     * "Where did THIS PERSON'S damage TAKEN go?" — the tanking half of the same question, and the one the boards census
     * raises but cannot answer: it compares two per-raider numbers and prints the delta, which does not say whether the
     * difference was skipped before it reached any row, routed to `Neither`, left on a hidden pet row, or lost in the
     * materialization. The sink below is the projection's own, so the targets it reports are the ones the product used.
     *
     *   EQLP_MIRROR_TANK=Ddread,Worthless EQLP_MIRROR_BOARDS=<log> dotnet test --filter Census_TankingResiduePerPerson
     */
    [TestMethod]
    public void Census_TankingResiduePerPerson()
    {
        var names = Environment.GetEnvironmentVariable("EQLP_MIRROR_TANK");
        if (string.IsNullOrEmpty(names)) Assert.Inconclusive("set EQLP_MIRROR_TANK=<name,name> (with EQLP_MIRROR_BOARDS=<log>) to run");

        var path = Resolve(Environment.GetEnvironmentVariable("EQLP_MIRROR_BOARDS"));
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Inconclusive("EQLP_MIRROR_BOARDS must name a log for the residue census");

        var wanted = new HashSet<string>(names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();

        var run = PipelineHarness.RunFileWithMirror(path);
        var facts = run.Facts;
        var timeline = Classified(run);

        /*
         * `EQLP_MIRROR_TANK=unplaced` asks about the whole bucket instead of named individuals: every name a confirmed
         * NPC hit while no rule had placed it. That is the set whose identity decides whether the walk's drop is right,
         * and picking names off the boards census would only ever ask about the biggest ones.
         */
        if (wanted.Remove("unplaced"))
        {
            for (var i = 0; i < facts.Facts.Length; i++)
            {
                var f = facts.Facts[i];
                var atk = facts.NameOf(f.AtkIdx);
                var def = facts.NameOf(f.DefIdx);
                if (atk is null || def is null) continue;

                if (FightProjection.SideAt(timeline, atk, f.TimeS) == FightProjection.Side.Npc
                    && FightProjection.SideAt(timeline, def, f.TimeS) == FightProjection.Side.Unknown
                    && !timeline.IsCharmedAt(atk, f.TimeS)) wanted.Add(def);
            }

            Console.WriteLine($"[tank] unplaced defenders of a mob: {wanted.Count} names");
        }

        /*
         * R7's own view of these names, recomputed after the fact. The graph rule is the one that should place a melee
         * raider with no joins and no chat (three opponent INSTANCES over 60 s, up to 2 % unclassified opposition, and
         * an absolute veto from the other side), so when a name that swings at the raid's enemies all night is still
         * Unknown the question is which gate refused it - and the three gates look identical from the outside. Asked
         * against the FINAL timeline: the only rule that runs after the graph is R18 (healed pets), so `unknown=` can
         * read a shade high for a name that turned out to be somebody's summon.
         */
        const double BurstGapS = 30;
        var npcTimes = new Dictionary<string, Dictionary<string, List<long>>>();
        var sideTimes = new Dictionary<string, Dictionary<string, List<long>>>();
        var unknownEdges = new Dictionary<string, int>();
        for (var i = 0; i < facts.Facts.Length; i++)
        {
            var f = facts.Facts[i];
            var atk = facts.NameOf(f.AtkIdx);
            if (atk is null || !wanted.Contains(atk)) continue;

            var def = facts.NameOf(f.DefIdx);
            var map = timeline.IdentityAt(def!, f.TimeS) switch
            {
                IdentityKind.Npc => npcTimes,
                IdentityKind.Unknown => null,
                _ => sideTimes,
            };
            if (map is null) { unknownEdges[atk] = unknownEdges.GetValueOrDefault(atk) + 1; continue; }

            if (!map.TryGetValue(atk, out var perDefender)) map[atk] = perDefender = new Dictionary<string, List<long>>();
            if (!perDefender.TryGetValue(def!, out var times)) perDefender[def] = times = [];
            times.Add((long)f.TimeS);
        }

        static int Instances(Dictionary<string, List<long>> byDefender)
        {
            var n = 0;
            foreach (var list in byDefender.Values)
            {
                n++;
                for (var i = 1; i < list.Count; i++) if (list[i] - list[i - 1] > BurstGapS) n++;
            }

            return n;
        }

        static double Span(Dictionary<string, List<long>> byDefender)
        {
            long min = long.MaxValue, max = long.MinValue;
            foreach (var list in byDefender.Values)
            {
                if (list.Count == 0) continue;
                min = Math.Min(min, list[0]); max = Math.Max(max, list[^1]);
            }

            return max > min ? max - min : 0;
        }

        // Watch every fact the projection files, through the projection's own sink, alongside the index that feeds
        // the board — same call, so what is counted here is what the grid gets.
        var index = new MirrorDamageIndex(timeline);
        var seen = new Dictionary<string, (long Count, long Sum)>();
        var byTarget = new Dictionary<string, (FightProjection.FactTarget Target, long Count, long Sum)>();
        var rowKeys = new Dictionary<string, Dictionary<string, long>>();
        void Sink(DamageFact fact, int ordinal, DerivedFight owner, FightProjection.FactTarget target)
        {
            index.OnFact(fact, ordinal, owner, target);
            var def = facts.NameOf(fact.DefIdx);
            if (def is null || !wanted.Contains(def)) return;

            var c = seen.GetValueOrDefault(def);
            seen[def] = (c.Count + 1, c.Sum + fact.Total);

            var k = def + "\0" + target;
            var e = byTarget.GetValueOrDefault(k);
            byTarget[k] = (target, e.Count + 1, e.Sum + fact.Total);

            if (!rowKeys.TryGetValue(def, out var rows2)) rowKeys[def] = rows2 = new Dictionary<string, long>();
            rows2[owner.Name] = rows2.GetValueOrDefault(owner.Name) + fact.Total;
        }

        var rows = FightProjection.Build(facts, timeline, Sink);
        Sectionizer.StampGroupIds(rows);
        var input = MirrorSummaryFights.Build(CharmPetRows.WithHiddenPets(rows, rows), index, facts);

        var legacyTanking = By(BuildTanking(run.Fights, WindowOf(run.Fights)));
        var derivedTanking = By(BuildTanking(input.Fights, input.AllRanges));

        foreach (var name in wanted)
        {
            /*
             * ONE pass over the capture per name. Both halves have to be read together: what landed on her (raw, and
             * grouped by the state the walk actually saw at that second) and what she herself swung at is what
             * separates "a raider nobody has evidence for yet" from "a mob that gets hit a lot". Grouping by the
             * per-second verdict rather than the final one is deliberate - Identity() answers with the end of the
             * capture, and a seed window that opens after the hits is exactly the thing under suspicion.
             */
            long rawCount = 0, rawSum = 0, outCount = 0, outSum = 0;
            double firstOut = double.PositiveInfinity, firstIn = double.PositiveInfinity, identitySince = double.NaN;
            var attackers = new Dictionary<string, long>();
            var states = new Dictionary<string, (long Count, long Sum)>();
            var outTargets = new Dictionary<string, long>();

            for (var i = 0; i < facts.Facts.Length; i++)
            {
                var f = facts.Facts[i];
                var atk = facts.NameOf(f.AtkIdx);
                var def = facts.NameOf(f.DefIdx);
                var hitsHer = string.Equals(def, name, StringComparison.OrdinalIgnoreCase);
                if (!hitsHer && !string.Equals(atk, name, StringComparison.OrdinalIgnoreCase)) continue;

                if (hitsHer)
                {
                    rawCount++; rawSum += f.Total;
                    if (f.TimeS < firstIn) firstIn = f.TimeS;
                    attackers[atk ?? "?"] = attackers.GetValueOrDefault(atk ?? "?") + f.Total;

                    var k = $"victim={timeline.IdentityAt(name, f.TimeS)}/charmed={timeline.IsCharmedAt(name, f.TimeS)} "
                            + $"attacker={timeline.IdentityAt(atk!, f.TimeS)}/attackerCharmed={timeline.IsCharmedAt(atk!, f.TimeS)}";
                    var e = states.GetValueOrDefault(k);
                    states[k] = (e.Count + 1, e.Sum + f.Total);
                }
                else
                {
                    outCount++; outSum += f.Total;
                    if (f.TimeS < firstOut) firstOut = f.TimeS;
                    if (double.IsNaN(identitySince) && timeline.IdentityAt(name, f.TimeS) is IdentityKind.Player or IdentityKind.Merc)
                        identitySince = f.TimeS;
                    if (timeline.IdentityAt(def!, f.TimeS) is IdentityKind.Npc) outTargets[def!] = outTargets.GetValueOrDefault(def!) + f.Total;
                }
            }

            var (seenCount, seenSum) = seen.GetValueOrDefault(name);
            legacyTanking.TryGetValue(name, out var lg);
            derivedTanking.TryGetValue(name, out var dg);
            Console.WriteLine($"[tank] === {name}: raw facts={rawCount:N0}/{rawSum:N0} reached a row={seenCount:N0}/{seenSum:N0} "
                              + $"(never reached one={rawCount - seenCount:N0}/{rawSum - seenSum:N0})  "
                              + $"board legacy={lg?.Total ?? 0:N0} derived={dg?.Total ?? 0:N0}");
            Console.WriteLine($"[tank] {name}: identity={timeline.IdentityWithSource(name, out var src)}{src} "
                              + $"isRaidVictim(late)={timeline.IsRaidVictimAt(name, double.PositiveInfinity)} "
                              + $"confirmedRaidPerson(late)={timeline.IsConfirmedRaidPersonAt(name, double.PositiveInfinity)} "
                              + $"ourPet(late)={timeline.IsOurPetAt(name, double.PositiveInfinity)}");
            Console.WriteLine($"[tank] {name}: outgoing {outCount:N0} facts/{outSum:N0}, first swing at {firstOut:F0}, first hit taken at {firstIn:F0}, "
                              + $"read raid-side from {(double.IsNaN(identitySince) ? "never" : identitySince.ToString("F0"))}, "
                              + $"npc targets={outTargets.Count} "
                              + $"({string.Join(", ", outTargets.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key} {kv.Value:N0}"))})");

            var npc = npcTimes.GetValueOrDefault(name);
            var ours = sideTimes.GetValueOrDefault(name);
            var unk = unknownEdges.GetValueOrDefault(name);
            var known = (npc?.Values.Sum(v => (long)v.Count) ?? 0) + (ours?.Values.Sum(v => (long)v.Count) ?? 0);
            Console.WriteLine($"[tank] {name}: R7 view npcInstances={Instances(npc ?? [])} npcSpan={Span(npc ?? []):F0}s "
                              + $"sawOurSide={(ours?.Count ?? 0) > 0} ourSideInstances={Instances(ours ?? [])} "
                              + $"unknownEdges={unk:N0}/{unk + known:N0} "
                              + $"({(unk + known == 0 ? 0 : (double)unk / (unk + known)) * 100:F2} %, gate 2 %, needs 3 instances over 60 s)");

            foreach (var (state, v) in states.OrderByDescending(kv => kv.Value.Sum))
                Console.WriteLine($"[tank] {name}: {state} count={v.Count:N0} sum={v.Sum:N0}");

            foreach (var (k, v) in byTarget.Where(kv => kv.Key.StartsWith(name + "\0", StringComparison.OrdinalIgnoreCase))
                                           .OrderByDescending(kv => kv.Value.Sum))
                Console.WriteLine($"[tank] {name}: target={v.Target} count={v.Count:N0} sum={v.Sum:N0}");

            if (rowKeys.TryGetValue(name, out var rk))
                foreach (var (rowName, sum) in rk.OrderByDescending(kv => kv.Value).Take(4))
                    Console.WriteLine($"[tank] {name}: filed on derived row '{rowName}' = {sum:N0}");

            foreach (var (atk, sum) in attackers.OrderByDescending(kv => kv.Value).Take(5))
                Console.WriteLine($"[tank] {name}: attacker '{atk}' {sum:N0} identity={timeline.Identity(atk)}");
        }
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

        var path = Resolve(Environment.GetEnvironmentVariable("EQLP_MIRROR_BOARDS"));
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Inconclusive("EQLP_MIRROR_BOARDS must name a log for the census to diagnose");

        var wanted = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();

        var run = PipelineHarness.RunFileWithMirror(path);
        var timeline = Classified(run);
        var index = new MirrorDamageIndex(timeline);
        var rows = FightProjection.Build(run.Facts, timeline, index.OnFact);
        Sectionizer.StampGroupIds(rows);
        var input = MirrorSummaryFights.Build(CharmPetRows.WithHiddenPets(rows, rows), index, run.Facts);

        foreach (var name in wanted)
        {
            Console.WriteLine($"[diag] === {name} :: verifiedPet={PlayerRegistry.Instance.IsVerifiedPet(name)} "
                              + $"ownerKnownFor={PlayerRegistry.Instance.GetPlayerFromPet(name)} "
                              + $"identity={timeline.Identity(name)} "
                              + $"({timeline.IdentityWithSource(name, out var source)}{source})");

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
                                  + $"taken={row.TankTotal:N0} charmOwnerAtStart={timeline.OwnerOf(row.Name, row.BeginTime)}");

            // Every name in the capture that shares this one's shape: pet suffixes, the +Pets grouping, spellings.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fact in run.Facts.Facts)
            {
                foreach (var candidate in new[] { run.Facts.NameOf(fact.AtkIdx), run.Facts.NameOf(fact.DefIdx) })
                {
                    if (candidate is null || !candidate.Contains(name, StringComparison.OrdinalIgnoreCase) || !seen.Add(candidate)) continue;

                    Console.WriteLine($"[diag] pooled name '{candidate}' identity={timeline.Identity(candidate)} "
                                      + $"ownerOf={timeline.OwnerOf(candidate, double.PositiveInfinity)} "
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

    /*
     * The per-consumer surfaces that moved to the mirror - the spell board (each fight's Dd/DoT/Proc dictionaries)
     * and the taunt board (each fight's TauntBlocks) - measured at the same altitude as the boards above: select
     * EVERY fight, compare per person, report rather than force. The engines legitimately differ about record
     * attribution (legacy drops a record it cannot place and folds pets only once its registry learned them; the
     * derived side reads the ownership word in the line), so the census fails only if it collapses to nothing on a
     * side that should have data, and prints what moved for a human to read. Same gate as the rest of this class:
     *   EQLP_MIRROR_BOARDS=<log> dotnet test --filter SpellTaunt_RealLog_Census --logger "console;verbosity=detailed"
     */
    [TestMethod]
    public void SpellTaunt_RealLog_Census()
    {
        var path = Resolve(Environment.GetEnvironmentVariable("EQLP_MIRROR_BOARDS"));
        if (string.IsNullOrEmpty(path)) Assert.Inconclusive("set EQLP_MIRROR_BOARDS=<path to log> to run");
        if (!File.Exists(path)) Assert.Fail($"EQLP_MIRROR_BOARDS points at nothing: {path}");

        // Only this log's records, or the census would include whatever other tests parsed.
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();

        var run = PipelineHarness.RunFileWithMirror(path);

        var timeline = Classified(run);
        var index = new MirrorDamageIndex(timeline);
        var rows = FightProjection.Build(run.Facts, timeline, index.OnFact);
        Sectionizer.StampGroupIds(rows);

        // The select-all shape with the hidden charm pets added back - the same materialization the boards above use.
        var input = MirrorSummaryFights.Build(CharmPetRows.WithHiddenPets(rows, rows), index, run.Facts);

        var legacySpells = SpellAgg(run.Fights);
        var derivedSpells = SpellAgg(input.Fights);
        Console.WriteLine($"[spell] file={Path.GetFileName(path)} legacy pairs={legacySpells.Count:N0} derived pairs={derivedSpells.Count:N0}");
        ReportDelta("spell", legacySpells, derivedSpells, "count/total/max");

        var legacyTaunts = TauntAgg(run.Fights);
        var derivedTaunts = TauntAgg(input.Fights);
        Console.WriteLine($"[taunt] file={Path.GetFileName(path)} legacy pairs={legacyTaunts.Count:N0} derived pairs={derivedTaunts.Count:N0}");
        ReportDelta("taunt", legacyTaunts, derivedTaunts, "taunts/failed/improved");

        // Both sides may legitimately be empty (a melee-only capture), but they must agree about it: the hazard this
        // census exists to catch is one side's spell data silently collapsing to nothing while the other still has it.
        Assert.IsTrue((legacySpells.Count == 0) == (derivedSpells.Count == 0),
            "the spell census is empty on exactly one side - one engine dropped every spell it should have counted");
    }

    /*
     * Legacy fills these dictionaries live per fight (FightManager's damage branch); the derived materialization
     * now fills the same three off the facts. Aggregated per (person, kind, spell) - the shape of a row on the
     * spell board - so an owner's own spells and her pets' never mix in this census.
     */
    private static Dictionary<string, (long A, long B, long C)> SpellAgg(IEnumerable<Fight> fights)
    {
        var agg = new Dictionary<string, (long A, long B, long C)>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in fights)
        {
            foreach (var kind in new[] { ("dd", f.DdDamage), ("dot", f.DoTDamage), ("proc", f.ProcDamage) })
            {
                foreach (var s in kind.Item2.Values)
                {
                    // The census keeps its numbers in long: a capture's whole evening of one spell fits with room
                    // to spare, and the delta print below wants a single signable type.
                    var count = (long)s.Count;
                    var total = (long)s.Total;
                    var max = (long)s.Max;
                    var key = PersonOf(s.Caster) + "|" + kind.Item1 + "|" + s.Spell;
                    if (agg.TryGetValue(key, out var v)) agg[key] = (v.A + count, v.B + total, Math.Max(v.C, max));
                    else agg[key] = (count, total, max);
                }
            }
        }

        return agg;
    }

    /*
     * The taunt board's own three words per (person, npc) - TauntStatsViewer's arithmetic, so the census counts
     * what the viewer would count. A line with no taunter name credits nobody: PersonOf("") is not a person.
     */
    private static Dictionary<string, (long A, long B, long C)> TauntAgg(IEnumerable<Fight> fights)
    {
        var agg = new Dictionary<string, (long A, long B, long C)>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in fights)
        {
            foreach (var block in f.TauntBlocks)
            {
                foreach (var action in block.Actions)
                {
                    if (action is not TauntRecord record) continue;
                    if (string.IsNullOrEmpty(record.Player)) continue;

                    var key = PersonOf(record.Player) + "|" + record.Npc;
                    if (agg.TryGetValue(key, out var v))
                    {
                        agg[key] = (v.A + (record.IsImproved ? 0 : record.Success ? 1 : 0),
                                    v.B + (record.IsImproved ? 0 : record.Success ? 0 : 1),
                                    v.C + (record.IsImproved ? 1 : 0));
                    }
                    else
                    {
                        agg[key] = (record.IsImproved ? 0 : record.Success ? 1 : 0,
                                    record.IsImproved ? 0 : record.Success ? 0 : 1,
                                    record.IsImproved ? 1 : 0);
                    }
                }
            }
        }

        return agg;
    }

    /*
     * The shared reading: how many (person, key) pairs both sides carry, how many of those moved, and which names
     * only one side has - the "folded or lost" question the boards above answer the same way. `columns` says what
     * A/B/C mean on this census; the movers are ordered by the middle column, the one a reader would check first.
     */
    private static void ReportDelta(string what, Dictionary<string, (long A, long B, long C)> legacy,
        Dictionary<string, (long A, long B, long C)> derived, string columns)
    {
        var moved = new List<(string Key, long L, long D)>();
        var shared = 0L;

        foreach (var kv in legacy)
        {
            if (!derived.TryGetValue(kv.Key, out var d)) continue;
            shared++;
            if (kv.Value.A != d.A || kv.Value.B != d.B || kv.Value.C != d.C) moved.Add((kv.Key, kv.Value.B, d.B));
        }

        var onlyLegacy = legacy.Where(kv => !derived.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
        var onlyDerived = derived.Where(kv => !legacy.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();

        Console.WriteLine($"[{what}] columns={columns} shared={shared:N0} moved={moved.Count:N0} legacyOnly={onlyLegacy.Count:N0} derivedOnly={onlyDerived.Count:N0}");
        foreach (var (key, l, d) in moved.OrderByDescending(m => Math.Abs(m.D - m.L)).Take(10))
        {
            Console.WriteLine($"[{what}]   {key}: legacy {l:N0} -> derived {d:N0}");
        }

        if (onlyLegacy.Count > 0) Console.WriteLine($"[{what}]   legacyOnly: {string.Join(", ", onlyLegacy.Take(10))}");
        if (onlyDerived.Count > 0) Console.WriteLine($"[{what}]   derivedOnly: {string.Join(", ", onlyDerived.Take(10))}");
    }
}
