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

    /*
     * A NAME ID PAST 16 BITS. 1,048,583 sits well above the ~100k distinct names a long capture really reaches and far above
     * what any u16 could hold, and it rides in the awkward-shape fixture of BOTH codecs on purpose: if an id plane were ever
     * narrowed back to two bytes the value would truncate silently and the row would come home naming somebody else — the exact
     * failure the widening to int was done to prevent. The round-trip equality on this id is the regression test for that.
     */
    private const int WideNameId = 1_048_583;

    private static DamageFact Row(int i)
    {
      // A deterministic mix that behaves like a real capture: seconds mostly holding still, ids clustering on a few names.
      var state = (i * 1103515245) + 12345;
      return new DamageFact(
        seq: i,
        timeS: FixtureTime.BaseL + (i / 7),
        atkIdx: state % 40,
        defIdx: (state >> 5) % 90,
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
      rows[0] = new DamageFact(0, FixtureTime.BaseL, 0, WideNameId, uint.MaxValue,
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

      // Backing survives reads, so eviction never needs to encode the same immutable rows again.
      Assert.AreEqual(2, table.ColdChunkCount);
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
      var before = (long)table.FactCount * System.Runtime.InteropServices.Marshal.SizeOf<DamageFact>();

      table.ColdifyNow();

      var after = table.EstimatedBytes;
      Assert.IsTrue(table.ColdBlobBytes > 0, "the blobs are counted, not just the live chunks");
      Assert.IsTrue(after < before / 2, $"cold storage has to more than halve what the estimate claims ({before / 1048576.0:F1} MB -> {after / 1048576.0:F1} MB)");

      // Decoded arrays are counted alongside their backing, not mistaken for still-compressed memory.
      for (var i = 0; i < table.FactCount; i++) _ = table.At(i);
      Assert.AreEqual(2, table.ColdChunkCount);
      Assert.IsTrue(table.EstimatedBytes > after);
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
    private const int HealChunk = HealFactTable.HealChunkSize;

    /// <summary>The heal codec round-trips what this stream writes, including the shapes a fixture would not pick by accident: a no-paren line
    /// (OverTotal 0, which is NOT "asked for this"), an overheal, Hot, the owner-in-name flag, the "no text" mask, NoSpell beside a real spell id.</summary>
    [TestMethod]
    public void HealRowsRoundTripThroughTheCodecFieldForField()
    {
      var names = new DamageFactTable();
      var heals = new HealFactTable(names);
      var healer = names.InternName("Reisil");
      var healed = names.InternName("Bulgar`s pet");
      var spell = heals.InternSpell("Celestial Regeneration XI");

      var rows = new HealFact[HealChunk];
      for (var i = 0; i < rows.Length; i++)
        rows[i] = new HealFact(i + 1, FixtureTime.BaseL + (i / 4), healer, healed,
          total: (uint)(i * 7919), overTotal: (uint)(i * 9000),
          typeId: LabelTypes.Heal, flags: 0, modMask: 0, subIdx: spell);

      // The awkward shapes, spread out so they are not one varint run.
      rows[0] = new HealFact(0, FixtureTime.BaseL, 0, WideNameId, uint.MaxValue, 0u,
        LabelTypes.Hot, HealFact.FlagOwnerInLine, (short)-1, HealFact.NoSpell);                     // no spell, no paren, no text mask
      rows[7] = new HealFact(9, rows[0].TimeS - 3, healer, healed, 5, 9, LabelTypes.Heal, 0, 0, spell);   // the clock went backwards
      rows[HealChunk - 1] = new HealFact(int.MaxValue - 2, FixtureTime.BaseL + 900_000_000, 1, 2, 1, 2, LabelTypes.Hot, 1, 3, 0);

      var guards = (HealFact.UnknownTypeLabels, HealFact.MasksBeyondByte, HealFact.MaskSentinelCollisions);
      var blob = HealFactChunkCodec.Encode(rows, rows.Length);
      var back = HealFactChunkCodec.Decode(blob, rows.Length);

      Assert.AreEqual(guards,
        (HealFact.UnknownTypeLabels, HealFact.MasksBeyondByte, HealFact.MaskSentinelCollisions),
        "decode seeds must not fabricate a packing fault");
      Assert.AreEqual(rows.Length, back.Length, "the decode owes one row per row encoded");
      for (var i = 0; i < rows.Length; i++) AssertSameHeal(rows[i], back[i], i);
      Assert.IsTrue(blob.Length < (HealChunk * 24) / 2,
        $"heals transpose less well than damage, but cold storage still has to beat half the row bytes (blob {blob.Length} of {HealChunk * 24})");
    }

    private static void AssertSameHeal(in HealFact want, in HealFact got, int ordinal)
    {
      Assert.AreEqual(want.Seq, got.Seq, $"heal {ordinal}: seq");
      Assert.AreEqual(want.TimeS, got.TimeS, $"heal {ordinal}: time");
      Assert.AreEqual(want.HealerIdx, got.HealerIdx, $"heal {ordinal}: healer id");
      Assert.AreEqual(want.HealedIdx, got.HealedIdx, $"heal {ordinal}: healed id");
      Assert.AreEqual(want.Total, got.Total, $"heal {ordinal}: total");
      Assert.AreEqual(want.OverTotal, got.OverTotal,
        $"heal {ordinal}: OverTotal is the line's own word - 0 means 'no paren', so a codec that wrote AskedFor instead would move every overheal figure");
      Assert.AreEqual(want.TypeId, got.TypeId, $"heal {ordinal}: type");
      Assert.AreEqual(want.Flags, got.Flags, $"heal {ordinal}: flags");
      Assert.AreEqual(want.ModMask, got.ModMask, $"heal {ordinal}: modifier mask");
      Assert.AreEqual(want.SubIdx, got.SubIdx, $"heal {ordinal}: spell id");
    }

    private static HealFactTable HealsFilled(int count, out HealFact[] expected)
    {
      var names = new DamageFactTable();
      var healer = names.InternName("Reisil");
      var healed = names.InternName("Coas");
      var table = new HealFactTable(names);
      expected = new HealFact[count];
      for (var i = 0; i < count; i++)
      {
        expected[i] = new HealFact(i + 1, FixtureTime.BaseL + (i / 3), healer, healed,
          total: (uint)(i * 7919), overTotal: (uint)(i * 9000),
          typeId: (i % 6 == 0) ? LabelTypes.Hot : LabelTypes.Heal,
          flags: (byte)((i % 5 == 0) ? HealFact.FlagOwnerInLine : 0),
          modMask: (short)((i % 9 == 0) ? 0x0F : 0),
          subIdx: (ushort)(i % 3 == 0 ? HealFact.NoSpell : i % 20));
        table.AddHeal(expected[i]);
      }
      return table;
    }

    /// <summary>The heal handoff follows the damage rule: below the floor nothing goes cold, past it only the full chunks older than the newest
    /// two, and every heal ordinal — including the one a window walk finds by BINARY SEARCH — still names its own row.</summary>
    [TestMethod]
    public void HealChunksGoColdOnTheSameRuleAndStillAnswerTheirOrdinals()
    {
      // An appendable tail is never encoded, even when it is full.
      var small = HealsFilled(HealChunk, out _);
      Assert.AreEqual(0, small.ColdifyNow());
      Assert.AreEqual(0, small.ColdRowCount);

      // Past it: four chunks. The append itself cools what its successor opened; the probe takes the rest.
      var heals = HealsFilled(HealChunk * 4, out var expected);
      Assert.AreEqual(3, heals.ColdifyNow(), "settle joins the append worker; it never encodes a second copy");
      Assert.AreEqual(3, heals.ColdifyNow(), "a second barrier reports the same backing, not extra work");
      Assert.AreEqual(heals.ColdChunkCount, heals.ColdRowCount / HealChunk);
      Assert.IsTrue(heals.HealChunksAllocated <= 3, $"the warm set stays at the newest chunks plus one (has {heals.HealChunksAllocated})");

      for (var i = 0; i < expected.Length; i++) AssertSameHeal(expected[i], heals.At(i), i);
      Assert.AreEqual(3, heals.ColdChunkCount, "the backing survives a read so cache eviction remains safe");

      // The entry point a heal window walk really uses: a halving that lands in the middle of the capture, where rows live in blobs. Three rows
      // share a second in this fixture, so what is owed is the FIRST row at or after that second - and a cold row read as the zero row would show
      // up here as a start point far to the right.
      var probe = expected.Length / 2;
      var target = heals.At(probe).TimeS;
      var found = heals.FirstIndexAtOrAfter(target);
      Assert.IsTrue(found <= probe && heals.At(found).TimeS == target, $"cold halving landed at {found} for second {target}");
      Assert.IsTrue(found == 0 || heals.At(found - 1).TimeS < target, "the start point is the first row of its second, not one after it");
      Assert.AreEqual(expected[found].Total, heals.At(found).Total, "and that row is still its own heal, not a zero row");
    }

    /// <summary>A heal slice walk over cold chunks returns every row once, and the estimate that feeds the spill trigger reports the blobs.</summary>
    [TestMethod]
    public void HealSlicesCrossColdSeamsAndTheEstimateReportsTheBlobs()
    {
      var heals = HealsFilled(HealChunk * 4, out var expected);   // past the table's own floor, so anything may go cold

      var before = (long)expected.Length * System.Runtime.InteropServices.Marshal.SizeOf<HealFact>();   // what the same rows cost AS rows: measured from the struct, not a remembered number
      Assert.IsTrue(heals.ColdifyNow() > 0);
      Assert.IsTrue(heals.ColdBlobBytes > 0, "the blobs are counted, not just the live chunks");
      Assert.IsTrue(heals.EstimatedBytes < before / 2,
        $"cold heals have to more than halve the estimate against their own row bytes ({before / 1048576.0:F1} MB of rows -> {heals.EstimatedBytes / 1048576.0:F1} MB store)");

      var seen = 0;
      foreach (var slice in heals.HealsIn(HealChunk - 5, heals.HealCount))
        for (var i = 0; i < slice.Length; i++)
        {
          AssertSameHeal(heals.At((HealChunk - 5) + seen), slice[i], (HealChunk - 5) + seen);
          seen++;
        }
      Assert.AreEqual(heals.HealCount - (HealChunk - 5), seen, "cold chunks and a live tail walk as one contiguous range");
    }

  }
}
