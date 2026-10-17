using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable enable annotations

namespace EQLogParser;

/*
 * The scratch pool behind cold-chunk encoding (FactChunkScratch), which exists because putting 82 chunks away allocated
 * **363 MB — about 7 % of everything a real parse allocates** while the blobs that survive were 12.4 MB. Reusing those buffers
 * buys that back for roughly ten parked MB, and the whole risk of such a pool is aliasing: a chunk already published being
 * rewritten by the next one, silently, and read back months later as somebody else's rows.
 *
 * Three laws are held here:
 *
 *   1. A PUBLISHED BLOB OUTLIVES ITS OWN SCRATCH. Encoding a second chunk reuses the same body buffer and the same sink; the first
 *      blob must still decode to exactly the rows it came from. This is THE regression test for pool aliasing.
 *   2. VERIFICATION STILL BITES. The round-trip proof moved into the codec so it could use a rented probe array; it must still
 *      refuse a blob that does not read back as the rows handed to it, or an encoding bug would reach a board instead of leaving
 *      the chunk warm and logged.
 *   3. THE POOL IS A SINGLE SLOT AND IT IS BOUNDED. Two rentals can never be the same array, a returned buffer is genuinely
 *      reused, and nothing larger than its stated budget is ever parked.
 */
[TestClass]
public class FactChunkScratchTest
{
  // The pool's own caps are a documented trade; this is the ceiling it may not exceed (body + rows + sink, per lane).
  private const int LaneBudgetBytes = 24 * 1024 * 1024;

  private static DamageFact DamageRow(int i, long total) =>
    new DamageFact(i, FixtureTime.BaseL + (i / 5), i % 37, (i >> 3) % 71, total, LabelTypes.Melee,
      0, (short)(i % 9 == 0 ? 0x1F : 0), (ushort)(i % 4 == 0 ? DamageFactTable.NoSubtype : i % 21));

  private static HealFact HealRow(int i, long total, long over) =>
    new HealFact(i, FixtureTime.BaseL + (i / 3), i % 29, (i >> 2) % 53, total, over, LabelTypes.Heal,
      0, LineModifiersParser.None, HealFact.NoSpell);

  private static DamageFact[] DamageRows(int count, long seed)
  {
    var rows = new DamageFact[count];
    for (var i = 0; i < count; i++) rows[i] = DamageRow(i, 1000 + seed + i);
    return rows;
  }

  private static HealFact[] HealRows(int count, long seed)
  {
    var rows = new HealFact[count];
    for (var i = 0; i < count; i++) rows[i] = HealRow(i, 2000 + seed + i, seed + i);
    return rows;
  }

  [TestMethod]
  public void APublishedDamageBlobOutlivesTheScratchThatMadeIt()
  {
    const int count = DamageFactTable.FactChunkSize; // a full chunk: the buffers are at their largest and reuse is certain
    var first = DamageRows(count, 0);
    var blobFirst = DamageFactChunkCodec.Encode(first, count);

    // Three more chunks through the same lane, so the body buffer and the Deflate sink this blob was made from are busy again.
    for (var round = 1; round <= 3; round++)
    {
      _ = DamageFactChunkCodec.Encode(DamageRows(count, round * 100_000), count);
    }

    var back = DamageFactChunkCodec.Decode(blobFirst, count);
    for (var i = 0; i < count; i++)
    {
      Assert.AreEqual(first[i].Seq, back[i].Seq, $"row {i}: seq");
      Assert.AreEqual(first[i].TimeS, back[i].TimeS, $"row {i}: time");
      Assert.AreEqual(first[i].AtkIdx, back[i].AtkIdx, $"row {i}: attacker");
      Assert.AreEqual(first[i].DefIdx, back[i].DefIdx, $"row {i}: defender");
      Assert.AreEqual(first[i].Total, back[i].Total, $"row {i}: total");
      Assert.AreEqual(first[i].ModMask, back[i].ModMask, $"row {i}: modifiers");
      Assert.AreEqual(first[i].SubIdx, back[i].SubIdx, $"row {i}: subtype");
    }

    // And the proof itself still agrees, after all that reuse.
    DamageFactChunkCodec.VerifyRoundTrip(blobFirst, first);
  }

