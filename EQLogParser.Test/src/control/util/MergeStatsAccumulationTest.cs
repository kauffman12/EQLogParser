using System.Reflection;

using EQLogParser;

namespace EQLogParser;

/*
 * MergeStats is the one primitive that folds several sources into a single row: the damage summary's Group View uses it for
 * "aggregate these raid members", Damage Breakdown uses it for sub-stats, and the delta phase stands on it too. So its law is asserted
 * field by field rather than sampled: **folding two sources accumulates every counter**, with three fields excepted because they are
 * extrema, not sums (Max / MaxPotentialHit take the larger, Min the smaller).
 *
 * An assignment where an accumulation belongs was found exactly this way in spirit: `DoubleBowHits` was ASSIGNED, so a five-member group
 * printed a double-barb rate built from one member's double-bows over five members' bow swings - a number that always looks like a
 * percentage. Enumerating the counters is what makes the next smuggled assignment fail by name instead of looking plausible.
 */
[TestClass]
public class MergeStatsAccumulationTest
{
  private static readonly string[] Extrema = ["Max", "MaxPotentialHit", "Min"];

  [TestMethod]
  public void EveryCounterAccumulatesAndOnlyExtremaDoNot()
  {
    var counters = typeof(Attempt).GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Where(p => p.PropertyType == typeof(uint) && p.CanWrite)
      .ToList();

    // The sweep is only meaningful if it actually covers the vocabulary it claims to.
    Assert.IsTrue(counters.Count >= 25, $"expected the whole counter set, found {counters.Count}");

    var to = new PlayerStats { Name = "Frostmaw" };
    var from = new PlayerStats { Name = "Frostmaw" };
    foreach (var c in counters)
    {
      c.SetValue(to, 3u);
      c.SetValue(from, 4u);
    }

    StatsUtil.MergeStats(to, from);

    var wrong = new List<string>();
    foreach (var c in counters)
    {
      var got = (uint)(c.GetValue(to) ?? 0u);
      var expected = c.Name switch
      {
        "Max" => 4u,
        "MaxPotentialHit" => 4u,
        "Min" => 3u,
        _ => 7u,
      };

      if (got != expected)
      {
        wrong.Add($"{c.Name}: merged to {got}, expected {expected}");
      }
    }

    Assert.AreEqual(0, wrong.Count, "MergeStats must accumulate every counter except the extrema: " + string.Join("; ", wrong));
    CollectionAssert.AreEquivalent(Extrema, counters.Where(c => c.Name is "Max" or "MaxPotentialHit" or "Min").Select(c => c.Name).ToArray());
  }

  [TestMethod]
  public void DoubleBowHitsFromTwoRangersAreBothCounted()
  {
    // The shape the bug shipped in: two members, each with double-bows, aggregated into one group row.
    var group = new PlayerStats { Name = "Group 1" };
    var a = new PlayerStats { Name = "One", BowHits = 10, DoubleBowHits = 3, Total = 100 };
    var b = new PlayerStats { Name = "Two", BowHits = 20, DoubleBowHits = 4, Total = 200 };

    StatsUtil.MergeStats(group, a);
    StatsUtil.MergeStats(group, b);

    Assert.AreEqual(30u, group.BowHits);
    Assert.AreEqual(7u, group.DoubleBowHits, "the last member must not erase the ones before it");
  }
}
