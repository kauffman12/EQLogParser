using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * The hit-frequency histogram is built when the chart asks, not on every record.
 *
 * Measured on the operator's own capture (docs/DesignNotes.md → "What a chart pass is actually made of"), the eager version cost **327 ms (13 %)
 * of a whole-capture damage build** and **144 MB of the 202 MB that build allocated**, and left **1,879,130 dictionary entries (~72 MB) retained**
 * for one window (`HitFreqChart`) most sessions never open. A `Dictionary<long,int>` keyed by raw damage amount is not a histogram — hits are
 * near-unique numbers, so rows averaged ~758 keys and every damaging record paid a hash insert plus its share of the rehashes.
 *
 * Now the walk appends the amount (~2 ns instead of ~70) and `Attempt` turns the list into counts on first read — measured at **1.8 ms for a raid
 * member's rows** against a board build of 2.4 s, so nobody feels it — then drops the list. Three laws, in descending order of how much they cost
 * to get wrong:
 *
 *   1. **The answer is identical, count for count.** Bucketing, sampling or rounding would have been cheaper still and were refused: this chart's
 *      entire subject is individual hit amounts. Laziness may move WHEN the work happens, never WHAT it reports.
 *   2. **A materialized dictionary is assign-once.** `HitFreqChart` reads `.Keys` on one line and `[key]` on the next; a dictionary that could be
 *      swapped between those lines throws inside somebody's chart.
 *   3. **Nothing is materialized by a build.** That is the whole saving, and it is invisible from the outside — which is exactly why it needs an
 *      assertion: a future "simplification" that reads these dictionaries while building (a sort, an export, a debug dump) silently puts the
 *      327 ms and the 144 MB back for everyone, forever, while every chart still looks correct.
 *
 * Law 3 is asserted on a real build rather than on a hand-made `Attempt` because the build is where the temptation lives.
 */
[TestClass]
public class HitFrequencyLazinessTest
{
  private const string Fixture = "damage-board.txt";

  [TestInitialize]
  public void Setup()
  {
    DamageLineParser.ResetProcessState();
    HealingLineParser.ClearCaches();
    RecordsStore.Instance.Clear(false);
    PlayerRegistry.Instance.Clear();
  }

  [TestCleanup]
  public void Cleanup()
  {
    DamageLineParser.ResetProcessState();
    HealingLineParser.ClearCaches();
    RecordsStore.Instance.Clear(false);
    PlayerRegistry.Instance.Clear();
  }

  private static List<long> Amounts(params long[] values) => [.. values];

  /* Compared as text so a mismatch prints the amounts involved rather than two KeyValuePair enumerators. */
  private static string PairText(KeyValuePair<long, int> pair) => $"{pair.Key}={pair.Value}";

  [TestMethod]
  public void ARowsAmountsStayRawUntilSomethingAsksForTheHistogram()
  {
    var row = new Attempt();
    Assert.IsFalse(row.HoldsRawHitTotals);
    Assert.IsFalse(row.HitFreqMaterialized);

    row.RecordHitTotal(120, isCrit: false);
    row.RecordHitTotal(400, isCrit: true);

    Assert.IsTrue(row.HoldsRawHitTotals, "a damaging record must leave its amount behind");
    Assert.IsFalse(row.HitFreqMaterialized,
      "the point of the change is that a build counts nothing: two records cannot be allowed to mint two dictionaries");
  }

  [TestMethod]
  public void ReadingTheHistogramCountsExactlyWhatTheEagerWalkCounted()
  {
    // The eager code did `AddValue(dict, total, 1)` per record. This is that algorithm, written plainly, as the thing to match.
    var amounts = Amounts(120, 400, 120, 120, 77, 400, 9_999);
    var expected = new Dictionary<long, int>();
    foreach (var amount in amounts)
    {
      expected.TryGetValue(amount, out var had);
      expected[amount] = had + 1;
    }

    var row = new Attempt();
    foreach (var amount in amounts)
    {
      row.RecordHitTotal(amount, isCrit: false);
    }

    CollectionAssert.AreEqual(expected.Select(PairText).OrderBy(t => t).ToList(), row.NonCritFreqValues.Select(PairText).OrderBy(t => t).ToList(),
      "the lazy path must reproduce the eager dictionary count for count - this chart is about individual hit amounts, so rounding or bucketing is not an option");

    Assert.AreEqual(amounts.Count, row.NonCritFreqValues.Values.Sum(), "every damaging record is counted exactly once");
  }