  [TestMethod]
  public void APublishedHealBlobOutlivesTheScratchThatMadeIt()
  {
    const int count = HealFactTable.HealChunkSize;
    var first = HealRows(count, 0);
    var blobFirst = HealFactChunkCodec.Encode(first, count);

    for (var round = 1; round <= 3; round++)
    {
      _ = HealFactChunkCodec.Encode(HealRows(count, round * 100_000), count);
    }

    var back = HealFactChunkCodec.Decode(blobFirst, count);
    for (var i = 0; i < count; i++)
    {
      Assert.AreEqual(first[i].Seq, back[i].Seq, $"row {i}: seq");
      Assert.AreEqual(first[i].TimeS, back[i].TimeS, $"row {i}: time");
      Assert.AreEqual(first[i].HealerIdx, back[i].HealerIdx, $"row {i}: healer");
      Assert.AreEqual(first[i].HealedIdx, back[i].HealedIdx, $"row {i}: healed");
      Assert.AreEqual(first[i].Total, back[i].Total, $"row {i}: landed");
      Assert.AreEqual(first[i].OverTotal, back[i].OverTotal, $"row {i}: asked for");
    }

    HealFactChunkCodec.VerifyRoundTrip(blobFirst, first);
  }

  [TestMethod]
  public void VerificationRefusesABlobThatIsNotTheseRows()
  {
    const int count = DamageFactTable.FactChunkSize;
    var honest = DamageRows(count, 7);
    var other = DamageRows(count, 991_000);
    var blob = DamageFactChunkCodec.Encode(honest, count);

    // The same shape, different content: exactly what a silently wrong encoder would produce, and the reason the
    // comparison is not allowed to be dropped just because renting the probe array made it cheaper.
    var threw = false;
    try
    {
      DamageFactChunkCodec.VerifyRoundTrip(blob, other);
    }
    catch (InvalidOperationException)
    {
      threw = true;
    }

    Assert.IsTrue(threw, "a blob that does not read back as these rows must refuse to be put away");
  }

  [TestMethod]
  public void ThePoolIsOneSlotPerKindBoundedAndActuallyReused()
  {
    var rented = FactChunkScratch<DamageFact>.RentBody(4096);

    // Held: the slot is empty, so a second rental cannot be the same buffer.
    var second = FactChunkScratch<DamageFact>.RentBody(64);
    Assert.AreNotSame(rented, second, "one buffer cannot be in two calls at once");

    FactChunkScratch<DamageFact>.ReturnBody(rented);
    FactChunkScratch<DamageFact>.ReturnBody(second); // slot already filled: this one is dropped, not stacked

    var again = FactChunkScratch<DamageFact>.RentBody(64);
    Assert.IsTrue(ReferenceEquals(again, rented) || ReferenceEquals(again, second), "a parked buffer comes back to work");
    FactChunkScratch<DamageFact>.ReturnBody(again);

    // A freak request is served fresh and never parked.
    var freak = FactChunkScratch<HealFact>.RentBody(9 * 1024 * 1024);
    FactChunkScratch<HealFact>.ReturnBody(freak);
    Assert.AreNotSame(freak, FactChunkScratch<HealFact>.RentBody(64), "an over-budget buffer is not retained");

    // Encoding real chunks engages the pool on both lanes, and what it keeps stays inside the stated trade.
    _ = DamageFactChunkCodec.Encode(DamageRows(DamageFactTable.FactChunkSize, 5), DamageFactTable.FactChunkSize);
    _ = HealFactChunkCodec.Encode(HealRows(HealFactTable.HealChunkSize, 5), HealFactTable.HealChunkSize);

    var retained = FactChunkScratch<DamageFact>.RetainedBytes + FactChunkScratch<HealFact>.RetainedBytes;
    Assert.IsTrue(retained > 0, "the lane kept its scratch, which is the point of the pool");
    Assert.IsTrue(retained <= 2 * LaneBudgetBytes,
      $"scratch holds {retained / 1048576.0:F1} MB across both lanes; the accepted budget is two lanes at {LaneBudgetBytes / 1048576} MB");
  }

  [TestMethod]
  public void ADecodeRefusesCorruptBytesWhateverBufferItWritesInto()
  {
    var dest = new DamageFact[8];

    // A truncated / nonsense blob must refuse out of the rented-scratch path exactly as it did out of the MemoryStream one —
    // the guards read the POPULATED length, not the buffer's capacity.
    var refused = false;
    try
    {
      DamageFactChunkCodec.DecodeInto([0x00, 0x01, 0x02], 8, dest);
    }
    catch (Exception)
    {
      refused = true;
    }

    Assert.IsTrue(refused, "junk bytes must not decode into rows");

    Assert.ThrowsExactly<ArgumentOutOfRangeException>(
      () => DamageFactChunkCodec.DecodeInto(DamageFactChunkCodec.Encode(DamageRows(8, 3), 8), 9, dest));
  }
}
