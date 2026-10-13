using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

/*
 * Narrow annotations only (Core builds with Nullable=disable); this API has one optional callback.
 */
#nullable enable annotations

namespace EQLogParser
{
  /*
   * Lines of one byte range of a log file, with exact fidelity to what sequential StreamReader.ReadLine
   * would have returned for those same bytes — the property the sharded backfill stands on. LogShardPlan
   * decides WHERE ranges start (line boundaries, making "every line belongs to exactly one range" true by
   * construction); this class makes "the lines are the same lines" true by construction too.
   *
   * The fidelity details, each mirrored deliberately:
   *   - Lines end at '\n'; a '\r' immediately before it is dropped (ReadLine eats CRLF whole).
   *   - Decoding is Encoding.UTF8 — the encoding StreamReader defaults to, replacement semantics included.
   *     A range can only begin after a '\n', and '\n' cannot occur inside a multi-byte UTF-8 sequence, so
   *     every range starts at a character boundary and no decode ever sees half a character — except the
   *     file's genuine final incomplete bytes, where both readers produce U+FFFD.
   *   - The UTF-8 BOM is skipped, but only by range 0 (it is three bytes of the file, found once).
   *   - A last line with no terminator is still delivered (ReadLine delivers it).
   *
   * Reads synchronously; a shard worker calls this on its own pool thread and the caller's bounded queue
   * provides the backpressure. Cancellation throws OperationCanceledException between chunks.
   */
  internal static class LogRangeReader
  {
    internal const int ChunkBytes = 1 << 20;

    // progress: bytes of THIS range consumed so far, after each chunk (progress reporting only).
    internal static void ForEachLine(string path, long from, long to,
      Action<string> line, Action<long>? progress = null, CancellationToken token = default)
    {
      if (from < 0 || to < from)
      {
        throw new ArgumentOutOfRangeException(nameof(from), $"range [{from}, {to}) is not valid");
      }

      if (from == to)
      {
        return;
      }

      var buffer = new byte[ChunkBytes];
      var pending = new List<byte>(1024);   // bytes of the line currently spanning a chunk boundary (any length)

      using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 0x10000, FileOptions.SequentialScan);
      if (from > 0)
      {
        fs.Seek(from, SeekOrigin.Begin);
      }

      var pos = from;
      var firstChunk = true;
      while (pos < to)
      {
        token.ThrowIfCancellationRequested();

        var want = (int)Math.Min(ChunkBytes, to - pos);
        var read = fs.Read(buffer.AsSpan(0, want));
        if (read <= 0)
        {
          break;   // the file shrank under the plan: the range simply ends early
        }

        var start = 0;
        if (firstChunk && from == 0 && read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
          start = 3;   // UTF-8 BOM belongs to no line
        }

        firstChunk = false;
        var lineStart = start;
        for (var i = start; i < read; i++)
        {
          if (buffer[i] != (byte)'\n')
          {
            continue;
          }

          if (i > lineStart)
          {
            pending.AddRange(new ArraySegment<byte>(buffer, lineStart, i - lineStart));
          }

          line(DecodeLine(pending));
          pending.Clear();
          lineStart = i + 1;
        }

        if (lineStart < read)
        {
          pending.AddRange(new ArraySegment<byte>(buffer, lineStart, read - lineStart));
        }

        pos += read;
        progress?.Invoke(pos - from);
      }

      if (pending.Count > 0)
      {
        // The file's last line has no terminator; ReadLine delivers it, so must this.
        line(DecodeLine(pending));
      }
    }

    private static string DecodeLine(List<byte> pending)
    {
      var span = CollectionsMarshal.AsSpan(pending);
      if (span.Length > 0 && span[^1] == (byte)'\r')
      {
        span = span[..^1];   // CRLF is one line break, not a character
      }

      return Encoding.UTF8.GetString(span);
    }
  }
}
