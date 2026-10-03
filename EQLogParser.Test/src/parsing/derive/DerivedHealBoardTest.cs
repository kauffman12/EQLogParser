using EQLogParser;

namespace EQLogParser;

/*
 * The healing board, fed from the capture instead of the record store.
 *
 * HealingStatsBuilder is the one board that never reads a Fight: it pulls `RecordsStore.GetAllHeals()` (a
 * (time, record) pair per heal), windows that list against GenerateStatsOptions.AllRanges and groups by healer.
 * So a derived healing grid cannot be produced by materializing rows — it needs the records themselves, which is
 * what HealSummarySource builds from HealFactTable and GenerateStatsOptions.Heals carries. Capture fidelity is
 * already pinned elsewhere (HealFactCaptureTest: every stored heal is a fact and back); what this file holds is
 * the part downstream of that — the records a board actually sums, and the seam's rules.
 *
 * Measured on `heal-board.txt` (7 heals over two raiders: a plain heal, one that asked for more than it landed, a
 * HoT tick, a crit-modified heal, two heals inside one second, a self-heal): raid 3,401, Rune 2,776 across 5 heals
 * with 2,388 of overheal and a 4,012 max potential, Kilsa 625 across 2 with 450 overheal and 1,350 potential. Both
 * engines print exactly those numbers, which is the point — the two paths differ in where records come from and in
 * nothing a column reads. Asserted as absolute values first for the usual reason: an equality alone would also pass
 * on a seam that handed both sides an empty list.
 */
[TestClass]
[DoNotParallelize]
public class DerivedHealBoardTest
{
    /*
     * Same three process globals as HealFactCaptureTest, for the same reason: RecordsStore keeps every record any
     * test has ever parsed, so a second parse of this fixture inside the same run would double the board (3,401
     * becoming 6,802 is exactly what that looks like), and the heal RepeatStore decides instance sharing from
     * sighting history. The builder itself starts each build clean — it is the input that has to be.
     */
    [TestInitialize]
    public void Setup()
    {
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();
    }

    private const string Fixture = "heal-board.txt";

