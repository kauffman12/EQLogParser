using System.IO.Compression;

namespace EQLogParser;

/*
 * The one Deflate setting both cold-chunk codecs write with (damage and healing share it so the two lanes of one
 * capture always move together).
 *
 * It used to be `CompressionLevel.SmallestSize`, i.e. zlib level 9, and .NET 10's `ZLibCompressionOptions` is what
 * made the choice tunable at all: the options type is accepted by `DeflateStream`, so an exact level is nameable
 * WITHOUT changing the blob format — every shipped blob still decompresses with a plain `DeflateStream`, and every
 * old blob still reads (verified: `SmallestSize` reproduces options level 9 byte for byte, `Optimal` reproduces 6).
 *
 * Level 7 because encode runs while a log is loading. Measured on eqlog_Kizant_xegony-09-03-26.txt (4,832,103
 * damage facts / 2,670,809 heals, 73 damage + 81 heal cold chunks) over real chunk bodies: damage +0.44 % bytes for
 * 3.4x faster encode, heals +0.95 % bytes for 4.0x faster — about 4 s of pool CPU down to about 1.6 s per load, for
 * ~150 KB more standing. Level 8 was measured too: it compressed slightly SMALLER than 9 and ran only 7 % faster on
 * the damage lane, which is why the dial did not stop there. The `Filtered` strategy — the one aimed at
 * predictor output, which is what a varint-delta plane is — was measured and is worse on both axes: our entropy
 * coder input is already post-filter, and the string matcher it suppresses is doing real work. Full table:
 * docs/DesignNotes.md → "The cold-chunk Deflate level".
 */
internal static class FactChunkCompression
{
  internal const int DeflateLevel = 7;

  /// <summary>Fresh per call: the options object is mutable, and a chunk encoder must not be able to edit another chunk's mind.</summary>
  internal static ZLibCompressionOptions NewOptions() => new() { CompressionLevel = DeflateLevel };
}
