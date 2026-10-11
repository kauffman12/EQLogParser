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
    private const int HealChunk = HealFactTable.HealChunkSize;

    [TestMethod]
    public void EveryRowOfACaptureSurvivesBeingPutAway()
    {
      var path = Environment.GetEnvironmentVariable("EQLP_COLD_REALLOG");
      if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) Assert.Inconclusive("set EQLP_COLD_REALLOG=local/logs/live/eqlog_….txt to run this");

      var run = PipelineHarness.RunFileDerived(path);

      // What the load itself left behind, before this test reads anything: the handoff should already have cooled everything older than the newest
      // two chunks on both streams. If a number here is 0, the append seam that filled this capture skipped the handoff and production would too.
      Console.WriteLine($"[cold] after load, before reading: damage {run.Facts.ColdChunkCount * DamageFactTable.FactChunkSize:N0} rows cold ({run.Facts.ColdChunkCount} blobs), " +
                        $"heals {((HealFactTable)run.HealFacts).ColdRowCount:N0} rows cold ({((HealFactTable)run.HealFacts).ColdChunkCount} blobs)");
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
      Console.WriteLine($"[cold] read the whole capture back in {readBackMs} ms after coldify; store now {facts.EstimatedBytes / 1048576.0:F1} MB " +
                        $"({facts.ColdChunkCount} chunks still cold — reading returns rows)");

      // The heal stream, same probe on its own table: it is a third of the row bytes here and the healing board reads it through the same door.
      var heals = (HealFactTable)run.HealFacts;
      var healBlobAfterLoad = heals.ColdBlobBytes;
      var healCount = heals.HealCount;
      var healBefore = new HealFact[healCount];
      var h = 0;
      foreach (var slice in heals.HealsIn(0, healCount))
        for (var s2 = 0; s2 < slice.Length; s2++) healBefore[h++] = slice[s2];
      Assert.AreEqual(healCount, h, "the pre-pass walked every heal");

      var healRawRows = (long)healCount * 24;
      w.Restart();
      var healChunks = heals.ColdifyNow();
      var healEncodeMs = w.ElapsedMilliseconds;
      var healBlobBytes = heals.ColdBlobBytes;

      w.Restart();
      for (var k = 0; k < healCount; k++)
      {
        var got = heals.At(k);
        Assert.AreEqual(healBefore[k].Seq, got.Seq, $"heal {k} came back with a different sequence");
        Assert.AreEqual(healBefore[k].Total, got.Total, $"heal {k} came back with a different amount");
        Assert.AreEqual(healBefore[k].OverTotal, got.OverTotal, $"heal {k} came back with a different ask (overheal)");
        Assert.AreEqual(healBefore[k].HealerIdx, got.HealerIdx, $"heal {k} came back from a different healer");
      }
      var healReadMs = w.ElapsedMilliseconds;

      Console.WriteLine($"[cold/heal] blobs standing right after the load (before this probe read anything): {healBlobAfterLoad / 1048576.0:F1} MB");
      Console.WriteLine($"[cold/heal] {healCount:N0} heals, {healChunks} chunks put away on request in {healEncodeMs} ms | " +
                        $"rows {healRawRows / 1048576.0:F1} MB -> blobs {healBlobBytes / 1048576.0:F1} MB " +
                        $"(ratio {healRawRows / (double)Math.Max(1L, healBlobBytes):F2}x on the cold part); read back in {healReadMs} ms");
      // A chunk-by-chunk encode of every row, measured directly: the number the load's own blobs should match. If the standing blobs are much
      // smaller than this, some chunk went cold that was not full, and a reader would be getting zero rows out of it.
      var probeBlob = 0;
      for (var start = 0; start < healCount; start += HealChunk)
      {
        var n = Math.Min(HealChunk, healCount - start);
        var window = new HealFact[n];
        for (var q = 0; q < n; q++) window[q] = heals.At(start + q);
        probeBlob += HealFactChunkCodec.Encode(window, n).Length;
      }
      Console.WriteLine($"[cold/heal] fresh chunk-by-chunk encode of all {healCount:N0} rows: {probeBlob / 1048576.0:F1} MB " +
                        $"(ratio {healRawRows / (double)Math.Max(1, probeBlob):F2}x) vs {healBlobAfterLoad / 1048576.0:F1} MB standing after the load");

      var blobForRatio = Math.Max(healBlobBytes, healBlobAfterLoad);
      Assert.IsTrue(blobForRatio > 0 && blobForRatio < healRawRows / 2,
        $"cold heals have to be worth their CPU (blobs {blobForRatio / 1048576.0:F1} MB of {healRawRows / 1048576.0:F1} MB of rows)");
    }
  }
}
