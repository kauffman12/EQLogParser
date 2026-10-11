using System.Diagnostics;
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
    var watch = Stopwatch.StartNew();
    var run = PipelineHarness.RunFileDerived(path);
    var facts = run.Facts;
    var heals = run.HealFacts;
    facts.ColdifyNow();
    heals.ColdifyNow();
    Console.WriteLine($"[cold] parse + encoder drain {watch.ElapsedMilliseconds} ms");
    var before = Fingerprint(facts, heals);

    long classifyMs, projectMs, materializeMs;
    var index = new FightFactIndex(run.Timeline);
    IReadOnlyList<DerivedFight> rows;
    using (facts.RetainDecoded())
    using (heals.RetainDecoded())
    {
      watch.Restart();
      ClassificationRules.Apply(facts, run.Timeline, heals);
      classifyMs = watch.ElapsedMilliseconds;
      watch.Restart();
      rows = FightProjection.Build(facts, run.Timeline, index.OnFact);
      Sectionizer.StampGroupIds(rows);
      projectMs = watch.ElapsedMilliseconds;
    }
    SummaryInput input;
    List<(double, HealRecord)> healRecords;
    using (facts.RetainDecoded())
    using (heals.RetainDecoded())
    {
      watch.Restart();
      input = FightSummarySource.Build(rows, index, facts);
      healRecords = HealSummarySource.Materialize(heals, input.AllRanges);
      materializeMs = watch.ElapsedMilliseconds;
    }

    Assert.AreEqual(before, Fingerprint(facts, heals), "every field survives all the normal read doors");
    Assert.IsTrue(input.Fights.Count > 0, "the measurement must build a real selection");
    Assert.IsTrue(facts.FactChunksAllocated <= ColdChunks<DamageFact>.WarmLimit + 1);
    Assert.IsTrue(heals.HealChunksAllocated <= ColdChunks<HealFact>.WarmLimit + 1);
    var rawDamage = (long)facts.FactCount * Marshal.SizeOf<DamageFact>();
    var rawHeal = (long)heals.HealCount * Marshal.SizeOf<HealFact>();
    // Tiny captures may not exceed the cache; the volume claim is only meaningful beyond it.
    if (facts.FactCount > DamageFactTable.FactChunkSize * 16)
      Assert.IsTrue(facts.EstimatedBytes < rawDamage / 2, "normal reads must retain the compression saving");
    if (heals.HealCount > HealFactTable.HealChunkSize * 16)
      Assert.IsTrue(heals.EstimatedBytes < rawHeal / 2);
    Console.WriteLine($"[cold] classify {classifyMs} ms | project {projectMs} ms | materialize {materializeMs} ms | " +
      $"{rows.Count} fights, {healRecords.Count} heals in selection");
    Console.WriteLine($"[cold] after normal reads: damage {rawDamage / 1048576d:F1} MB raw -> {facts.EstimatedBytes / 1048576d:F1} MB store " +
      $"({facts.ColdBlobBytes / 1048576d:F1} MB backing); heals {rawHeal / 1048576d:F1} -> {heals.EstimatedBytes / 1048576d:F1} MB " +
      $"({heals.ColdBlobBytes / 1048576d:F1} MB backing)");
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
