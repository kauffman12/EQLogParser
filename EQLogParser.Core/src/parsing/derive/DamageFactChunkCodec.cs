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
 * Bit 0 of the presence byte announces a high-u16 plane (amount bits 32–47). Narrow-only chunks omit it.
 * Decode accepts only known presence bits and the exact populated payload length.
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

  /*
   * Encode `count` rows into one blob. The body buffer is sized for the worst varint expansion, so the write path never bounds-checks a
   * grow, and it comes from <see cref="FactChunkScratch{T}"/>: at the shipped chunk size this was 2.3 MB of large-heap garbage per chunk on
   * its way to becoming a few hundred KB of blob. The blob a caller receives is always a fresh exact-size array (`ToArray`), never the
   * scratch — that is what keeps a later chunk from being able to touch bytes that are already published.
   */
  internal static byte[] Encode(DamageFact[] rows, int count)
  {
    // One pass to ask whether this chunk carries an amount above 32 bits at all; ordinary chunks stay the size they were.
    var wide = false;
    for (var i = 0; i < count && !wide; i++) wide = rows[i].Total > uint.MaxValue;

    var body = FactChunkScratch<DamageFact>.RentBody(
      checked((count * (FactVarint.FactFieldsMaxBytes + FixedWidth + (wide ? HighWidth : 0))) + 1));
    MemoryStream sink = null;
    try
    {
      var p = BuildBody(body, rows, count);

      sink = FactChunkScratch<DamageFact>.RentSink(Math.Max(1024, p / 4));
      // Level lives in FactChunkCompression (shared with the heal lane): the blob format is plain Deflate either way,
      // so a level change costs no format version and no old capture stops reading.
      using (var d = new DeflateStream(sink, FactChunkCompression.NewOptions(), true)) d.Write(body.AsSpan(0, p));

      // The published bytes are a copy: the sink goes straight back to work and cannot be read as this chunk's content.
      return sink.ToArray();
    }
    finally
    {
      FactChunkScratch<DamageFact>.ReturnBody(body);
      if (sink is not null) FactChunkScratch<DamageFact>.ReturnSink(sink);
    }
  }

  /*
   * The uncompressed body: presence byte, varint deltas and fixed planes exactly as Encode writes them. Split out so a codec
   * comparison can hand the IDENTICAL bytes to any compressor; Encode compresses this with Deflate at FactChunkCompression's level.
   */
  internal static int BuildBody(byte[] body, DamageFact[] rows, int count)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(count);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(count, rows.Length);

    // One pass to ask whether this chunk carries an amount above 32 bits at all; ordinary chunks stay the size they were.
    var wide = false;
    for (var i = 0; i < count && !wide; i++) wide = rows[i].Total > uint.MaxValue;

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

    for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p + (i * 4), 4), unchecked((uint)rows[i].Total));
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

    return p;
  }

  /// <summary>Decode a blob back into exactly `count` rows — the same objects the table would have held, field for field.</summary>
  internal static DamageFact[] Decode(byte[] blob, int count)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(count);
    var rows = new DamageFact[count];
    DecodeInto(blob, count, rows);
    return rows;
  }

  /*
   * Prove a blob reads back as exactly these rows without allocating another chunk's worth of rows to do it: the probe array and the inflate
   * buffer both come from the scratch pool. The comparison itself is the table's standing guarantee, word for word — an encoding fault has to
   * degrade to "this chunk stayed raw", never to a board quietly reading somebody else's row.
   */
  internal static void VerifyRoundTrip(byte[] blob, DamageFact[] rows)
  {
    var probe = FactChunkScratch<DamageFact>.RentRows(rows.Length);
    try
    {
      DecodeInto(blob, rows.Length, probe);
      for (var i = 0; i < rows.Length; i++)
        if (!SameRow(rows[i], probe[i])) throw new InvalidOperationException($"damage codec differs at row {i}");
    }
    finally
    {
      FactChunkScratch<DamageFact>.ReturnRows(probe);
    }
  }

  private static bool SameRow(in DamageFact a, in DamageFact b) =>
    a.Seq == b.Seq && a.TimeS == b.TimeS && a.AtkIdx == b.AtkIdx && a.DefIdx == b.DefIdx && a.Total == b.Total
    && a.TypeId == b.TypeId && a.Flags == b.Flags && a.ModMask == b.ModMask && a.SubIdx == b.SubIdx;

  /*
   * Decode into a caller-owned array: a real read brings the rows its cache will keep, verification brings rented scratch. The inflate goes
   * straight into a rented buffer rather than a MemoryStream grown by CopyTo — that buffer was another ~0.9 MB per chunk read.
   */
  internal static void DecodeInto(byte[] blob, int count, DamageFact[] dest)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(count);
    ArgumentOutOfRangeException.ThrowIfLessThan(dest.Length, count);

    var rented = FactChunkScratch<DamageFact>.RentBody(checked(count * (FixedWidth + 10)));
    var buf = rented;
    var filled = 0;
    try
    {
      using var src = new MemoryStream(blob, 0, blob.Length, false);
      using var d = new DeflateStream(src, CompressionMode.Decompress);
      while (true)
      {
        if (filled == buf.Length) buf = Grow(buf); // body beat the capacity hint: fresh buffer, the rented one still goes back
        var got = d.Read(buf.AsSpan(filled));
        if (got == 0) break;
        filled += got;
      }

      var body = buf.AsSpan(0, filled);   // the POPULATED length: capacity is not data
      if (body.IsEmpty) throw new InvalidDataException($"damage chunk of {count} rows decoded to nothing");
      if ((body[0] & ~HasTotalHigh) != 0) throw new InvalidDataException("unknown damage chunk presence bits");
      var wide = (body[0] & HasTotalHigh) != 0;

      var p = 1;
      long seq = 0, time = 0;

      // The fixed planes start where the varints stopped, so reading them is what locates them — no offset table to keep in step.
      for (var i = 0; i < count; i++)
      {
        seq += FactVarint.Read(body, ref p);
        time += FactVarint.Read(body, ref p);
        // The ids land in the seed row and ride through to the rebuild below, so decoding them needs no side array of its own.
        dest[i] = new DamageFact((int)seq, time, (int)FactVarint.Read(body, ref p), (int)FactVarint.Read(body, ref p), 0, 0, 0, 0, 0);
      }

      var total = p;
      var totalHigh = checked(total + (count * 4)); // only addressed when wide
      var type = checked(totalHigh + (wide ? count * HighWidth : 0));
      var flags = checked(type + count);
      var mod = checked(flags + count);
      var sub = checked(mod + (count * 2));

      // Validate all fixed planes before reading any amount or metadata.
      var required = checked(sub + (count * 2));
      if (body.Length != required)
        throw new InvalidDataException($"damage chunk of {count} rows needs {required} bytes and decoded {body.Length} (wide: {wide})");

      for (var i = 0; i < count; i++)
      {
        ref readonly var seed = ref dest[i];
        long amount = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(total + (i * 4), 4));
        if (wide) amount |= (long)BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(totalHigh + (i * 2), 2)) << 32;

        dest[i] = new DamageFact(seed.Seq, seed.TimeS,
          seed.AtkIdx,
          seed.DefIdx,
          amount,
          body[type + i],
          body[flags + i],
          (short)BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(mod + (i * 2), 2)),
          BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(sub + (i * 2), 2)));
      }
    }
    finally
    {
      FactChunkScratch<DamageFact>.ReturnBody(rented);
    }
  }

  /// <summary>A bigger inflate buffer for a body that beat the capacity hint (rare; the rented buffer still goes back).</summary>
  private static byte[] Grow(byte[] buf)
  {
    var grown = new byte[checked(buf.Length * 2)];
    Array.Copy(buf, grown, buf.Length);
    return grown;
  }
}
