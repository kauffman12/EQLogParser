using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable enable annotations

namespace EQLogParser
{
  /*
   * Cold chunk storage (A1 step 3a): a damage chunk that can never receive another row is encoded — transposed planes, delta on Seq and
   * Time, Deflate, measured 5.80x on a real capture — and its row array goes back to the GC. Reading one decodes it again (~3 ms per
   * chunk), so the rows a board walks come back byte for byte while an idle or monitoring session holds fractions of them.
   *
   * Four laws are held here, in this order because reading a chunk changes what is cold:
   *
   *   1. THE CODEC LOSES NOTHING. Nine fields, including the ones no grid prints (mod mask, subtype id, flags), and the shapes a fixture
   *      would never generate on purpose: uint.MaxValue damage, NoSubtype, a backwards second, ModMask −1, id 0 next to short.Max.
   *   2. ORDINALS SURVIVE. Cold storage is invisible below the addressing door — At / RefAt / FactsIn name the same row as before — because
   *      FightFactIndex and every board block stores ordinals into this table and nothing here may renumber them.
   *   3. THE WALK ACROSS A SEAM IS WHOLE. A slice run over cold chunks plus a live tail is the path every classification stage and the
   *      identity census take; a slice that spanned two chunks (it must not) or stopped at a boundary would drop rows silently.
   *   4. THE ESTIMATE TELLS THE TRUTH. EstimatedBytes feeds the spill trigger, so bytes returned to the GC have to show up as returned.
   */
  [TestClass]
  public class ColdChunkTest
  {
    private const int Chunk = DamageFactTable.FactChunkSize;

    private static DamageFact Row(int i)
    {
      // A deterministic mix that behaves like a real capture: seconds mostly holding still, ids clustering on a few names.
      var state = (i * 1103515245) + 12345;
      return new DamageFact(
        seq: i,
        timeS: FixtureTime.BaseL + (i / 7),
        atkIdx: (short)(state % 40),
        defIdx: (short)((state >> 5) % 90),
        // Amounts like a capture's, not like a random stream: most hits are four digits with a long tail, and the real
        // measurement behind this storage got its ratio from exactly that (docs/DesignNotes.md → "Rows go cold, and what a chunk costs to remember").
        total: (uint)((state >> 6) % 4096),
        typeId: LabelTypes.Melee,
        flags: (byte)((i % 5 == 0) ? DamageFact.FlagOwnerInLine : 0),
        modMask: (short)((i % 11 == 0) ? 0x1F : 0),
        subIdx: (ushort)(i % 4 == 0 ? DamageFactTable.NoSubtype : i % 32));
    }

    private static DamageFactTable Filled(int count, out DamageFact[] expected)
    {
      var table = new DamageFactTable(0);
      expected = new DamageFact[count];
      for (var i = 0; i < count; i++)
      {
        expected[i] = Row(i);
        table.AddFact(expected[i]);
      }
      return table;
    }

    private static void AssertSameRow(in DamageFact want, in DamageFact got, int ordinal)
    {
      Assert.AreEqual(want.Seq, got.Seq, $"ordinal {ordinal}: seq");
      Assert.AreEqual(want.TimeS, got.TimeS, $"ordinal {ordinal}: time");
      Assert.AreEqual(want.AtkIdx, got.AtkIdx, $"ordinal {ordinal}: attacker id");
      Assert.AreEqual(want.DefIdx, got.DefIdx, $"ordinal {ordinal}: defender id");
      Assert.AreEqual(want.Total, got.Total, $"ordinal {ordinal}: total");
      Assert.AreEqual(want.TypeId, got.TypeId, $"ordinal {ordinal}: type");
      Assert.AreEqual(want.Flags, got.Flags, $"ordinal {ordinal}: flags");
      Assert.AreEqual(want.ModMask, got.ModMask, $"ordinal {ordinal}: modifier mask");
      Assert.AreEqual(want.SubIdx, got.SubIdx, $"ordinal {ordinal}: subtype id");
    }

