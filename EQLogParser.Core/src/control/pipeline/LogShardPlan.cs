using System;
using System.Collections.Generic;
using System.IO;

namespace EQLogParser
{
  /*
   * Deciding WHERE to cut a log file for parallel reading — and WHETHER. Both halves are here because the
   * "where" is only correct given the "whether", and both were measured before either was written down
   * (docs/DesignNotes → the sharded-load chapter: RangeSpike over a 998 MB capture — two workers reach
   * ~1.6x wall at about today's peak RAM; three reach ~2x for roughly half again the load-time memory).
   *
   * The policy, stated plainly:
   *
   *   - Two workers, not more. Past two, RAM scales linearly while the speedup flattens — the cost is the
   *     handshake (per-shard tables, merge), not the file. THREE is a deliberate maybe-someday; the code
   *     takes `parts` so raising the dial is one number, but what SHIPS is two.
   *   - A range smaller than MinWindowBytes does not get its own worker: the merge cost and the widened
   *     boundary seams (a stream cut mid-sentence loses carryover across the cut) outweigh a sub-minute
   *     slice of parallelism. This is why a normal-size log still loads on one lane, exactly as before.
   *   - Whole-file reads only. A `lastMins` seek-back is live-tail-shaped and already partial; the reader
   *     consults this class only for `minBack < 0`.
   *
   * The cut rule: a boundary must land on a line start, which makes "line i belongs to exactly one shard"
   * a property of the PLAN rather than of read-buffer luck. Every returned byte except the first is one
   * past a '\n'; '\n' (0x0A) cannot occur inside a multi-byte UTF-8 sequence, so no character is split at
   * a boundary either — a StreamReader opened at any cut decodes exactly what the sequential reader would
   * have decoded from those bytes. (Carriage-return-only logs do not exist here: EverQuest writes '\n'.)
   */
  internal static class LogShardPlan
  {
    // What ships. The spike's numbers support THREE for +0.4x at more RAM; raising this is the whole change.
    internal const int Workers = 2;

    // Ranges smaller than this are not worth a lane, a capture, and their share of the merge.
    internal const long MinWindowBytes = 64L << 20;   // 64 MiB

    // 1 means "do not shard": sequential reading, the path every small log and every seek-back takes.
    internal static int WorkersFor(long fileBytes) => fileBytes >= (long)Workers * MinWindowBytes ? Workers : 1;

    /*
     * Returns strictly increasing offsets [0, c1, c2, ..., len] delimiting `parts` contiguous ranges — or
     * fewer ranges than asked when the file cannot supply that many line boundaries (every returned range
     * stays non-empty). Streaming chunk scan: an 8 MB buffer, one pass over the byte window around each
     * target; a gigabyte file plans in milliseconds and the cost is invisible next to the read itself.
     */
    internal static long[] CutPoints(string path, int parts)
    {
      var len = new FileInfo(path).Length;
      parts = Math.Max(1, parts);
      var cuts = new List<long> { 0 };
      var targetIndex = 1;

      const int Chunk = 1 << 23;
      var buffer = new byte[Chunk];
      using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Chunk, FileOptions.SequentialScan))
      {
        long pos = 0;
        int read;
        while (targetIndex < parts && (read = fs.Read(buffer, 0, Chunk)) > 0)
        {
          while (targetIndex < parts && (targetIndex * len) / parts < pos + read)
          {
            var target = (int)Math.Max(0, (targetIndex * len) / parts - pos);
            var found = -1;
            for (var i = target; i < read; i++)
            {
              if (buffer[i] == (byte)'\n')
              {
                found = i + 1;
                break;
              }
            }

            if (found < 0)
            {
              break;   // a line longer than one chunk: this target is chased in the next chunk
            }

            // When len < parts, integer targets collapse onto the same byte; only a strictly later
            // boundary may be recorded, but the target index always advances (or the loop would never end).
            var cut = pos + found;
            if (cut > cuts[^1])
            {
              cuts.Add(cut);
            }

            targetIndex++;
          }

          pos += read;
        }
      }

      // Fewer boundaries found than targets (tiny line counts, or trailing garbage): collapse to what the
      // file can actually give, then close on the length — but only if the last cut has not already reached it.
      if (cuts[^1] < len)
      {
        cuts.Add(len);
      }

      return [.. cuts];
    }
  }
}
