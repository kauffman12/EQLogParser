using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  /*
   * A renamed file is not proof that its CONTENT is new: a sync client can replace a log with an identical copy, and
   * reading that copy from zero counts every old fight twice. Compare the bytes the old reader reached with the
   * replacement before choosing where the new reader starts. Runs only on a reopen; RandomAccess leaves both stream
   * positions and the old StreamReader's buffer alone.
   *
   * Answers: bytes to skip, or null for "no usable answer yet" (the compared region matched whole but held no newline).
   * Skip complete lines only — a matching fragment of a line is not a line we may drop. The caller drains the old reader
   * to EOF first and passes that position, so buffered-but-unprocessed bytes are never treated as consumed.
   */
  internal static class LogFollowPrefix
  {
    private const int BlockSize = 64 * 1024;

    internal static async Task<long?> SharedLineEndAsync(FileStream oldFile, FileStream replacement,
      long oldReadLength, CancellationToken cancellationToken)
    {
      var limit = Math.Min(Math.Min(oldReadLength, oldFile.Length), replacement.Length);
      if (limit <= 0)
      {
        return 0;
      }

      var oldBlock = ArrayPool<byte>.Shared.Rent(BlockSize);
      var newBlock = ArrayPool<byte>.Shared.Rent(BlockSize);
      try
      {
        long offset = 0;
        long lastLineEnd = 0;
        while (offset < limit)
        {
          var size = (int)Math.Min(BlockSize, limit - offset);
          var oldCount = await RandomAccess.ReadAsync(oldFile.SafeFileHandle, oldBlock.AsMemory(0, size), offset,
            cancellationToken).ConfigureAwait(false);
          var newCount = await RandomAccess.ReadAsync(replacement.SafeFileHandle, newBlock.AsMemory(0, size), offset,
            cancellationToken).ConfigureAwait(false);
          var count = Math.Min(oldCount, newCount);
          if (count == 0)
          {
            break;
          }

          var oldSpan = oldBlock.AsSpan(0, count);
          var newSpan = newBlock.AsSpan(0, count);
          if (oldSpan.SequenceEqual(newSpan))
          {
            var lastNewline = oldSpan.LastIndexOf((byte)'\n');
            if (lastNewline >= 0)
            {
              lastLineEnd = offset + lastNewline + 1;
            }
          }
          else
          {
            for (var i = 0; i < count; i++)
            {
              // A difference means the replacement is genuinely new content from here: everything before the last shared
              // newline (possibly none) is the only part proven already read.
              if (oldBlock[i] != newBlock[i])
              {
                return lastLineEnd;
              }

              if (oldBlock[i] == (byte)'\n')
              {
                lastLineEnd = offset + i + 1;
              }
            }
          }

          offset += count;
        }

        /*
         * Whole-region match with no line boundary: the replacement is byte-identical so far but the first line has not
         * been written (or copied) yet. Starting at 0 would replay lines the old reader already handed over, and seeking
         * to its end could drop them, so say "no answer" and let the caller retry after the next poll.
         */
        return lastLineEnd > 0 ? lastLineEnd : null;
      }
      finally
      {
        ArrayPool<byte>.Shared.Return(oldBlock);
        ArrayPool<byte>.Shared.Return(newBlock);
      }
    }
  }
}
