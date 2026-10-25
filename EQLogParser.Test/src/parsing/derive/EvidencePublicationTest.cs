namespace EQLogParser;

[TestClass]
public class EvidencePublicationTest
{
  [TestMethod]
  public void ConcurrentSnapshotsContainOnlyCompletedEvidenceRowsThroughGrowthAndCompaction()
  {
    const int total = 100_000;
    var facts = new DamageFactTable();
    var name = facts.InternName("Caster");
    var aux = facts.InternAux("Spell");
    using var start = new ManualResetEventSlim();
    var writer = Task.Run(() =>
    {
      start.Wait();
      for (var i = 1; i <= total; i++)
      {
        facts.AddEvidence(new EvidenceFact(i, FixtureTime.BaseL + i, name, EvidenceFact.EvCast, aux));
        if (i % 4096 == 0) facts.CompactToCount();
      }
    });
    start.Set();
    var consumed = 0;
    try
    {
      while (!writer.IsCompleted || consumed < facts.EvidenceCount)
      {
        var evidence = facts.Evidence;
        for (var i = consumed; i < evidence.Length; i++)
        {
          var row = evidence[i];
          Assert.AreEqual(i + 1, row.Seq);
          Assert.AreEqual(FixtureTime.BaseL + i + 1, row.TimeS);
          Assert.AreEqual(name, row.NameIdx);
          Assert.AreEqual(EvidenceFact.EvCast, row.Kind);
          Assert.AreEqual(aux, row.AuxIdx);
        }
        consumed = evidence.Length;
        Thread.Yield();
      }
    }
    finally
    {
      writer.GetAwaiter().GetResult();
    }
    Assert.AreEqual(total, consumed);
  }

  [TestMethod]
  public void AReturnedEvidenceSpanKeepsItsBoundaryAndRowsAfterGrowthAndCompaction()
  {
    var facts = new DamageFactTable();
    facts.AddEvidence(new EvidenceFact(1, FixtureTime.BaseL, facts.InternName("Caster"), EvidenceFact.EvCast));
    var first = facts.Evidence;
    for (var i = 2; i <= 1025; i++)
      facts.AddEvidence(new EvidenceFact(i, FixtureTime.BaseL + i, 0, EvidenceFact.EvCast));
    facts.CompactToCount();
    Assert.AreEqual(1, first.Length);
    Assert.AreEqual(1, first[0].Seq);
    Assert.AreEqual(EvidenceFact.EvCast, first[0].Kind);
    Assert.AreEqual(1025, facts.Evidence.Length);
    Assert.AreEqual(1025, facts.Evidence[1024].Seq);
  }
}
