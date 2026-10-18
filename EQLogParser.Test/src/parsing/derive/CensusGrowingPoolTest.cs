namespace EQLogParser;

[TestClass]
public class CensusGrowingPoolTest
{
  [TestMethod]
  public void ACensusSurvivesNamesBeingInternedUnderneathIt()
  {
    const int phases = 8;
    const int namesPerPhase = 1_000;
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    var timeline = new EntityTimeline();
    for (var i = 0; i < 2_000; i++)
    {
      facts.AddFact(new DamageFact(i + 1, FixtureTime.BaseL + i, facts.InternName($"Seeded{i}"),
        facts.InternName("Target"), 10, 0, 0, 0, ushort.MaxValue));
    }

    using var cancellation = new CancellationTokenSource();
    using var boundary = new Barrier(2);
    var grower = Task.Run(() =>
    {
      for (var phase = 0; phase < phases; phase++)
      {
        Meet(boundary, cancellation.Token);
        for (var i = phase * namesPerPhase; i < (phase + 1) * namesPerPhase; i++)
        {
          facts.AddFact(new DamageFact(100_000 + i, FixtureTime.BaseL + 500,
            facts.InternName($"Arriving{i}"), facts.InternName($"Victim{i}"), 5, 0, 0, 0, ushort.MaxValue));
        }
        Meet(boundary, cancellation.Token);
      }
    });

    try
    {
      // Each census races a fresh batch of names, rather than repeatedly rebuilding after the writer has finished.
      for (var phase = 0; phase < phases; phase++)
      {
        Meet(boundary, cancellation.Token);
        var report = ClassificationReport.Build(timeline, facts, heals, null, null);
        Assert.IsNotNull(report);
        Assert.AreEqual(report.TotalNames, report.Rows.Count, "A snapshot's own count and rows must agree.");
        foreach (var row in report.Rows) Assert.IsNotNull(row.Name);
        Meet(boundary, cancellation.Token);
      }
      grower.GetAwaiter().GetResult();
      Assert.AreEqual(2_000 + phases * namesPerPhase, facts.FactCount);
    }
    finally
    {
      cancellation.Cancel();
      try
      {
        grower.GetAwaiter().GetResult();
      }
      catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
      {
        // Release a writer waiting at the boundary if an assertion failed.
      }
    }
  }

  private static void Meet(Barrier boundary, CancellationToken cancellation)
  {
    Assert.IsTrue(boundary.SignalAndWait(TimeSpan.FromSeconds(30), cancellation), "The census race stalled.");
  }
}
