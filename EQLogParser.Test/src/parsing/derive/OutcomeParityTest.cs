using EQLogParser;

namespace EQLogParser;

/*
 * A swing that makes no number is still a fact the grid needs.
 *
 * The tank board's first columns are counts of things that did NOT happen: `# Attempts`, `% Hit`, `Misses`,
 * `Blocks`, `Dodges`, `Parries`. Each one comes out of StatsUtil.UpdateDamageStats reading a record's LABEL, so
 * the mirror's job was never to count them — only to refuse to throw away the lines that carry them. Those lines
 * are the "X tries to <verb> Y, but ..." family, and the parser turns each into a record with total 0 and one of
 * seven labels (Block, Dodge, Miss, Parry, Riposte, Absorb, Invulnerable), so they ride the same
 * EventsDamageProcessed seam as damage and land in the fact table like any other hit.
 *
 * Two measured things this file holds:
 *
 *   - All seven outcome families reach the fact table, each with its own label, a total of 0 and the swing's verb
 *     kept as the subtype ("Bashes"), which is what HitLogViewer needs to reproduce a row.
 *   - The counters agree field for field between the two engines, on both boards, per raider — including the ones
 *     that live in the modifier mask rather than in a label (Crit/Flurry/Rampage/Strikethrough), because DamageFact
 *     carries that mask in the bytes OverTotal left behind.
 *
 * Asserted as absolute numbers, not just "derived == legacy": the same capture regression that zeroed one engine's
 * counters would zero both sides of an equality and pass it. Same reason `ALineCarriesEveryTankingField` checks a
 * row is 1344/5/5 before checking two engines agree about it.
 */
[TestClass]
public class OutcomeParityTest
{
    // The counter set the attempt columns bind, across both boards.
    private static readonly string[] CounterFields =
    [
        nameof(PlayerSubStats.Total), nameof(PlayerSubStats.Hits), nameof(PlayerSubStats.MeleeAttempts),
        nameof(PlayerSubStats.Misses), nameof(PlayerSubStats.Blocks), nameof(PlayerSubStats.Dodges),
        nameof(PlayerSubStats.Parries), nameof(PlayerSubStats.RiposteHits), nameof(PlayerSubStats.Absorbs),
        nameof(PlayerSubStats.Invulnerable), nameof(PlayerSubStats.CritHits), nameof(PlayerSubStats.FlurryHits),
        nameof(PlayerSubStats.RampageHits), nameof(PlayerSubStats.StrikethroughHits),
    ];

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", name);

    // One board out of one engine: the real builder, with every row of that side handed to it the way a
    // whole-table request would.
    private static CombinedStats? BuildDamage(IEnumerable<Fight> rows, TimeRange range)
    {
        var options = new GenerateStatsOptions { AllRanges = range };
        foreach (var row in rows) options.Npcs.Add(row);

        DamageStatsBuilder.Instance.BuildTotalStats(options);
        return DamageStatsBuilder.Instance.GetLastStats()?.CombinedStats;
    }

