using System.Buffers.Binary;
using System.IO.Compression;

namespace EQLogParser;

/*
 * The cold-chunk encoding for damage rows: transposed planes with zigzag-varint deltas on the monotonic columns, then Deflate. Chosen by
 * measurement over a 2,286,368-row chunk set — row-major deflate 14.62 MB / planes+delta varint 9.02 MB (5.80x), and Brotli's extra 16 % costs
 * fifteen times the encode time, which is refused because encoding runs while a raid is being captured. Decode is ~3 ms per chunk, which is what
 * makes decode-on-read affordable.
 *
 * Layout, compressed as one Deflate stream: a presence byte, then zigzag varint deltas of Seq, then TimeS (epochs rebased through FactTime on the
 * way back in), then attacker and defender ids as varints, then total u32 · [total high u16] · type u8 · flags u8 · modifier mask u16 · subtype id u16.
 *
 * The presence byte is the amount's 48th bit and up, asked once per chunk: a row whose hit fits in 32 bits (which every captured hit does, and most
 * synthetic ones too) contributes nothing but the zero its low plane already holds, so the high plane costs 2 bytes a row ONLY in a chunk that met an
 * amount needing it. The decoder validates the decoded length against that byte before reading a plane, because a blob whose capacity is wider than
 * what was written used to let a truncated read pose as an id.
 *
 * Ids are varints, not an i32 plane: fixed 4-byte planes made a cold row 30 bytes instead of 26 and failed the cold-store halving guard on
 * healing, while a varint costs 1-3 bytes and spends one byte on the -1 sentinel. Fields go through the row's public surface so the codec cannot
 * disagree with FactTime about a stored second.
 */
internal static class DamageFactChunkCodec
{
  private const int FixedWidth = 4 + 1 + 1 + (2 * 2);   // total low, type, flags, mod, sub — the ids are varints; the amount's high half is optional
  private const int HighWidth = 2;                     // one u16 per row, present only when a row in THIS chunk needed more than 32 bits
  private const byte HasTotalHigh = 1;                 // the presence byte's only bit so far

  /// <summary>Encode `count` rows into one blob. The buffer is sized for the worst varint expansion, so the write path never bounds-checks a grow.</summary>
  internal static byte[] Encode(DamageFact[] rows, int count)
  {
    // One pass to ask whether this chunk carries an amount above 32 bits at all; ordinary chunks stay the size they were.
    var wide = false;
    for (var i = 0; i < count && !wide; i++) wide = rows[i].Total > uint.MaxValue;

    var body = new byte[((count * (FactVarint.FactFieldsMaxBytes + FixedWidth)) + (wide ? count * HighWidth : 0)) + 1];
    var p = 1;
    body[0] = (byte)(wide ? HasTotalHigh : 0);

    long prevSeq = 0, prevTime = 0;
    for (var i = 0; i < count; i++)
    {
      p = FactVarint.Write(body, p, rows[i].Seq - prevSeq);
      prevSeq = rows[i].Seq;
      p = FactVarint.Write(body, p, rows[i].TimeS - prevTime);
      prevTime = rows[i].TimeS;
      p = FactVarint.Write(body, p, rows[i].AtkIdx);   // not a delta: ids are arrival order, not monotonic per row, and zigzag covers -1
      p = FactVarint.Write(body, p, rows[i].DefIdx);
    }

    for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p + (i * 4), 4), (uint)rows[i].Total);
    p += count * 4;
    if (wide)
    {
      for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p + (i * 2), 2), (ushort)(rows[i].Total >> 32));
      p += count * HighWidth;
    }
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
    var body = new ReadOnlySpan<byte>(ms.GetBuffer(), 0, (int)ms.Length);   // the POPULATED length: capacity is not data

    if (body.IsEmpty) throw new InvalidDataException($"damage chunk of {count} rows decoded to nothing");
    var wide = (body[0] & HasTotalHigh) != 0;

    var rows = new DamageFact[count];
    var p = 1;
    long seq = 0, time = 0;

    // The fixed planes start where the varints stopped, so reading them is what locates them — no offset table to keep in step.
    for (var i = 0; i < count; i++)
    {
      seq += FactVarint.Read(body, ref p);
      time += FactVarint.Read(body, ref p);
      // The ids land in the seed row and ride through to the rebuild below, so decoding them needs no side array of its own.
      rows[i] = new DamageFact((int)seq, time, (int)FactVarint.Read(body, ref p), (int)FactVarint.Read(body, ref p), 0, 0, 0, 0, 0);
    }

    var total = p;
    var totalHigh = total + (count * 4);                       // only addressed when wide
    var type = totalHigh + (wide ? count * HighWidth : 0);
    var flags = type + count;
    var mod = flags + count;
    var sub = mod + (count * 2);

    // The planes are located by arithmetic, so a blob that is short (or long) by any part of one row cannot be read as rows at all.
    var required = sub + (count * 2);
    if (body.Length != required)
      throw new InvalidDataException($"damage chunk of {count} rows needs {required} bytes and decoded {body.Length} (wide: {wide})");

    for (var i = 0; i < count; i++)
    {
      ref readonly var seed = ref rows[i];
      long amount = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(total + (i * 4), 4));
      if (wide) amount |= (long)BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(totalHigh + (i * 2), 2)) << 32;

      rows[i] = new DamageFact(seed.Seq, seed.TimeS,
        seed.AtkIdx,
        seed.DefIdx,
        amount,
        body[type + i],
        body[flags + i],
        (short)BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(mod + (i * 2), 2)),
        BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(sub + (i * 2), 2)));
    }

    return rows;
  }

}
