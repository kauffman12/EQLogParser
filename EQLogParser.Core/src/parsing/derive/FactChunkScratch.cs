namespace EQLogParser;

/*
 * The buffers a cold-chunk encode/decode needs and throws away, parked between calls instead of paid for every chunk.
 *
 * Why (measured 2026-10-17 on a real capture): putting 82 completed chunks away allocated **363 MB — about 7 % of everything the whole
 * parse allocated** — while the thing that has to survive, the compressed blobs, was 12.4 MB. Per chunk the codecs asked for a worst-case
 * body buffer (2.3 MB damage / 2.6 MB heal), a Deflate sink, and then — because an encode verifies itself by decoding — a second full copy
 * of the rows (1.75 MB / 1.00 MB) plus another inflate buffer. All of it was garbage the moment the chunk was put away, all of it in the
 * large-object heap, all of it churned while a raid is being captured.
 *
 * Three rules make this reuse safe rather than a corruption bug waiting for a bad day:
 *
 *   1. **One slot per kind, exchanged atomically.** A buffer is taken with `Interlocked.Exchange` (which empties the slot) and handed back
 *      with `Interlocked.CompareExchange` against null; if somebody else parked one first, what we hold is simply dropped. A rented buffer
 *      is unreachable from every other thread between take and return, so nothing can alias it, and two threads never encode into one buffer.
 *   2. **The blob stays a fresh, exact-size array.** Encoding compresses into a pooled sink and hands out `ToArray()`: the published bytes
 *      are never the scratch, so a later chunk cannot rewrite a chunk that is already stored — which is the one failure this whole file
 *      could otherwise cause, and it would be silent.
 *   3. **The retained budget is stated and capped.** A chunk body at the shipped chunk size is a couple of MB, so anything above
 *      <see cref="MaxRetainedBodyBytes"/> is served fresh and never parked; the same for rows. Steady state is roughly ten MB across both
 *      lanes (body + probe rows + sink each) in exchange for not re-allocating ~350 MB per capture, and a small capture that never fills a
 *      chunk parks nothing at all.
 *
 * Round-trip verification is NOT part of this trade: the encode path still decodes what it wrote and compares every field before a slot is
 * cleared (see DamageFactTable/HealFactTable `EncodeChunk`). It just does that decode into a buffer it does not have to allocate.
 */
internal static class FactChunkScratch<T> where T : struct
{
  // A freak request is served from the heap once and forgotten; the pool only ever holds ordinary chunk-sized buffers.
  private const int MaxRetainedBodyBytes = 8 * 1024 * 1024;
  private const int MaxRetainedRows = 1 << 17;
  private const int MaxRetainedSinkCapacity = 8 * 1024 * 1024;

  private static byte[] _body;
  private static T[] _rows;
  private static MemoryStream _sink;

  /// <summary>An array of at least `minBytes`, reused when a parked one fits.</summary>
  internal static byte[] RentBody(int minBytes)
  {
    var buf = Interlocked.Exchange(ref _body, null);
    return buf is not null && buf.Length >= minBytes ? buf : new byte[minBytes];
  }

  internal static void ReturnBody(byte[] buf)
  {
    if (buf is { Length: > 0 and <= MaxRetainedBodyBytes })
    {
      Interlocked.CompareExchange(ref _body, buf, null);
    }
  }

  /// <summary>A row array of at least `count`, for the verification decode. Callers overwrite every slot they read.</summary>
  internal static T[] RentRows(int count)
  {
    var rows = Interlocked.Exchange(ref _rows, null);
    return rows is not null && rows.Length >= count ? rows : new T[count];
  }

  internal static void ReturnRows(T[] rows)
  {
    if (rows is { Length: > 0 and <= MaxRetainedRows })
    {
      Interlocked.CompareExchange(ref _rows, rows, null);
    }
  }

  /// <summary>The Deflate output sink. Callers take their own exact-size copy out of it (`ToArray`).</summary>
  internal static MemoryStream RentSink(int capacity)
  {
    var ms = Interlocked.Exchange(ref _sink, null);
    if (ms is not null && ms.Capacity >= capacity)
    {
      ms.SetLength(0);
      ms.Position = 0;
      return ms;
    }

    return new MemoryStream(Math.Max(1024, capacity));
  }

  internal static void ReturnSink(MemoryStream ms)
  {
    if (ms is not null && ms.Capacity is > 0 and <= MaxRetainedSinkCapacity)
    {
      Interlocked.CompareExchange(ref _sink, ms, null);
    }
  }

  /// <summary>Bytes parked right now — the cost side of this trade, asserted bounded by test.</summary>
  internal static int RetainedBytes
  {
    get
    {
      var body = Volatile.Read(ref _body);
      var rows = Volatile.Read(ref _rows);
      var sink = Volatile.Read(ref _sink);

      var rowBytes = rows is null ? 0 : rows.Length * System.Runtime.InteropServices.Marshal.SizeOf<T>();
      return (body?.Length ?? 0) + rowBytes + (sink?.Capacity ?? 0);
    }
  }
}
