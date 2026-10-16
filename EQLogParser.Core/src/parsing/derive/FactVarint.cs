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

  internal static long Read(byte[] buf, ref int p)
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