    private static string Path_(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", name);

    private static TimeRange Window(double lastTimeS)
    {
        var range = new TimeRange();
        range.Add(new TimeSegment(0, lastTimeS + 10));
        return range;
    }

    // One builder run, and only that builder's answer — GetLastStats is per-builder, so the two boards cannot be
    // read from one shared field.
    private static CombinedStats? BuildHeals(IReadOnlyList<Fight> rows, TimeRange range, List<(double, HealRecord)>? heals)
    {
        var options = new GenerateStatsOptions { AllRanges = range, MinSeconds = 0, Heals = heals };
        foreach (var row in rows) options.Npcs.Add(row);

        HealingStatsBuilder.Instance.BuildTotalStats(options);
        return HealingStatsBuilder.Instance.GetLastStats()?.CombinedStats;
    }

    [TestMethod]
    public void EveryMaterializedHealIsTheRecordTheParserMade()
    {
        var path = Path_(Fixture);
        Assert.IsTrue(File.Exists(path), $"missing fixture: {path} (copied by the test project's Content items)");

        var run = PipelineHarness.RunFileDerived(path);
        var stored = RecordsStore.Instance.GetAllHeals().ToList();
        var derived = HealSummarySource.Materialize(run.HealFacts, null);

        Assert.AreEqual(7, stored.Count, "the fixture writes seven heals");
        Assert.AreEqual(stored.Count, derived.Count, "one materialized record per stored heal");

        for (var i = 0; i < stored.Count; i++)
        {
            var (storedTime, storedRecord) = stored[i];
            var (derivedTime, derivedRecord) = derived[i];

            // Order too, not just content: the builder finds its window with FindIndex and walks forward, so a
            // reordered list loses part of a segment silently. Append order in the table is ingest order.
            Assert.AreEqual(storedTime, derivedTime, $"heal #{i} arrived at a different time");
            Assert.AreEqual(storedRecord.Healer, derivedRecord.Healer, $"heal #{i} healer");
            Assert.AreEqual(storedRecord.Healed, derivedRecord.Healed, $"heal #{i} target");
            Assert.AreEqual(storedRecord.Total, derivedRecord.Total, $"heal #{i} amount landed");
            Assert.AreEqual(storedRecord.Type, derivedRecord.Type, $"heal #{i} label");
            Assert.AreEqual(storedRecord.SubType, derivedRecord.SubType, $"heal #{i} spell");
            Assert.AreEqual(storedRecord.ModifiersMask, derivedRecord.ModifiersMask, $"heal #{i} modifier mask");

            // Verbatim, including the zero that means "the line never said": normalising OverTotal to what was
            // asked would double StatsUtil.UpdateHealStats' MaxPotentialHit on every plain heal line.
            Assert.AreEqual(storedRecord.OverTotal, derivedRecord.OverTotal, $"heal #{i} amount asked for");

            // A null subtype would be a missing row in the board's spell breakdown — the parser itself fills
            // Labels.SelfHeal when a line carries no spell text, so no real fact needs the fallback here.
            Assert.IsNotNull(derivedRecord.SubType, $"heal #{i} lost its spell word");
        }

        // The two meanings of the overheal column, in this fixture's own two lines.
        var asked = derived.First(h => h.Item2.OverTotal > 0).Item2;
        Assert.AreEqual(812u, asked.Total, "landed");
        Assert.AreEqual(3200u, asked.OverTotal, "asked for");
        Assert.AreEqual(0u, derived.First(h => h.Item2.Healer == "Kilsa" && h.Item2.SubType == "Healing Breeze Rk. XI").Item2.OverTotal,
            "a plain heal line writes no parenthesised amount, and that stays a zero");
    }

    [TestMethod]
    public void TheDerivedHealingBoardReadsLikeTheStoreBuiltOne()
    {
        var path = Path_(Fixture);
        var run = PipelineHarness.RunFileDerived(path);
        var range = Window(run.Facts.Facts.Length > 0 ? run.Facts.Facts[^1].TimeS : 0);

        var legacy = BuildHeals(run.Fights, range, null);
        var derived = BuildHeals(run.Fights, range, HealSummarySource.Materialize(run.HealFacts, range));

        Assert.IsNotNull(legacy);
        Assert.IsNotNull(derived);
        Assert.AreEqual(2, legacy.StatsList.Count, "two healers on the board");
        CollectionAssert.AreEquivalent(legacy.StatsList.Select(p => p.Name).ToList(), derived.StatsList.Select(p => p.Name).ToList());

        // Absolute first: what this fixture is worth, printed rather than implied.
        Assert.AreEqual(3401L, legacy.RaidStats.Total, "the raid's healing");
        var rune = legacy.StatsList.Single(p => p.Name == "Rune");
        Assert.AreEqual(2776L, rune.Total, "Rune healed 1204 + 812 + 300 + 250 + a 210 self-heal");
        Assert.AreEqual(5u, rune.Hits);
        Assert.AreEqual(2388L, rune.Extra, "the overheal of her 812-asked-for-3200 heal: 3200 - 812 = 2388");
        Assert.AreEqual(4012L, rune.MaxPotentialHit, "Total + OverTotal of that same heal");

        var kilsa = legacy.StatsList.Single(p => p.Name == "Kilsa");
        Assert.AreEqual(625L, kilsa.Total, "a 450 HoT tick and a 175 direct heal");
        Assert.AreEqual(2u, kilsa.Hits);
        Assert.AreEqual(450L, kilsa.Extra);

        // ... then the two engines on every column the healing grid binds.
        foreach (var name in legacy.StatsList.Select(p => p.Name))
        {
            var l = legacy.StatsList.Single(p => p.Name == name);
            var d = derived.StatsList.Single(p => p.Name == name);

            Assert.AreEqual(l.Total, d.Total, $"{name}: healed amount");
            Assert.AreEqual(l.Hits, d.Hits, $"{name}: heal count");
            Assert.AreEqual(l.SpellHits, d.SpellHits, $"{name}: spell heal count");
            Assert.AreEqual(l.Extra, d.Extra, $"{name}: overheal");
            Assert.AreEqual(l.Max, d.Max, $"{name}: biggest heal");
            Assert.AreEqual(l.Min, d.Min, $"{name}: smallest heal");
            Assert.AreEqual(l.MaxPotentialHit, d.MaxPotentialHit, $"{name}: biggest ask");
            Assert.AreEqual(l.TotalSeconds, d.TotalSeconds, 0.001, $"{name}: activity window the board divides by");
        }

        Assert.AreEqual(legacy.RaidStats.Total, derived.RaidStats.Total, "raid healing total");
        Assert.AreEqual(legacy.RaidStats.Hits, derived.RaidStats.Hits, "raid heal count");
    }

    /*
     * The seam's contract, which is the one place this could lie.
     *
     * `Heals == null` means "say nothing about healing" and the board reads the record store; a non-null list — even
     * an empty one — IS the input. Without that distinction a derived selection with no healing in it would keep
     * displaying whatever the previous (legacy) click left on the grid, which is the exact bug class this whole path
     * is about: two lists sharing one board and nobody knowing which one is on screen.
     */
    [TestMethod]
    public void AnEmptyHealListIsTheBoardBeingToldThereWasNoHealing()
    {
        var path = Path_(Fixture);
        var run = PipelineHarness.RunFileDerived(path);
        var range = Window(run.Facts.Facts.Length > 0 ? run.Facts.Facts[^1].TimeS : 0);

        Assert.AreEqual(3401L, BuildHeals(run.Fights, range, null)?.RaidStats.Total,
            "null keeps the record store, which this fixture fills");

        var cleared = BuildHeals(run.Fights, range, []);
        Assert.IsNotNull(cleared);
        Assert.AreEqual(0L, cleared.RaidStats.Total, "an empty list is an empty board, not last click's numbers");
        Assert.AreEqual(0, cleared.StatsList.Count);

        // And the same through the session-level shape: a selection of nothing carries an empty heal list rather
        // than null, so all three boards clear together.
        var nothing = FightSummarySource.Build([], new FightFactIndex(), run.Facts);
        Assert.IsNull(nothing.Heals, "FightSummarySource says nothing about healing; the session owns that");
    }
}
