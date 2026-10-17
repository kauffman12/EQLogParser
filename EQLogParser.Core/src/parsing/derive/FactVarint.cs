using System.IO;

namespace EQLogParser;

// Shared zigzag encoding for the two cold fact formats; both lanes use the same signed field widths.
internal static class FactVarint
{
  // Sequence deltas and two int name ids need at most five bytes each; allow ten for the long clock delta.
  // The difference of two int sequences still fits five zigzag-varint bytes (at most 2^33-2 unsigned).
  internal const int FactFieldsMaxBytes = (3 * 5) + 10;

  internal static int Write(byte[] buf, int p, long value)
  {
    var u = (ulong)((value << 1) ^ (value >> 63));
    while (u >= 0x80)
    {
      buf[p++] = (byte)(u | 0x80);
      u >>= 7;
    }
    buf[p++] = (byte)u;
    return p;
  }

  /*
   * Bounded on purpose. A cold blob is decoded into a buffer whose CAPACITY can exceed what was written into it, and an
   * unbounded read walks off the end of the rows and turns truncated data into an enormous id: reading a chunk asks for
   * the varint inside the bytes that are actually there and refuses otherwise.
   */
  internal static long Read(ReadOnlySpan<byte> data, ref int p)
  {
    ulong u = 0;
    var shift = 0;

    while (true)
    {
      if ((uint)p >= (uint)data.Length) throw new InvalidDataException("fact chunk varint runs past the end of the decoded body");
      if (shift > 63) throw new InvalidDataException("fact chunk varint is wider than a long");

      var b = data[p++];
      // Only bit 63 remains in the tenth byte; higher payload bits would be silently shifted away.
      if (shift == 63 && b > 1) throw new InvalidDataException("fact chunk varint overflows a long");
      u |= (ulong)(b & 0x7F) << shift;
      if ((b & 0x80) == 0) break;
      shift += 7;
    }

    return (long)((u >> 1) ^ (~(u & 1) + 1));
  }
}
