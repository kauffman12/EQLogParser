using System.Reflection;

using EQLogParser;

namespace EQLogParser;

/*
 * The law Phase B's cell cache stands on: **folding counted partials must be indistinguishable from one uninterrupted count.** If it is
 * not, a refresh that re-counts only the new tail prints numbers that differ depending on how many times it ran - and that is a board nobody
 * can trust, not a faster board.
 *
 * Records are counted the way the builder counts them (the row AND the child keyed by its sub-type), the boundary step runs at the same
 * places on both sides, and the comparison sweeps every counter rather than eyeballing totals: the two `MergeStats` holes found earlier were
 * single fields, invisible in an aggregate.
 */
[TestClass]
public class RecursiveFoldEquivalenceTest
{
  private static DamageRecord Rec(string type, string subType, uint total)
    => new() { Type = type, SubType = subType, Total = total };

  [TestMethod]
  public void FoldingCountedPartialsMatchesOneUninterruptedCount()
  {
    var first = new[]
    {
      Rec(Labels.Melee, "Hits", 100), Rec(Labels.Melee, "Shoots", 250), Rec(Labels.Dd, "Frostbite", 400),
    };
    var second = new[]
    {
      Rec(Labels.Melee, "Hits", 500), Rec(Labels.Dd, "Frostbite", 900), Rec(Labels.Dot, "Toxic Bane", 700),
    };

    // Reference: one row, counted straight through, with the same frame boundary in the middle that a cell edge would be.
    var reference = new PlayerStats { Name = "Frostmaw" };
    Count(reference, first);
    CloseAll(reference);
    Count(reference, second);
    CloseAll(reference);

    // Folded: two partial rows, each closed at its own end, then folded into an empty row.
    var a = new PlayerStats { Name = "Frostmaw" };
    var b = new PlayerStats { Name = "Frostmaw" };
    Count(a, first);
    CloseAll(a);
    Count(b, second);
    CloseAll(b);

    var folded = new PlayerStats { Name = "Frostmaw" };
    StatsUtil.MergeStatsRecursive(folded, a);
    StatsUtil.MergeStatsRecursive(folded, b);

    CollectionAssert.AreEqual(Sweep(reference), Sweep(folded),
      "folded partials must match one uninterrupted count on every counter (best second included)");
    CollectionAssert.AreEqual(Children(reference), Children(folded),
      "and on the child rows: same keys, same totals - a folded row the grid expands differently is a different report");

    // Guards that the test would notice an empty fold rather than pass on one.
    Assert.IsTrue(reference.Total > 0 && reference.SubStats.Count == 4 && folded.SubStats.Count == 4,
      $"the fixture must really have counted (total {reference.Total}) into four child keys (reference {reference.SubStats.Count}, folded {folded.SubStats.Count}): "
      + $"Hits, Shoots, DD=Frostbite and DOT=Toxic Bane - the equality asserts would otherwise pass on two empty rows");
  }

  [TestMethod]
  public void FoldingAnUndrainedRowRefusesRatherThanLosingASecond()
  {
    var closed = new PlayerStats { Name = "Frostmaw" };
    Count(closed, [Rec(Labels.Melee, "Hits", 100)]);
    CloseAll(closed);

    var midSecond = new PlayerStats { Name = "Frostmaw" };
    Count(midSecond, [Rec(Labels.Melee, "Hits", 250)]);

    // Its running second is still in scratch: folding it now would either drop those 250 from BestSec or count them twice.
    Assert.AreNotEqual(0L, midSecond.BestSecTemp, "the fixture must actually hold an open second for this to test anything");
    Assert.Throws<InvalidOperationException>(() => StatsUtil.MergeStatsRecursive(closed, midSecond));

    Assert.AreEqual(100u, closed.Total, "and the refused fold changed nothing on the target");
  }

  // The builder's own shape: a record updates the row and the child keyed by its sub-type.
  private static void Count(PlayerStats row, DamageRecord[] records)
  {
    foreach (var record in records)
    {
      StatsUtil.UpdateDamageStats(row, record);
      StatsUtil.UpdateDamageStats(row.SubStatOf(record.SubType, record.Type), record);
    }
  }

  private static void CloseAll(PlayerStats row)
  {
    StatsUtil.CloseBestSecond(row);
    foreach (var child in row.SubStats)
    {
      StatsUtil.CloseBestSecond(child);
    }
  }

  // Every numeric field the row can hold - Attempt's counters AND the totals/rates declared further up - because the two bugs found in
  // MergeStats were single fields that an aggregate comparison would never notice.
  private static List<string> Sweep(PlayerStats stats)
    => typeof(PlayerStats).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && (p.PropertyType == typeof(uint) || p.PropertyType == typeof(long)))
        .GroupBy(p => p.Name, StringComparer.Ordinal)
        .Select(g => g.First())
        .Select(p => $"{p.Name}={p.GetValue(stats)}")
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();

  private static List<string> Children(PlayerStats stats)
    => [.. stats.SubStats.Select(c => $"{c.Type}|{c.Name}={c.Total}").OrderBy(s => s, StringComparer.Ordinal)];
}