    [TestMethod]
    public void TheCodecCarriesEveryFieldAndTheShapesAFixtureWouldNeverPick()
    {
      var rows = new DamageFact[Chunk];
      for (var i = 0; i < Chunk; i++) rows[i] = Row(i);

      // The deliberately awkward ones, spread through the chunk so they are not all in the first varint run.
      rows[0] = new DamageFact(0, FixtureTime.BaseL, 0, (short)(short.MaxValue - 1), uint.MaxValue,
        LabelTypes.Dd, DamageFact.FlagAttackerIsSpell | DamageFact.FlagOwnerInLine, (short)-1, DamageFactTable.NoSubtype);
      rows[7] = new DamageFact(9, rows[0].TimeS - 4, -1, 1, 0, LabelTypes.Melee, 0, 0, 0);      // the clock went backwards
      rows[Chunk - 1] = new DamageFact(int.MaxValue - 2, FixtureTime.BaseL + 900_000_000, 1, 2, 1, LabelTypes.Bane, 2, 4, 3);

      var blob = DamageFactChunkCodec.Encode(rows, rows.Length);
      var back = DamageFactChunkCodec.Decode(blob, rows.Length);

      Assert.AreEqual(rows.Length, back.Length, "the decode owes one row per row encoded");
      for (var i = 0; i < rows.Length; i++) AssertSameRow(rows[i], back[i], i);
      Assert.IsTrue(blob.Length < (Chunk * 24) / 2, $"an encoding that saves less than half the row bytes is not worth its CPU (blob {blob.Length} of {Chunk * 24})");
    }

    [TestMethod]
    public void AChunkPutAwayStillAnswersEveryOrdinalThroughTheDoor()
    {
      var table = Filled((Chunk * 2) + 1000, out var expected);

      Assert.AreEqual(2, table.ColdifyNow(), "two chunks can no longer receive rows; the tail chunk still can");
      Assert.AreEqual(2, table.ColdChunkCount, "both completed chunks are held as blobs");

      for (var i = 0; i < expected.Length; i++) AssertSameRow(expected[i], table.At(i), i);

      // Reading them put them back: decode-on-read, not a blob that quietly outranks the truth.
      Assert.AreEqual(0, table.ColdChunkCount, "a chunk the capture read came back to rows and stopped costing its blob");
      for (var i = 0; i < expected.Length; i++) AssertSameRow(expected[i], table.At(i), i);
    }

    [TestMethod]
    public void ASliceWalkCrossesColdSeamsAndReadsEveryRowOnce()
    {
      var table = Filled((Chunk * 2) + 1000, out var expected);
      table.ColdifyNow();

      var seen = 0;
      foreach (var slice in table.FactsIn(Chunk - 5, table.FactCount))
        for (var i = 0; i < slice.Length; i++)
        {
          AssertSameRow(expected[(Chunk - 5) + seen], slice[i], (Chunk - 5) + seen);
          seen++;
        }

      Assert.AreEqual(expected.Length - (Chunk - 5), seen, "cold chunks and a live tail walk as one contiguous range");
    }

    [TestMethod]
    public void ColdRowsShowUpCheaperInTheNumberThatFeedsTheSpillTrigger()
    {
      var table = Filled(Chunk * 3, out _);
      var rawChunks = table.FactChunksAllocated;
      var before = table.EstimatedBytes;
      Assert.IsTrue(rawChunks >= 3, "three full chunks are live rows before anything is put away");

      table.ColdifyNow();

      var after = table.EstimatedBytes;
      Assert.IsTrue(table.ColdBlobBytes > 0, "the blobs are counted, not just the live chunks");
      Assert.IsTrue(after < before / 2, $"cold storage has to more than halve what the estimate claims ({before / 1048576.0:F1} MB -> {after / 1048576.0:F1} MB)");

      // Rows read back after a cold period count as rows again — the trigger must not keep believing they are small.
      for (var i = 0; i < table.FactCount; i++) _ = table.At(i);
      Assert.AreEqual(0, table.ColdChunkCount);
      Assert.IsTrue(table.EstimatedBytes > after, "a decoded chunk stops reporting its blob and starts reporting its rows");
    }

    [TestMethod]
    public void RowsAppendedAfterAHandoffAreNotEncodedIntoTheChunkTheyOpened()
    {
      var table = new DamageFactTable(0);
      for (var i = 0; i < (Chunk * 2) + 5; i++) table.AddFact(Row(i));

      // The tail chunk has five rows: putting away what can be put away must leave those five addressable and unmoved.
      table.ColdifyNow();
      Assert.AreEqual(2, table.ColdChunkCount);

      for (var i = Chunk * 2; i < (Chunk * 2) + 5; i++) AssertSameRow(Row(i), table.At(i), i);

      table.AddFact(Row((Chunk * 2) + 5));
      AssertSameRow(Row((Chunk * 2) + 5), table.At((Chunk * 2) + 5), (Chunk * 2) + 5);
      Assert.AreEqual((Chunk * 2) + 6, table.FactCount, "appending past a cold pair continues the same ordinal sequence");
    }
  }
}
