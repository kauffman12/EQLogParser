using System.Buffers.Binary;
using System.IO.Compression;

namespace EQLogParser;

/*
 * The cold-chunk encoding for damage rows: transposed planes with zigzag-varint deltas on the monotonic columns, then Deflate. Chosen by
 * measurement over a 2,286,368-row chunk set — row-major deflate 14.62 MB / planes+delta varint 9.02 MB (5.80x), and Brotli's extra 16 % costs
 * fifteen times the encode time, which is refused because encoding runs while a raid is being captured. Decode is ~3 ms per chunk, which is what
 * makes decode-on-read affordable.
 *
 * Layout, compressed as one Deflate stream: zigzag varint deltas of Seq, then TimeS (epochs rebased through FactTime on the way back in), then
 * attacker and defender ids as varints, then total u32 · type u8 · flags u8 · modifier mask u16 · subtype id u16.
 *
 * Ids are varints, not an i32 plane: fixed 4-byte planes made a cold row 30 bytes instead of 26 and failed the cold-store halving guard on
 * healing, while a varint costs 1-3 bytes and spends one byte on the -1 sentinel. Fields go through the row's public surface so the codec cannot
 * disagree with FactTime about a stored second.
 */
internal static class DamageFactChunkCodec
{
  private const int FixedWidth = 4 + 1 + 1 + (2 * 2);   // total, type, flags, mod, sub — the ids are varints in section 3

  /// <summary>Encode `count` rows into one blob. The buffer is sized for the worst varint expansion, so the write path never bounds-checks a grow.</summary>
  internal static byte[] Encode(DamageFact[] rows, int count)
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
      p = WriteVarint(body, p, rows[i].AtkIdx);   // not a delta: ids are arrival order, not monotonic per row, and zigzag covers -1
      p = WriteVarint(body, p, rows[i].DefIdx);
    }

    for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p + (i * 4), 4), rows[i].Total);
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
    // Level lives in FactChunkCompression (shared with the heal lane): the blob format is plain Deflate either way,
    // so a level change costs no format version and no old capture stops reading.
    using (var d = new DeflateStream(ms, FactChunkCompression.NewOptions(), true)) d.Write(body, 0, p);
    return ms.ToArray();
  }

  /// <summary>Decode a blob back into exactly `count` rows — the same objects the table would have held, field for field.</summary>
  internal static DamageFact[] Decode(byte[] blob, int count)
  {
    using var src = new MemoryStream(blob);
    using var d = new DeflateStream(src, CompressionMode.Decompress);
    using var ms = new MemoryStream(count * (FixedWidth + 10));
    d.CopyTo(ms);
    var body = ms.GetBuffer();

    var rows = new DamageFact[count];
    var p = 0;
    long seq = 0, time = 0;

    // The fixed planes start where the varints stopped, so reading them is what locates them — no offset table to keep in step.
    for (var i = 0; i < count; i++)
    {
      seq += ReadVarint(body, ref p);
      time += ReadVarint(body, ref p);
      // The ids land in the seed row and ride through to the rebuild below, so decoding them needs no side array of its own.
      rows[i] = new DamageFact((int)seq, time, (int)ReadVarint(body, ref p), (int)ReadVarint(body, ref p), 0, 0, 0, 0, 0);
    }

    var total = p;
    var type = total + (count * 4);
    var flags = type + count;
    var mod = flags + count;
    var sub = mod + (count * 2);

    for (var i = 0; i < count; i++)
    {
      ref readonly var seed = ref rows[i];
      rows[i] = new DamageFact(seed.Seq, seed.TimeS,
        seed.AtkIdx,
        seed.DefIdx,
        BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(total + (i * 4), 4)),
        body[type + i],
        body[flags + i],
        (short)BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(mod + (i * 2), 2)),
        BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(sub + (i * 2), 2)));
    }

    return rows;
  }

  private static int WriteVarint(byte[] buf, int p, long value)
  {
    var u = (ulong)((value << 1) ^ (value >> 63));      // zigzag: a small negative delta costs the same bytes as a small positive one
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
