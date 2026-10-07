using EQLogParser;

namespace EQLogParser.Test.src.parsing.derive;

/*
 * The two heal surfaces that used to read a list of live records now read the capture's rows.
 *
 * RecordsStore no longer keeps HealRecord objects (Core → HealRecordSource: measured 766,713 of them / 29.25 MB held
 * for the length of a session, beside the same information already stored as 32-byte heal facts). Two consumers were
 * left standing on that list — HealingStatsBuilder's whole-capture fallback and the death log's twenty-second window —
 * and both now materialize through `HealSummarySource`, the code the derived healing board already uses. What this file
 * holds is what a "the two boards match" fixture cannot see: what each of the three inputs — a wired table, a window
 * outside it, and no session at all — is allowed to mean.
 *
 * The absolute figures are the ones `heal-board.txt` prints on both doors (raid 3,401; Rune 2,776 across 5 heals with
 * 2,388 overheal and a 4,012 max potential, Kilsa 625 across 2), asserted first because an equality between two paths
 * also passes when both hand the board an empty list.
 */
[TestClass]
[DoNotParallelize]
public class HealRecordSourceTest
{
  [TestInitialize]
  public void Setup()
  {
    HealingLineParser.ClearCaches();
    RecordsStore.Instance.Clear(false);
    PlayerRegistry.Instance.Clear();
    HealTap.Clear();
    HealRecordSource.Current = null;
  }

  [TestCleanup]
  public void Cleanup()
  {
    // A wired table is process state pointing at one capture's rows. Left standing it would let the next test's board
    // read this fixture's heals — the same hazard Dispose's still-mine guard exists for in production.
    HealRecordSource.Current = null;
    HealingLineParser.ClearCaches();
    RecordsStore.Instance.Clear(false);
    PlayerRegistry.Instance.Clear();
    HealTap.Clear();
  }

  private static string Path_(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", name);

  [TestMethod]
  public void TheBoardsFallbackReadsTheCapturedRows()
  {
    var path = Path_("heal-board.txt");
    Assert.IsTrue(File.Exists(path), $"missing fixture: {path} (copied by the test project's Content items)");

    var run = PipelineHarness.RunFileDerived(path);
    HealRecordSource.Current = run.HealFacts;   // what DeriveEngine.Start() wires in production

    // The null arm means "the whole capture", which is what HealingSummary's own rebuilds (and the store) used to mean.
    var all = HealRecordSource.All();
    Assert.AreEqual(run.HealFacts.HealCount, all.Count, "one record per captured heal");
    Assert.AreEqual(3401L, all.Sum(h => (long)h.Item2.Total), "the fixture's raid total, straight off the rows");

    var combined = BuildThroughTheFallback(Rows(run), heals: null, ClockEnd(all));
    Assert.IsNotNull(combined);
    Assert.AreEqual(3401L, combined.RaidStats.Total, "the fallback reads the rows exactly as it read the store's list");
    Assert.AreEqual(2776L, combined.StatsList.First(s => s.Name == "Rune").Total, "Rune across 5 heals");
    Assert.AreEqual(625L, combined.StatsList.First(s => s.Name == "Kilsa").Total, "Kilsa across 2");
  }

  [TestMethod]
  public void AWindowOutsideTheHealsIsEmptyRatherThanEverything()
  {
    var run = PipelineHarness.RunFileDerived(Path_("heal-board.txt"));
    HealRecordSource.Current = run.HealFacts;

    var all = HealRecordSource.All();
    Assert.IsTrue(all.Count > 0, "the fixture heals");

    var firstTime = all[0].Item1;
    var window = HealRecordSource.During(firstTime, firstTime + 1);
    Assert.IsTrue(window.Count >= 1, "a window opened on a heal's own second finds it — inclusive on both ends, like the builder's own test");

    var before = HealRecordSource.During(firstTime - 500, firstTime - 400);
    Assert.AreEqual(0, before.Count, "\"no heals in that window\" stays empty; a window must never widen to the capture");
  }

  [TestMethod]
  public void NoSessionSaysNothingAboutHealingRatherThanThrowing()
  {
    HealRecordSource.Current = null;

    Assert.AreEqual(0, HealRecordSource.All().Count, "a closed log has no heals to hand out");
    Assert.AreEqual(0, HealRecordSource.During(0, 1000).Count, "and no window into them either");

    // The builder's fallback is the read that would have met a null table; empty is what a cleared session said before.
    var combined = BuildThroughTheFallback([], heals: null, 100);
    Assert.IsNotNull(combined);
    Assert.AreEqual(0L, combined.RaidStats.Total, "no session is \"nothing was healed\", not stale numbers");
  }

  // The Fight rows the board takes as Npcs, materialized the way a click does — the healer/victim names the healing
  // validator accepts come from those rows, so leaving them out would test a board production never builds.
  private static IReadOnlyList<Fight> Rows(PipelineHarness.DeriveRunResult run)
  {
    ClassificationRules.Apply(run.Facts, run.Timeline);
    var index = new FightFactIndex();
    var rows = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
    Sectionizer.StampGroupIds(rows);
    return FightSummarySource.Build(rows, index, run.Facts).Fights;
  }

  // The capture's own clock, plus room: heal times are epoch seconds, so a window near zero would find nothing and
  // "the fallback reads the rows" would fail for a reason that has nothing to do with the seam.
  private static double ClockEnd(List<(double, HealRecord)> heals) => heals.Count > 0 ? heals.Max(h => h.Item1) + 10 : 100;

  // One builder run through the null arm only: options.Heals stays unset, which IS the thing under test.
  private static CombinedStats? BuildThroughTheFallback(IReadOnlyList<Fight> rows, List<(double, HealRecord)>? heals, double endTime)
  {
    var range = new TimeRange();
    range.Add(new TimeSegment(0, endTime));

    var options = new GenerateStatsOptions { AllRanges = range, MinSeconds = 0, Heals = heals };
    foreach (var row in rows) options.Npcs.Add(row);

    HealingStatsBuilder.Instance.BuildTotalStats(options);
    return HealingStatsBuilder.Instance.GetLastStats()?.CombinedStats;
  }
}
