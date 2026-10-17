using System.IO.Compression;

namespace EQLogParser;

[TestClass]
public class WideAmountCodecTest
{
  private const long T = 63_800_000_000;

  [TestMethod]
  public void VarintsRejectTruncationAndEveryOverflowingTenthByte()
  {
    foreach (var value in new[] { long.MinValue, -1L, 0L, 1L, long.MaxValue })
    {
      var buffer = new byte[10];
      var length = FactVarint.Write(buffer, 0, value);
      var p = 0;
      Assert.AreEqual(value, FactVarint.Read(buffer.AsSpan(0, length), ref p));
      Assert.AreEqual(length, p);
    }

    for (var last = 2; last <= byte.MaxValue; last++)
    {
      var bytes = Enumerable.Repeat((byte)0x80, 10).ToArray();
      bytes[^1] = (byte)last;
      var p = 0;
      Assert.ThrowsExactly<InvalidDataException>(() => FactVarint.Read(bytes, ref p));
    }

    for (var length = 0; length < 10; length++)
    {
      var bytes = Enumerable.Repeat((byte)0x80, length).ToArray();
      var p = 0;
      Assert.ThrowsExactly<InvalidDataException>(() => FactVarint.Read(bytes, ref p));
    }
  }

  [TestMethod]
  public void EveryHighPlaneCombinationPreservesAllFields()
  {
    foreach (var position in new[] { 0, 8, 16 })
    {
      for (var presence = 0; presence < 4; presence++)
      {
        var damage = Enumerable.Range(0, 17).Select(i => Damage(i, uint.MaxValue)).ToArray();
        var heals = Enumerable.Range(0, 17).Select(i => Heal(i, uint.MaxValue, uint.MaxValue)).ToArray();
        var total = (presence & 1) == 0 ? uint.MaxValue : TextUtils.MaxAmount;
        var asked = (presence & 2) == 0 ? uint.MaxValue : TextUtils.MaxAmount;
        damage[position] = Damage(position, total);
        heals[position] = Heal(position, total, asked);
        var d = DamageFactChunkCodec.Encode(damage, damage.Length);
        var h = HealFactChunkCodec.Encode(heals, heals.Length);
        Assert.AreEqual((byte)(presence & 1), Inflate(d)[0]);
        Assert.AreEqual((byte)presence, Inflate(h)[0]);
        CollectionAssert.AreEqual(damage, DamageFactChunkCodec.Decode(d, damage.Length));
        CollectionAssert.AreEqual(heals, HealFactChunkCodec.Decode(h, heals.Length));
      }
    }
  }

  [TestMethod]
  public void BothCodecsRejectUnknownBitsMissingPlanesTruncationAndTrailingBytes()
  {
    CheckCorruption(DamageFactChunkCodec.Encode([Damage(1, 12)], 1),
      b => DamageFactChunkCodec.Decode(b, 1), 1);
    CheckCorruption(HealFactChunkCodec.Encode([Heal(1, 12, 34)], 1),
      b => HealFactChunkCodec.Decode(b, 1), 3);
  }

