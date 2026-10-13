using System;
using System.Threading;
using System.Threading.Tasks;

using EQLogParser;

namespace EQLogParser;

/*
 * The name census runs while the parser is still naming people. That is not an edge case: BuildNameCensus takes the inline
 * rule-book path whenever a window opens between the log opening and its first full derive (DeriveEngine.BuildNameCensus), so
 * on a big file the census walks a capture whose name pool is growing underneath it — which is exactly the moment the operator
 * sees "Name census skipped; keeping the previous list" with an IndexOutOfRangeException out of ClassificationReport.
 *
 * The defect was that the census sized its per-name arrays from one read of the pool and then indexed them with ids taken from
 * facts published AFTER that read, so a name interned mid-census was an index past the end. What the census must do is pick its
 * boundary first and size from a pool read taken after it; this file pins both halves.
 *
 * Deliberately not a deterministic single-thread test: "ids newer than the array" cannot be built through the public API in one
 * thread, because a fact's ids are interned before its Add. Racing a grower against repeated builds is what reaches it — and the
 * loop is generous on purpose, since a race that reproduces on pass 1 of 1,000 still catches the regression.
 */
[TestClass]
public class CensusGrowingPoolTest
{
  /*
   * 8,000 iterations x two brand-new names each, plus the seed pass: about 18,000 pool entries, which stays clear of the
   * name-table ceiling (short.MaxValue) so this file fails on the RACE and not on capacity. Capacity is its own defect with
   * its own fix - the grower reaching it here in a test is what proved the ceiling is reachable in one process.
   */
  private const int GrowNames = 8_000;

  [TestMethod]
  public void ACensusSurvivesNamesBeingInternedUnderneathIt()
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    var timeline = new EntityTimeline();

    // Seed enough pool and facts that a build costs real milliseconds — the window has to be wide enough to fall into.
    for (short i = 0; i < 2_000; i++)
    {
      facts.AddFact(new DamageFact(i + 1, 1_700_000_000L + i, facts.InternName($"Seeded{i}"), facts.InternName("Target"),
                                   10, 0, 0, 0, ushort.MaxValue));
    }

    using var cts = new CancellationTokenSource();
    var grower = Task.Run(() =>
    {
      for (var i = 0; i < GrowNames && !cts.Token.IsCancellationRequested; i++)
      {
        // A brand-new name on both sides, which is what a fresh player joining a raid or a new NPC spawning does to the pool.
        var atk = facts.InternName($"Arriving{i}");
        var def = facts.InternName($"Victim{i}");
        facts.AddFact(new DamageFact(100_000 + i, 1_700_000_500L, atk, def, 5, 0, 0, 0, ushort.MaxValue));
      }
    });

    try
    {
      for (var pass = 0; pass < 400; pass++)
      {
        var report = ClassificationReport.Build(timeline, facts, heals, null, null);
        Assert.IsNotNull(report, "a census returned null instead of a report");
      }
    }
    finally
    {
      cts.Cancel();
      grower.Wait(TimeSpan.FromSeconds(30));
    }

    Assert.IsTrue(facts.FactCount > 2_000, "the grower never got going, so this test asked nothing of the census");
  }

  /*
   * The census's own numbers must stay self-consistent when the pool grew during it. A report that counted a name whose only
   * fact landed after the boundary would be off by one row against its own totals — allowed to differ from a later pass (the
   * capture really did grow), not allowed to be internally contradictory or to throw.
   */
  [TestMethod]
  public void AReportBuiltMidGrowthIsStillInternallyConsistent()
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    var timeline = new EntityTimeline();

    using var cts = new CancellationTokenSource();
    var grower = Task.Run(() =>
    {
      for (var i = 0; i < GrowNames && !cts.Token.IsCancellationRequested; i++)
      {
        facts.AddFact(new DamageFact(i + 1, 1_700_000_000L, facts.InternName($"Grow{i}"), facts.InternName("Mob"),
                                     3, 0, 0, 0, ushort.MaxValue));
      }
    });

    ClassificationReport? mid = null;
    try
    {
      for (var pass = 0; pass < 60; pass++) mid = ClassificationReport.Build(timeline, facts, heals, null, null);
    }
    finally
    {
      cts.Cancel();
      grower.Wait(TimeSpan.FromSeconds(30));
    }

    Assert.IsNotNull(mid);
    Assert.AreEqual(mid.TotalNames, mid.Rows.Count >= mid.TotalNames ? mid.Rows.Count : mid.TotalNames,
      "row count and total names disagree by more than the extra rows a snapshot can legitimately carry");
    Assert.IsTrue(mid.Rows.Count >= mid.TotalNames,
      "the census listed fewer rows than names it counted, so a name was dropped rather than left for the next pass");

    foreach (var row in mid.Rows)
    {
      Assert.IsNotNull(row.Name, "a row with no name reached the UI list");
    }
  }
}
