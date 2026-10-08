using System.Reflection;

using EQLogParser;

namespace EQLogParser;

/*
 * The three preconditions the cell cache (docs/incremental-summary-refresh.md, Phase B) stands on, made executable before any of it is built.
 * Each one was an assumption in the design; two of them turned out to have teeth.
 *
 *   1. FOLDING MUST NOT REACH SOMEBODY ELSE'S ROW. Child rows are minted per (owner list, key) at one funnel, so folding a copy can never
 *      mutate another owner's board. If that ever stops being true - one child instance shared by a player row and the `X +Pets` aggregate -
 *      a cached cell becomes a cross-row corruption that only shows on pet-heavy nights.
 *   2. FINALIZE MUST BE A PROJECTION, NOT AN ACCUMULATION. Rates are recomputed over reused cells; if re-deriving moved a counter, reuse
 *      would silently re-count the night.
 *   3. EXTREMA FOLD, THEY DO NOT REPLACE. BestSec/Max survive a fold by max, which is what lets a cell's best second be reused as-is.
 */
[TestClass]
public class CellFoldPreconditionsTest
{
  [TestMethod]
  public void ChildRowsAreMintedPerOwnerListSoFoldingCannotReachAnotherRow()
  {
    var frostmaw = new PlayerStats { Name = "Frostmaw" };
    var raid = new PlayerStats { Name = "Raid" };
    SubStatIndex frostIndex = new(), raidIndex = new();

    var mine = StatsUtil.SubStatLookup(ref frostIndex, frostmaw.SubStats, "Frostbite", Labels.Dot);
    Assert.AreSame(mine, StatsUtil.SubStatLookup(ref frostIndex, frostmaw.SubStats, "Frostbite", Labels.Dot),
      "one entry per key is what makes a child list a map");

    var theirs = StatsUtil.SubStatLookup(ref raidIndex, raid.SubStats, "Frostbite", Labels.Dot);
    Assert.AreNotSame(mine, theirs, "the same key under two owners must not hand out one instance: a fold would then mutate both boards");

    mine.Total = 500;
    Assert.AreEqual(0L, theirs.Total, "writing my sub-stat must not appear on another owner's row");
  }

  [TestMethod]
  public void RecalculatingRatesNeitherCountsAgainNorDrifts()
  {
    var raid = new PlayerStats { Name = "Raid", Total = 100_000, TotalSeconds = 100 };
    var stats = new PlayerStats
    {
      Name = "Frostmaw", Hits = 40, Total = 4_000, Extra = 250, CritHits = 9, SpellHits = 20, MeleeHits = 20, TotalSeconds = 40,
    };

    var before = Snapshot(stats);
    StatsUtil.CalculateRates(stats, raid, null);
    var afterFirst = Snapshot(stats);
    StatsUtil.CalculateRates(stats, raid, null);
    var afterSecond = Snapshot(stats);

    CollectionAssert.AreEqual(before, afterFirst, "finalize must not change a counter: it reads the accumulation and writes derived fields");
    CollectionAssert.AreEqual(afterFirst, afterSecond, "recomputing rates over an already-finalized row is a no-op (a reused cell gets re-finalized)");
    Assert.IsTrue(stats.Dps > 0 && stats.Potential == 4_250, "and it must actually have derived something: the test would pass on inert code otherwise");
  }

  [TestMethod]
  public void BestSecondSurvivesAFoldByMaxRatherThanByReplacement()
  {
    var target = new PlayerStats { Name = "Frostmaw", BestSec = 1_200, Max = 900 };
    var cell = new PlayerStats { Name = "Frostmaw", BestSec = 3_400, Max = 400 };

    StatsUtil.MergeStats(target, cell);

    Assert.AreEqual(3_400L, target.BestSec, "the night's best second is the best of the folded cells, not the last one folded in");
    Assert.AreEqual(900u, target.Max, "an extreme folds by max; a replacement would let a small cell erase a big hit");
  }

  private static List<string> Snapshot(PlayerStats stats)
    => typeof(Attempt).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && (p.PropertyType == typeof(uint) || p.PropertyType == typeof(long)))
        .Select(p => $"{p.Name}={p.GetValue(stats)}")
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();
}
