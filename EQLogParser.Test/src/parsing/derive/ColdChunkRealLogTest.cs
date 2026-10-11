using System.Diagnostics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable enable annotations

namespace EQLogParser
{
  /*
   * Cold chunk storage over a capture no fixture can reach (EQLP_COLD_REALLOG=<log>): the fixtures in ColdChunkTest prove the codec and the
   * addressing on a few hundred thousand rows, but the thing that only real volume can show is whether every row of a night survives being
   * put away and read back — one damaged chunk would move a board's totals with no exception anywhere.
   *
   * It also prints the realized numbers behind the design (docs/DesignNotes.md → "Rows go cold, and what a chunk costs to remember"): blob bytes against raw row bytes, the
   * cost of putting a whole capture away, and what reading it all back costs a board that walks history. Encoding is handed to a pool worker
   * per chunk during the load; this run forces it synchronously so the measurement is complete rather than partial.
   *
   *   EQLP_COLD_REALLOG=local/logs/live/eqlog_Kizant_xegony-2.txt dotnet test --filter ColdChunkRealLog --logger "console;verbosity=detailed"
   */
  [TestClass]
  public class ColdChunkRealLogTest
  {
    [TestMethod]
    public void EveryRowOfACaptureSurvivesBeingPutAway()
    {
      var path = Environment.GetEnvironmentVariable("EQLP_COLD_REALLOG");
      if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) Assert.Inconclusive("set EQLP_COLD_REALLOG=local/logs/live/eqlog_….txt to run this");

      var run = PipelineHarness.RunFileDerived(path);
      var facts = run.Facts;
      var count = facts.FactCount;

      // The answer before the storage changed: read every row while all of them are plain rows.
      var before = new DamageFact[count];
      var i = 0;
      foreach (var slice in facts.FactsIn(0, count))
        for (var s = 0; s < slice.Length; s++) before[i++] = slice[s];
      Assert.AreEqual(count, i, "the pre-pass walked the whole capture");

      var rawBytes = facts.EstimatedBytes;
      var w = Stopwatch.StartNew();
      var chunks = facts.ColdifyNow();
      var encodeMs = w.ElapsedMilliseconds;

      var blobBytes = facts.ColdBlobBytes;
      Console.WriteLine($"[cold] {Path.GetFileName(path)}: {count:N0} rows, {chunks} chunks put away in {encodeMs} ms | " +
                        $"rows {rawBytes / 1048576.0:F1} MB -> store {(rawBytes - (long)chunks * DamageFactTable.FactChunkSize * 24 + blobBytes) / 1048576.0:F1} MB " +
                        $"(blobs {blobBytes / 1048576.0:F1} MB, ratio {rawBytes / (double)Math.Max(1L, blobBytes):F2}x on the cold part)");

      w.Restart();
      var seen = 0;
      foreach (var slice in facts.FactsIn(0, count))
        for (var s = 0; s < slice.Length; s++)
        {
          Assert.AreEqual(before[seen].Seq, slice[s].Seq, $"row {seen} came back with a different sequence");
          Assert.AreEqual(before[seen].Total, slice[s].Total, $"row {seen} came back with a different amount");
          Assert.AreEqual(before[seen].AtkIdx, slice[s].AtkIdx, $"row {seen} came back with a different attacker");
          Assert.AreEqual(before[seen].DefIdx, slice[s].DefIdx, $"row {seen} came back with a different defender");
          Assert.AreEqual(before[seen].TimeS, slice[s].TimeS, $"row {seen} came back at a different second");
          seen++;
        }
      var readBackMs = w.ElapsedMilliseconds;

      Assert.AreEqual(count, seen, "every row of the capture is still addressed by its ordinal");
      Assert.IsTrue(blobBytes < rawBytes / 2, $"cold storage has to be worth its CPU (blobs {blobBytes / 1048576.0:F1} MB of {rawBytes / 1048576.0:F1} MB)");
      Console.WriteLine($"[cold] read the whole capture back in {readBackMs} ms after coldify; store now {facts.EstimatedBytes / 1048576.0:F1} MB " +
                        $"({facts.ColdChunkCount} chunks still cold — reading returns rows)");
    }
  }
}