    // The tanking builder reports through its generation event instead of a last-stats getter, so the
    // subscription has to be open while it runs (and closed after, or the next test inherits this one's board).
    private static CombinedStats? BuildTanking(IEnumerable<Fight> rows, TimeRange range)
    {
        var options = new GenerateStatsOptions { AllRanges = range };
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

    private static TimeRange WholeLogRange(double lastTimeS)
    {
        var range = new TimeRange();
        range.Add(new TimeSegment(0, lastTimeS + 10));
        return range;
    }

    // Both engines' boards, keyed by raider.
    private sealed class Boards
    {
        public required Dictionary<string, PlayerStats> LegacyDamage { get; init; }
        public required Dictionary<string, PlayerStats> DerivedDamage { get; init; }
        public required Dictionary<string, PlayerStats> LegacyTanking { get; init; }
        public required Dictionary<string, PlayerStats> DerivedTanking { get; init; }

        // Optional because a fixture can produce no legacy fight row at all (a log with nothing to open an encounter
        // with), which the tests below assert on rather than assume away.
        public Fight? LegacyRow { get; init; }
        public Fight? DerivedRow { get; init; }
    }

    private static Boards RunBoards(string fixture)
    {
        var path = Fixture(fixture);
        Assert.IsTrue(File.Exists(path), $"missing fixture: {path} (copied by the test project's Content items)");

        var run = PipelineHarness.RunFileDerived(path);
        var lastTimeS = run.Facts.Facts.Length > 0 ? run.Facts.Facts[^1].TimeS : 0;
        var range = WholeLogRange(lastTimeS);

        // The same summary request the app makes when the derived table is switched on.
        var index = new FightFactIndex();
        var rows = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
        Sectionizer.StampGroupIds(rows);
        var summary = FightSummarySource.Build(rows, index, run.Facts);

        Dictionary<string, PlayerStats> By(CombinedStats? stats) =>
            (stats?.StatsList ?? []).ToDictionary(p => p.Name, p => p);

        return new Boards
        {
            LegacyDamage = By(BuildDamage(run.Fights, range)),
            DerivedDamage = By(BuildDamage(summary.Fights, range)),
            LegacyTanking = By(BuildTanking(run.Fights, range)),
            DerivedTanking = By(BuildTanking(summary.Fights, range)),
            LegacyRow = run.Fights.Count > 0 ? run.Fights[0] : null,
            DerivedRow = summary.Fights.Count > 0 ? summary.Fights[0] : null,
        };
    }

    [TestMethod]
    public void ASwingThatMakesNoNumberIsStillAFact()
    {
        var path = Fixture("attempt-fight.txt");
        Assert.IsTrue(File.Exists(path), $"missing fixture: {path}");

        var run = PipelineHarness.RunFileDerived(path);

        var byLabel = new Dictionary<string, int>();
        var subtypes = new List<string>();
        foreach (var fact in run.Facts.Facts)
        {
            var label = LabelTypes.LabelOf(fact.TypeId);
            byLabel[label] = byLabel.GetValueOrDefault(label) + 1;
            subtypes.Add(run.Facts.SubtypeOf(fact.SubIdx));

            // Everything that is not a swing with a number on it must hold zero, or the outcome lines would be
            // counted as damage too — which is what "don't count misses/dodges" means inside the builders.
            if (label is Labels.Block or Labels.Dodge or Labels.Miss or Labels.Parry
                     or Labels.Riposte or Labels.Absorb or Labels.Invulnerable)
                Assert.AreEqual(0UL, fact.Total, $"a {label} swing carried damage: {run.Facts.NameOf(fact.AtkIdx)}");
        }

        // The seven families, at the counts this fixture writes (some twice, some once, so a family that maps
        // onto another label shows up here as two of one and none of the other). A family that stopped being
        // captured is a column of the tank board going quiet for every raid in the game.
        Assert.AreEqual(2, byLabel.GetValueOrDefault(Labels.Block), "two blocks");
        Assert.AreEqual(2, byLabel.GetValueOrDefault(Labels.Dodge), "two dodges");
        Assert.AreEqual(2, byLabel.GetValueOrDefault(Labels.Parry), "two parries");
        Assert.AreEqual(1, byLabel.GetValueOrDefault(Labels.Miss), "one miss");
        Assert.AreEqual(1, byLabel.GetValueOrDefault(Labels.Riposte), "one riposte");
        Assert.AreEqual(1, byLabel.GetValueOrDefault(Labels.Absorb), "one absorb");
        Assert.AreEqual(1, byLabel.GetValueOrDefault(Labels.Invulnerable), "one INVULNERABLE");
        Assert.AreEqual(6, byLabel.GetValueOrDefault(Labels.Melee), "six swings with numbers on them");

        // The verb survives as the subtype, because a row a player points at has to be reproducible from its lines.
        CollectionAssert.Contains(subtypes, "Bashes");
    }

    [TestMethod]
    public void TheAttemptColumnsAgreePerRaiderBetweenEngines()
    {
        var boards = RunBoards("attempt-fight.txt");

        Assert.AreEqual(2, boards.LegacyDamage.Count, "two raiders on the damage board");
        Assert.AreEqual(2, boards.LegacyTanking.Count, "two raiders on the tanking board");
        CollectionAssert.AreEquivalent(boards.LegacyDamage.Keys, boards.DerivedDamage.Keys);
        CollectionAssert.AreEquivalent(boards.LegacyTanking.Keys, boards.DerivedTanking.Keys);

        // Absolute numbers first: Rune took four swings she did not land (miss, dodge, block) and one that did,
        // and took four swings herself; Kilsa was crit once and hit INVULNERABLE once. If the capture of any
        // outcome family died, these zeros stop being zeros on BOTH sides and this is what says so.
        AssertFields("Rune", boards.LegacyDamage["Rune"], new(450, 2, 5, 1, 1, 1, 0, 0, 0, 0, 0, 0, 1, 0));
        AssertFields("Kilsa", boards.LegacyDamage["Kilsa"], new(306, 2, 4, 0, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0));
        AssertFields("Rune", boards.LegacyTanking["Rune"], new(234, 1, 4, 0, 1, 0, 1, 0, 1, 0, 0, 0, 0, 0));
        AssertFields("Kilsa", boards.LegacyTanking["Kilsa"], new(512, 1, 3, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 0));

        // ... and the two engines agreeing on every one of them.
        foreach (var name in boards.LegacyDamage.Keys)
            AssertEqualFields($"damage board / {name}", boards.LegacyDamage[name], boards.DerivedDamage[name]);
        foreach (var name in boards.LegacyTanking.Keys)
            AssertEqualFields($"tanking board / {name}", boards.LegacyTanking[name], boards.DerivedTanking[name]);

        Assert.IsNotNull(boards.LegacyRow);
        Assert.IsNotNull(boards.DerivedRow);
        Assert.IsTrue(Math.Abs(boards.LegacyRow.BeginDamageTime - boards.DerivedRow.BeginDamageTime) <= 1.0,
            $"the row's window moved: legacy {boards.LegacyRow.BeginDamageTime} vs derived {boards.DerivedRow.BeginDamageTime}");
        Assert.IsTrue(Math.Abs(boards.LegacyRow.LastDamageTime - boards.DerivedRow.LastDamageTime) <= 1.0,
            "a zero-total swing extends the fight differently in one engine — DPS denominators would follow");
    }

    private sealed record Counters(long Total, long Hits, long Attempts, long Misses, long Blocks, long Dodges,
                                   long Parries, long Ripostes, long Absorbs, long Invulnerable, long Crits,
                                   long Flurries, long Rampages, long Strikethroughs);

    private static void AssertFields(string who, PlayerStats stats, Counters expected)
    {
        var actual = new Counters(stats.Total, stats.Hits, stats.MeleeAttempts, stats.Misses, stats.Blocks,
            stats.Dodges, stats.Parries, stats.RiposteHits, stats.Absorbs, stats.Invulnerable, stats.CritHits,
            stats.FlurryHits, stats.RampageHits, stats.StrikethroughHits);
        Assert.AreEqual(expected, actual, $"{who}: unexpected counters");
    }

    private static void AssertEqualFields(string what, PlayerStats legacy, PlayerStats derived)
    {
        // Counter fields are uint on Attempt, Total is long — Convert handles both without a cast per field.
        long Field(PlayerStats stats, string name) => Convert.ToInt64(typeof(PlayerSubStats).GetProperty(name)!.GetValue(stats)!);

        foreach (var field in CounterFields)
            Assert.AreEqual(Field(legacy, field), Field(derived, field),
                $"{what}: {field} differs between the legacy board and the derived one");
    }

    /*
     * A swing that lands nothing is still an engagement fact, and the grid's rows are cut by time: a raid that
     * misses for forty seconds is in a fight the whole time in both engines. If zero-total facts stopped opening or
     * extending a row on one side, the two tables would disagree about WHERE a player's numbers live even while
     * every counter above agreed — so the fight count itself is part of the contract.
     */
    [TestMethod]
    public void ZeroTotalSwingsCutTheSameFightList()
    {
        var path = Fixture("attempt-fight.txt");
        var run = PipelineHarness.RunFileDerived(path);

        Assert.AreEqual(1, run.Fights.Count, "legacy sees one fight");

        var index = new FightFactIndex();
        var rows = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
        Sectionizer.StampGroupIds(rows);
        var summary = FightSummarySource.Build(rows, index, run.Facts);

        Assert.AreEqual(1, rows.Count, "derived sees one fight too");

        var legacyRow = run.Fights[0];
        var derivedRow = summary.Fights[0];

        /*
         * The row-level hit counters, measured rather than derived from the line count: DamageHits passes facts
         * through LabelTypes.IsHit (mirroring FightManager's gate on StatsUtil.IsHitType), and BLOCK is a hit type
         * there — so the raid's one blocked swing counts in DamageHits while miss/dodge/parry/riposte/absorb/
         * INVULNERABLE do not: 4 swings with numbers + 1 block = 5. The tank branch has no such gate (FightManager
         * counts unconditionally), so all 7 swings the mob took at the raid are in TankHits, zero-total or not.
         */
        Assert.AreEqual(5u, legacyRow.DamageHits, "legacy: four numbered swings and a block");
        Assert.AreEqual(7u, legacyRow.TankHits, "legacy: every swing aimed at a raider");
        Assert.AreEqual(legacyRow.DamageHits, derivedRow.DamageHits, "# Hits (damage) differs between engines");
        Assert.AreEqual(legacyRow.TankHits, derivedRow.TankHits, "# Hits (tanking) differs between engines");

        // Zero-total swings must not move the sums either way.
        Assert.AreEqual(756L, (long)legacyRow.DamageTotal, "Rune 300 + 150 rampage, Kilsa 96 flurry + 210");
        Assert.AreEqual(746L, (long)legacyRow.TankTotal, "234 + a 512 backstab");
        Assert.AreEqual(legacyRow.DamageTotal, derivedRow.DamageTotal, "damage total differs between engines");
        Assert.AreEqual(legacyRow.TankTotal, derivedRow.TankTotal, "damage taken differs between engines");
    }
}
