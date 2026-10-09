using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * A chart event names the BUILD that produced the records it walks.
 *
 * This is the Core half of the fix for the most expensive door a user touches: the summary panes fire `"UPDATE"` at the charts on their OWN
 * interactions (selecting rows, switching a view option), and each fire re-aggregated every record of the open capture. Measured on a field
 * run over a whole night's select-all — two `DamageChart UPDATE` passes over 4,660,915 records drawing the identical answer (5 lines, 36,163
 * points), 1,589 ms and 1,107 ms, with NO stats build between them. `LineChart` now drops an UPDATE whose whole question — generation,
 * selection, group selection, view option, top count — is the one it already answered; that drop is only as trustworthy as this stamp.
 *
 * Two laws, and they point in opposite directions on purpose:
 *
 *   1. **A build always restamps.** The stamp is taken on entry to the build, before any early exit, so a rebuild can never be mistaken for
 *      "the data the chart already holds" — even a build that changes nothing produced a new number, and the chart walks again.
 *   2. **A fire between builds carries the SAME number.** That is what makes the second interaction skippable, and it is why the stamp lives on
 *      the builder rather than being generated per event: an event-local counter would make every UPDATE look new and the fix would do nothing
 *      while still looking implemented.
 *
 * The asymmetry is the safety: **unknown content (`-1`) is never reusable**, so any path that does not stamp through a build — including the
 * healing builder's full throw-away reset — falls back to walking. A missing stamp costs milliseconds, never a stale chart.
 */
[TestClass]
public class ChartDataGenerationTest
{
  [TestInitialize]
  public void Setup() => PlayerRegistry.Instance.Clear();

  [TestCleanup]
  public void Cleanup() => PlayerRegistry.Instance.Clear();

  private static List<(double Time, HealRecord Record)> Heals(int count)
  {
    var heals = new List<(double, HealRecord)>(count);
    for (var i = 0; i < count; i++)
    {
      heals.Add((1_000d + i % 60, new HealRecord
      {
        Healer = i % 2 == 0 ? "Reisil" : "Coas",
        Healed = i % 3 == 0 ? "Bryn" : "Akira",
        Type = Labels.Heal,
        SubType = "Superior Healing",
        Total = 500u + (uint)i,
      }));
    }

    return heals;
  }

  [TestMethod]
  public void AChartEventCarriesTheBuildThatWroteItsRecords()
  {
    var builder = HealingStatsBuilder.Instance;
    var all = new TimeRange(new TimeSegment(1_000, 1_060));
    var seen = new List<long>();

    void OnData(DataPointEvent e) => seen.Add(e.DataGeneration);

    builder.EventsUpdateDataPoint += OnData;
    try
    {
      builder.BuildTotalStats(new GenerateStatsOptions { AllRanges = all, Heals = Heals(40), Source = "generation test build one" });

      var firstBuild = builder.ChartDataGeneration;
      Assert.IsTrue(firstBuild > 0,
        "a completed build must leave a positive generation behind: an unstamped -1 is the chart's 'never reuse' signal, so if a build "
        + "cannot stamp itself the whole duplicate-update fix is silently dead");

      // A build announces its own result, so events already exist; all of them describe build one's content.
      Assert.IsTrue(seen.Count >= 1, "a completed build is expected to fire its own UPDATE (that is how a pane's chart gets filled)");
      foreach (var generation in seen)
      {
        Assert.AreEqual(firstBuild, generation,
          "every event must carry the generation of the build that wrote the records it hands over");
      }

      // The pane's own interaction with no build behind it: the same content, therefore the same stamp -- which is exactly what lets
      // LineChart drop the pass instead of aggregating 4.6 million records a second time.
      var beforeRepeat = seen.Count;
      builder.FireChartEvent("UPDATE");
      Assert.AreEqual(beforeRepeat + 1, seen.Count);
      Assert.AreEqual(firstBuild, seen[seen.Count - 1],
        "an UPDATE fired between builds has to look identical to the previous one - that equality is what the skip is decided on");

      // A rebuild means new content, whatever it did with it.
      builder.BuildTotalStats(new GenerateStatsOptions { AllRanges = all, Heals = Heals(40), Source = "generation test build two" });
      var secondBuild = builder.ChartDataGeneration;
      Assert.AreNotEqual(firstBuild, secondBuild,
        "a second build must restamp: reusing the previous number would let the chart skip a pass over data that changed");

      Assert.IsTrue(seen.Count > beforeRepeat + 1, "the second build should have announced itself too");
      builder.FireChartEvent("UPDATE");
      foreach (var generation in seen.GetRange(beforeRepeat + 1, seen.Count - beforeRepeat - 1))
      {
        Assert.AreEqual(secondBuild, generation, "events after a rebuild must all describe the new content's generation");
      }
    }
    finally
    {
      builder.EventsUpdateDataPoint -= OnData;
    }
  }

  [TestMethod]
  public void AnUnstampedEventIsNeverTiedToAnyContent()
  {
    // The default is the "walk it" signal: an event built outside a stamped build cannot claim to describe known content.
    Assert.AreEqual(-1, new DataPointEvent().DataGeneration,
      "an unstamped DataPointEvent must carry -1 - anything else could be mistaken for a generation the chart already drew");

    // Every build restamps, in order: three builds are three different answers to "which records is this?", and a repeat of any earlier
    // number would let a chart skip a pass over content it never aggregated.
    var builder = HealingStatsBuilder.Instance;
    var all = new TimeRange(new TimeSegment(2_000, 2_060));
    long previous = -1;
    for (var i = 0; i < 3; i++)
    {
      builder.BuildTotalStats(new GenerateStatsOptions { AllRanges = all, Heals = Heals(20), Source = $"generation test stamp {i}" });

      var now = builder.ChartDataGeneration;
      Assert.IsTrue(now > 0, $"build {i} left no generation behind (a -1 here means the chart may never reuse anything)");
      Assert.AreNotEqual(previous, now, $"build {i} reused an earlier build's generation");
      previous = now;
    }
  }
}