  [TestMethod]
  public void WideHistorySurvivesEvictionNestedLeasesAndContinuedAppend()
  {
    var damage = new DamageFactTable(0);
    var heals = new HealFactTable(damage, 0);
    var damageCount = 6 * DamageFactTable.FactChunkSize;
    var healCount = 6 * HealFactTable.HealChunkSize;
    for (var i = 0; i < damageCount; i++) damage.AddFact(Damage(i, TextUtils.MaxAmount - i));
    for (var i = 0; i < healCount; i++) heals.AddHeal(Heal(i, TextUtils.MaxAmount - i, TextUtils.MaxAmount));
    damage.ColdifyNow();
    heals.ColdifyNow();

    using (damage.RetainDecoded())
    using (heals.RetainDecoded())
    {
      Assert.ThrowsExactly<InvalidOperationException>(() =>
      {
        using var nestedDamage = damage.RetainDecoded();
        using var nestedHeals = heals.RetainDecoded();
        var ordinal = 0;
        foreach (var slice in damage.FactsIn(0, damageCount))
          foreach (ref readonly var row in slice)
          {
            Assert.AreEqual(Damage(ordinal, TextUtils.MaxAmount - ordinal), row);
            ordinal++;
          }
        ordinal = 0;
        foreach (var slice in heals.HealsIn(0, healCount))
          foreach (ref readonly var row in slice)
          {
            Assert.AreEqual(Heal(ordinal, TextUtils.MaxAmount - ordinal, TextUtils.MaxAmount), row);
            ordinal++;
          }
        throw new InvalidOperationException("reader leaves through a failure");
      });
      Assert.IsTrue(damage.FactChunksAllocated > ColdChunks<DamageFact>.WarmLimit + 1);
      Assert.IsTrue(heals.HealChunksAllocated > ColdChunks<HealFact>.WarmLimit + 1);
    }

    Assert.IsTrue(damage.FactChunksAllocated <= ColdChunks<DamageFact>.WarmLimit + 1);
    Assert.IsTrue(heals.HealChunksAllocated <= ColdChunks<HealFact>.WarmLimit + 1);
    damage.AddFact(Damage(damageCount, TextUtils.MaxAmount));
    heals.AddHeal(Heal(healCount, TextUtils.MaxAmount, TextUtils.MaxAmount));
    damage.ColdifyNow();
    heals.ColdifyNow();
    Assert.AreEqual(Damage(0, TextUtils.MaxAmount), damage.RefAt(0));
    Assert.AreEqual(Heal(0, TextUtils.MaxAmount, TextUtils.MaxAmount), heals.RefAt(0));
    Assert.AreEqual(TextUtils.MaxAmount, damage.At(damageCount).Total);
    Assert.AreEqual(TextUtils.MaxAmount, heals.At(healCount).OverTotal);
  }

  private static DamageFact Damage(int i, long total) => new(i, T + i, 70_000, 90_000, total,
    LabelTypes.Dd, DamageFact.FlagOwnerInLine, LineModifiersParser.Crit, 1234);

  private static HealFact Heal(int i, long total, long asked) => new(i, T + i, 70_000, 90_000, total, asked,
    LabelTypes.Hot, HealFact.FlagOwnerInLine, LineModifiersParser.Crit, 1234);

  private static void CheckCorruption(byte[] blob, Action<byte[]> decode, byte knownBits)
  {
    var body = Inflate(blob);
    for (var bit = 1; bit <= 128; bit <<= 1)
    {
      if ((bit & knownBits) != 0) continue;
      var unknown = (byte[])body.Clone();
      unknown[0] |= (byte)bit;
      Assert.ThrowsExactly<InvalidDataException>(() => decode(Deflate(unknown)));
    }
    for (var bit = 1; bit <= knownBits; bit <<= 1)
    {
      var missing = (byte[])body.Clone();
      missing[0] |= (byte)bit;
      Assert.ThrowsExactly<InvalidDataException>(() => decode(Deflate(missing)));
    }
    for (var length = 0; length < body.Length; length++)
    {
      var shortBody = body[..length];
      Assert.ThrowsExactly<InvalidDataException>(() => decode(Deflate(shortBody)));
    }
    Assert.ThrowsExactly<InvalidDataException>(() => decode(Deflate([.. body, 0])));
  }

  private static byte[] Inflate(byte[] blob)
  {
    using var source = new MemoryStream(blob);
    using var decoder = new DeflateStream(source, CompressionMode.Decompress);
    using var output = new MemoryStream();
    decoder.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] Deflate(byte[] body)
  {
    using var output = new MemoryStream();
    using (var encoder = new DeflateStream(output, CompressionLevel.Fastest, true)) encoder.Write(body);
    return output.ToArray();
  }
}
