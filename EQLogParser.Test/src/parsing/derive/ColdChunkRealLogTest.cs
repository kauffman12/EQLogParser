using System.Runtime.InteropServices;

namespace EQLogParser;

// Volume assertion: a normal classification/projection/materialization must not permanently rehydrate the capture.
// EQLP_COLD_REALLOG=<absolute log path> dotnet test --filter ColdChunkRealLog --logger "console;verbosity=detailed"
[TestClass]
public class ColdChunkRealLogTest
{
  [TestMethod]
  public void EveryRowOfACaptureSurvivesBeingPutAway()
  {
    var path = Environment.GetEnvironmentVariable("EQLP_COLD_REALLOG");
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) Assert.Inconclusive("set EQLP_COLD_REALLOG to a real capture");
    var run = PipelineHarness.RunFileDerived(path);
    var facts = run.Facts;
    var heals = run.HealFacts;
    facts.ColdifyNow();
    heals.ColdifyNow();
    var before = Fingerprint(facts, heals);

    var index = new FightFactIndex(run.Timeline);
    IReadOnlyList<DerivedFight> rows;
    using (facts.RetainDecoded())
    using (heals.RetainDecoded())
    {
      ClassificationRules.Apply(facts, run.Timeline, heals);
      rows = FightProjection.Build(facts, run.Timeline, index.OnFact);
      Sectionizer.StampGroupIds(rows);
    }
    SummaryInput input;
    List<(double, HealRecord)> healRecords;
    using (facts.RetainDecoded())
    using (heals.RetainDecoded())
    {
      input = FightSummarySource.Build(rows, index, facts);
      healRecords = HealSummarySource.Materialize(heals, input.AllRanges);
    }

    Assert.AreEqual(before, Fingerprint(facts, heals), "every field survives all the normal read doors");
    Assert.IsTrue(input.Fights.Count > 0, "the measurement must build a real selection");
    Assert.IsTrue(facts.FactChunksAllocated <= ColdChunks<DamageFact>.WarmLimit + 1);
    Assert.IsTrue(heals.HealChunksAllocated <= ColdChunks<HealFact>.WarmLimit + 1);
    var rawDamage = (long)facts.FactCount * Marshal.SizeOf<DamageFact>();
    var rawHeal = (long)heals.HealCount * Marshal.SizeOf<HealFact>();
    // Tiny captures may not exceed the cache; the volume claim is only meaningful beyond it.
    /*
     * The claim is about ROWS, so it is measured against rows. EstimatedBytes is a bigger number ON PURPOSE: beside the blobs it carries the warm decode
     * cache (WarmLimit chunks plus the append tail, charged at full row price because those bytes really are resident) and the four un-chunked event queues -
     * deaths, identity events, taunts, and EVIDENCE, which takes one row per cast line. On eqlog_Incogitable_xegony.txt that queue alone held 352,673 rows in
     * a 524,288-slot array (16.0 MB of capacity, ~11 MB trimmed) against 50.8 MB of damage rows, so the whole estimate sat at 31.8 MB over a 25.4 MB half
     * while the FACTS themselves had gone 50.8 MB -> 6.7 MB, 7.6x. Asking the cold store to halve is the real guard; asking it to out-shrink a different
     * queue is a question about capture shape, and it is why this assert used to fail on a capture with no defect in it.
     */
    var residentRows = (long)facts.FactChunksAllocated * DamageFactTable.FactChunkSize * Marshal.SizeOf<DamageFact>();
    if (facts.FactCount > DamageFactTable.FactChunkSize * 16)
      Assert.IsTrue(residentRows + facts.ColdBlobBytes < rawDamage / 2, "cold chunks must retain less than half the rows they replaced");

    // And the estimate itself stays what it is for: a whole-capture resident figure feeding the D2 revisit trigger, never less than the rows it holds.
    Assert.IsTrue(facts.EstimatedBytes >= residentRows + facts.ColdBlobBytes,
      "the spill trigger's number has to include what is resident, not only the backing");
    if (heals.HealCount > HealFactTable.HealChunkSize * 16)
      Assert.IsTrue(heals.EstimatedBytes < rawHeal / 2);
    Assert.IsTrue(healRecords.Count <= heals.HealCount, "Materialization may not duplicate captured heals.");
  }

  private static (long Damage, long Heal) Fingerprint(DamageFactTable facts, HealFactTable heals)
  {
    var damage = 1L;
    var heal = 1L;
    unchecked
    {
      foreach (var slice in facts.FactsIn(0, facts.FactCount))
        foreach (ref readonly var row in slice)
        {
          damage = damage * 397 + row.Seq;
          damage = damage * 397 + row.TimeS;
          damage = damage * 397 + row.AtkIdx;
          damage = damage * 397 + row.DefIdx;
          damage = damage * 397 + row.Total;
          damage = damage * 397 + row.TypeId;
          damage = damage * 397 + row.Flags;
          damage = damage * 397 + row.ModMask;
          damage = damage * 397 + row.SubIdx;
        }
      foreach (var slice in heals.HealsIn(0, heals.HealCount))
        foreach (ref readonly var row in slice)
        {
          heal = heal * 397 + row.Seq;
          heal = heal * 397 + row.TimeS;
          heal = heal * 397 + row.HealerIdx;
          heal = heal * 397 + row.HealedIdx;
          heal = heal * 397 + row.Total;
          heal = heal * 397 + row.OverTotal;
          heal = heal * 397 + row.TypeId;
          heal = heal * 397 + row.Flags;
          heal = heal * 397 + row.ModMask;
          heal = heal * 397 + row.SubIdx;
        }
    }
    return (damage, heal);
  }
}