  [TestMethod]
  public void ACritAmountNeverLandsInTheNonCritHistogram()
  {
    var row = new Attempt();
    row.RecordHitTotal(120, isCrit: false);
    row.RecordHitTotal(120, isCrit: true);

    CollectionAssert.AreEqual(new List<string> { "120=1" }, row.NonCritFreqValues.Select(PairText).ToList());
    CollectionAssert.AreEqual(new List<string> { "120=1" }, row.CritFreqValues.Select(PairText).ToList(),
      "the crit split is what the chart plots side by side; both buckets see the same amount here on purpose");
  }

  [TestMethod]
  public void AMaterializedHistogramIsNeverSwappedUnderAReader()
  {
    var row = new Attempt();
    row.RecordHitTotal(50, isCrit: false);

    var first = row.NonCritFreqValues;
    var second = row.NonCritFreqValues;

    Assert.AreSame(first, second,
      "HitFreqChart reads .Keys on one line and indexes with it on the next: a dictionary that could be rebuilt in between throws inside the chart");

    Assert.IsFalse(row.HoldsRawHitTotals,
      "the raw list is released when the counts exist - keeping both would cost more than the eager design this replaced");

    // A row nobody ever opened still answers, empty rather than null: the chart iterates without a null check.
    Assert.AreEqual(0, new Attempt().CritFreqValues.Count);
  }

  [TestMethod]
  public void ABoardBuildLeavesEveryRowUncounted()
  {
    var run = PipelineHarness.RunFileDerived(BoardGolden.FixturePath(Fixture));

    ClassificationRules.Apply(run.Facts, run.Timeline);
    var index = new FightFactIndex();
    var fights = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
    Sectionizer.StampGroupIds(fights);
    var input = FightSummarySource.Build(fights, index, run.Facts);

    var range = new TimeRange();
    foreach (var fight in input.Fights) range.Add(new TimeSegment(fight.BeginDamageTime, fight.LastDamageTime));

    CombinedStats? built = null;
    void OnStatus(StatsGenerationEvent e) => built = e.CombinedStats ?? built;

    var builder = DamageStatsBuilder.Instance;
    builder.EventsGenerationStatus += OnStatus;
    try
    {
      var options = new GenerateStatsOptions { Source = "hit frequency laziness test" };
      options.Npcs.AddRange(input.Fights);
      options.AllRanges = range;
      options.MinSeconds = 0;
      builder.BuildTotalStats(options);
    }
    finally
    {
      builder.EventsGenerationStatus -= OnStatus;
    }

    Assert.IsNotNull(built, "the build produced no board");

    var statRows = built.ExpandedStatsList ?? [];
    Assert.IsTrue(statRows.Count > 0, "the fixture must produce board rows");

    long rawAmounts = 0;
    var rowsWithRaw = 0;
    void Survey(Attempt row)
    {
      rawAmounts += row.RawHitTotals;
      if (row.HoldsRawHitTotals) rowsWithRaw++;

      // The build's own state, per row: damage happened, so amounts are held; nothing has counted them.
      Assert.IsFalse(row.HitFreqMaterialized,
        "a row was materialized during a board build - reading these dictionaries while building is exactly what costs 327 ms and 144 MB on a "
        + "night-sized capture, and it stays invisible because every chart still looks correct");
    }

    foreach (var row in statRows)
    {
      Survey(row);
      foreach (var sub in row.SubStats) Survey(sub);
    }

    Assert.IsTrue(rawAmounts > 0, "the fixture deals damage, so the board must be holding amounts to count later");
    Assert.IsTrue(rowsWithRaw > 0);
  }
}
