using System.IO.Compression;

namespace EQLogParser;

/*
 * The one Deflate level both cold-chunk codecs write with (damage and healing move together). A level is a tuning choice, not a format:
 * the blob is plain Deflate either way, so old blobs still read and none are invalidated.
 * Level 7 rather than 9 because encode runs during a load — ~4x faster for under 1 % more bytes on real chunk bodies. Level 8 and the
 * Filtered strategy were measured and rejected. Table: docs/DesignNotes.md → "The cold-chunk Deflate level".
 */
internal static class FactChunkCompression
{
  internal const int DeflateLevel = 7;

  /// <summary>Fresh per call: the options object is mutable, and a chunk encoder must not be able to edit another chunk's mind.</summary>
  internal static ZLibCompressionOptions NewOptions() => new() { CompressionLevel = DeflateLevel };
}
