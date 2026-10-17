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
 *   4. total (u32) · [total high (u16)] · overTotal (u32) · [overTotal high (u16)] · type (u8) · flags (u8) · modifier mask (u16) · spell/subtype id (u16)
 *
 * The brackets are a presence byte asked once per chunk, one bit per amount: both amounts reach 48 bits, and the two bytes above 32 exist only in a
 * chunk that met a heal needing them. Ordinary chunks pay nothing for the wider domain, and every chunk pays one byte. The decoder checks the decoded
 * length against that byte before touching a plane, so a truncated blob refuses instead of reading capacity as data.
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
  private const int FixedWidth = (4 * 2) + 1 + 1 + (2 * 2);   // total low, overTotal low, type, flags, mod, sub — the ids are varints
  private const int HighWidth = 2;                            // one u16 per row per amount, present only when this chunk met a wide one
  private const byte HasTotalHigh = 1;
  private const byte HasOverTotalHigh = 2;

  /*
   * Encode `count` rows into one blob: same shape and same scratch discipline as DamageFactChunkCodec. The body buffer (2.6 MB at the shipped
   * chunk size) and the Deflate sink come from <see cref="FactChunkScratch{T}"/> and go back to it, while the blob handed out is a fresh
   * exact-size copy — a published chunk can never be rewritten by a later one.
   */
  internal static byte[] Encode(HealFact[] rows, int count)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(count);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(count, rows.Length);
    var wideTotal = false;
    var wideOverTotal = false;
    for (var i = 0; i < count && (!wideTotal || !wideOverTotal); i++)
    {
      wideTotal |= rows[i].Total > uint.MaxValue;
      wideOverTotal |= rows[i].OverTotal > uint.MaxValue;
    }

    var highWidth = ((wideTotal ? 1 : 0) + (wideOverTotal ? 1 : 0)) * HighWidth;
    var body = FactChunkScratch<HealFact>.RentBody(
      checked((count * (FactVarint.FactFieldsMaxBytes + FixedWidth + highWidth)) + 1));
    MemoryStream sink = null;
    try
    {
      var p = 1;
      body[0] = (byte)((wideTotal ? HasTotalHigh : 0) | (wideOverTotal ? HasOverTotalHigh : 0));

      long prevSeq = 0, prevTime = 0;
      for (var i = 0; i < count; i++)
      {
        p = FactVarint.Write(body, p, rows[i].Seq - prevSeq);
        prevSeq = rows[i].Seq;
        p = FactVarint.Write(body, p, rows[i].TimeS - prevTime);
        prevTime = rows[i].TimeS;
        p = FactVarint.Write(body, p, rows[i].HealerIdx);   // zigzag varints, same reason as DamageFactChunkCodec: a widened id plane cost more than the ids are worth
        p = FactVarint.Write(body, p, rows[i].HealedIdx);
      }

      for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p + (i * 4), 4), unchecked((uint)rows[i].Total));
      p += count * 4;
      if (wideTotal)
      {
        for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p + (i * 2), 2), (ushort)(rows[i].Total >> 32));
        p += count * HighWidth;
      }
      for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p + (i * 4), 4), unchecked((uint)rows[i].OverTotal));
      p += count * 4;
      if (wideOverTotal)
      {
        for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p + (i * 2), 2), (ushort)(rows[i].OverTotal >> 32));
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

      sink = FactChunkScratch<HealFact>.RentSink(Math.Max(1024, p / 4));
      // Same dial as the damage lane, for the same reason: see FactChunkCompression.
      using (var d = new DeflateStream(sink, FactChunkCompression.NewOptions(), true)) d.Write(body.AsSpan(0, p));

      // The published bytes are a copy: the sink goes straight back to work and cannot be read as this chunk's content.
      return sink.ToArray();
    }
    finally
    {
      FactChunkScratch<HealFact>.ReturnBody(body);
      if (sink is not null) FactChunkScratch<HealFact>.ReturnSink(sink);
    }
  }

  internal static HealFact[] Decode(byte[] blob, int count)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(count);
    var rows = new HealFact[count];
    DecodeInto(blob, count, rows);
    return rows;
  }

  /*
   * Prove a blob reads back as exactly these rows without allocating another chunk's worth of rows to do it — the probe array and the inflate
   * buffer come from the scratch pool. The field-by-field comparison is unchanged and still decides what happens to the chunk: a mismatch
   * leaves the rows warm in place, because a healing board is not worth a codec that can be quietly wrong.
   */
  internal static void VerifyRoundTrip(byte[] blob, HealFact[] rows)
  {
    var probe = FactChunkScratch<HealFact>.RentRows(rows.Length);
    try
    {
      DecodeInto(blob, rows.Length, probe);
      for (var i = 0; i < rows.Length; i++)
      {
        var a = rows[i];
        var b = probe[i];
        if (a.Seq != b.Seq || a.TimeS != b.TimeS || a.HealerIdx != b.HealerIdx || a.HealedIdx != b.HealedIdx
            || a.Total != b.Total || a.OverTotal != b.OverTotal || a.TypeId != b.TypeId || a.Flags != b.Flags
            || a.ModMask != b.ModMask || a.SubIdx != b.SubIdx)
          throw new InvalidOperationException("cold heal chunk round trip differs at row " + i);
      }
    }
    finally
    {
      FactChunkScratch<HealFact>.ReturnRows(probe);
    }
  }

  /// <summary>Decode into a caller-owned array: a real read brings the rows its cache will keep, verification brings rented scratch.</summary>
  internal static void DecodeInto(byte[] blob, int count, HealFact[] dest)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(count);
    ArgumentOutOfRangeException.ThrowIfLessThan(dest.Length, count);

    var rented = FactChunkScratch<HealFact>.RentBody(checked(count * (FixedWidth + 10)));
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
      if (body.IsEmpty) throw new InvalidDataException($"heal chunk of {count} rows decoded to nothing");
      if ((body[0] & ~(HasTotalHigh | HasOverTotalHigh)) != 0)
        throw new InvalidDataException("unknown heal chunk presence bits");
      var wideTotal = (body[0] & HasTotalHigh) != 0;
      var wideOverTotal = (body[0] & HasOverTotalHigh) != 0;

      var p = 1;
      long seq = 0, time = 0;

      // Reading the varints is what locates the fixed planes — no offset table that could drift from the encoder.
      for (var i = 0; i < count; i++)
      {
        seq += FactVarint.Read(body, ref p);
        time += FactVarint.Read(body, ref p);
        // A valid seed avoids four scratch arrays and must not trip the constructor's packing guards.
        dest[i] = new HealFact((int)seq, time, (int)FactVarint.Read(body, ref p), (int)FactVarint.Read(body, ref p),
          0, 0, LabelTypes.Heal, 0, LineModifiersParser.None, HealFact.NoSpell);
      }

      var total = p;
      var totalHigh = checked(total + (count * 4)); // addressed only when wideTotal
      var overTotal = checked(totalHigh + (wideTotal ? count * HighWidth : 0));
      var overTotalHigh = checked(overTotal + (count * 4)); // addressed only when wideOverTotal
      var type = checked(overTotalHigh + (wideOverTotal ? count * HighWidth : 0));
      var flags = checked(type + count);
      var mod = checked(flags + count);
      var sub = checked(mod + (count * 2));

      // Validate all fixed planes before reading any amount or metadata.
      var required = checked(sub + (count * 2));
      if (body.Length != required)
        throw new InvalidDataException($"heal chunk of {count} rows needs {required} bytes and decoded {body.Length} " +
          $"(wide total: {wideTotal}, wide asked-for: {wideOverTotal})");

      for (var i = 0; i < count; i++)
      {
        ref readonly var seed = ref dest[i];
        long landed = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(total + (i * 4), 4));
        if (wideTotal) landed |= (long)BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(totalHigh + (i * 2), 2)) << 32;
        long asked = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(overTotal + (i * 4), 4));
        if (wideOverTotal) asked |= (long)BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(overTotalHigh + (i * 2), 2)) << 32;

        dest[i] = new HealFact(seed.Seq, seed.TimeS,
          seed.HealerIdx,
          seed.HealedIdx,
          landed,
          asked,
          body[type + i],
          body[flags + i],
          (short)BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(mod + (i * 2), 2)),
          BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(sub + (i * 2), 2)));
      }
    }
    finally
    {
      FactChunkScratch<HealFact>.ReturnBody(rented);
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
