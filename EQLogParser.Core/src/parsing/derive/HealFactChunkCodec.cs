using System.Buffers.Binary;
using System.IO.Compression;

namespace EQLogParser;

/*
 * The cold-chunk encoding for heal rows (A1 step 3b) — the same shape the damage stream measured its way to (DamageFactChunkCodec carries
 * the candidate table: planes + delta beat row-major 5.80x to 3.58x, and Brotli's extra 16 % cost fifteen times the CPU while a raid is being
 * captured, so it stayed refused). Heals were measured separately at 4.36x row-major / 6.80x transposed, and their blob differs only in which
 * planes exist:
 *
 *   1. zigzag varint deltas of Seq            (unique per row across both streams, so entropy coding alone could not touch it)
 *   2. zigzag varint deltas of TimeS          (a heal second repeats far more often than a damage second, which is why this stream transposes well)
 *   3. healer id · healed id — zigzag varints beside Seq and TimeS, for the reason written in DamageFactChunkCodec
 *   4. total (u32) · overTotal (u32) · type (u8) · flags (u8) · modifier mask (u16) · spell/subtype id (u16)
 *
 * `OverTotal` is written as the row stores it, never as `AskedFor`: a heal line without its "(amount)" paren legitimately carries 0 there and
 * that zero is the difference between "asked for exactly this" and "asked for more" in every overheal figure on the healing board.
 *
 * Encode and decode go through the row's PUBLIC surface (`TimeS`, `TypeId`, `Flags`, `ModMask` and the constructor), so a cold round trip cannot
 * disagree with the label-bit and mask-sentinel normalisation the constructor performs — a fact built from a log line and the same fact read back
 * out of a blob take the same two paths and therefore agree.
 */
internal static class HealFactChunkCodec
{
  private const int FixedWidth = (4 * 2) + 1 + 1 + (2 * 2);   // total, overTotal, type, flags, mod, sub — the ids are varints

  internal static byte[] Encode(HealFact[] rows, int count)
  {
    var body = new byte[(count * ((4 * 10) + FixedWidth)) + 1];   // four varint fields worst case, then the fixed planes
    var p = 0;

    long prevSeq = 0, prevTime = 0;
    for (var i = 0; i < count; i++)
    {
      p = WriteVarint(body, p, rows[i].Seq - prevSeq);
      prevSeq = rows[i].Seq;
      p = WriteVarint(body, p, rows[i].TimeS - prevTime);
      prevTime = rows[i].TimeS;
      p = WriteVarint(body, p, rows[i].HealerIdx);   // zigzag varints, same reason as DamageFactChunkCodec: a widened id plane cost more than the ids are worth
      p = WriteVarint(body, p, rows[i].HealedIdx);
    }

    for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p + (i * 4), 4), rows[i].Total);
    p += count * 4;
    for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p + (i * 4), 4), rows[i].OverTotal);
    p += count * 4;
    for (var i = 0; i < count; i++) body[p + i] = rows[i].TypeId;
    p += count;
    for (var i = 0; i < count; i++) body[p + i] = rows[i].Flags;
    p += count;
    for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p + (i * 2), 2), (ushort)rows[i].ModMask);
    p += count * 2;
    for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p + (i * 2), 2), rows[i].SubIdx);
    p += count * 2;

    using var ms = new MemoryStream(Math.Max(1024, p / 4));
    using (var d = new DeflateStream(ms, CompressionLevel.SmallestSize, true)) d.Write(body, 0, p);
    return ms.ToArray();
  }

  internal static HealFact[] Decode(byte[] blob, int count)
  {
    using var src = new MemoryStream(blob);
    using var d = new DeflateStream(src, CompressionMode.Decompress);
    using var ms = new MemoryStream(count * (FixedWidth + 10));
    d.CopyTo(ms);
    var body = ms.GetBuffer();

    var seqs = new long[count];
    var times = new long[count];
    var healerIds = new int[count];   // read with the varints, applied when the rows are assembled below
    var healedIds = new int[count];
    var p = 0;
    long seq = 0, time = 0;

    // Reading the varints is what locates the fixed planes — no offset table that could drift from the encoder.
    for (var i = 0; i < count; i++)
    {
      seq += ReadVarint(body, ref p);
      time += ReadVarint(body, ref p);
      seqs[i] = seq;
      times[i] = time;
      healerIds[i] = (int)ReadVarint(body, ref p);
      healedIds[i] = (int)ReadVarint(body, ref p);
    }

    var total = p;
    var overTotal = total + (count * 4);
    var type = overTotal + (count * 4);
    var flags = type + count;
    var mod = flags + count;
    var sub = mod + (count * 2);

    var rows = new HealFact[count];
    for (var i = 0; i < count; i++)
      rows[i] = new HealFact((int)seqs[i], times[i],
        healerIds[i],
        healedIds[i],
        BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(total + (i * 4), 4)),
        BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(overTotal + (i * 4), 4)),
        body[type + i],
        body[flags + i],
        (short)BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(mod + (i * 2), 2)),
        BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(sub + (i * 2), 2)));

    return rows;
  }

  private static int WriteVarint(byte[] buf, int p, long value)
  {
    var u = (ulong)((value << 1) ^ (value >> 63));      // zigzag: a backwards second costs the same bytes as a forwards one
    while (u >= 0x80)
    {
      buf[p++] = (byte)(u | 0x80);
      u >>= 7;
    }
    buf[p++] = (byte)u;
    return p;
  }

  private static long ReadVarint(byte[] buf, ref int p)
  {
    ulong u = 0;
    var shift = 0;
    byte b;
    do
    {
      b = buf[p++];
      u |= (ulong)(b & 0x7F) << shift;
      shift += 7;
    }
    while ((b & 0x80) != 0);
    return (long)((u >> 1) ^ (~(u & 1) + 1));
  }
}
