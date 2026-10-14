using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  /*
   * A renamed file is not proof that its CONTENT is new. A sync client can replace a log with an identical copy (or an
   * identical prefix plus more lines); reading that copy from zero counts every old fight twice. Compare the bytes the
   * old reader actually reached with the replacement before choosing a starting point. This runs only on a reopen, not
   * on the per-line path. RandomAccess leaves both streams' positions and the old StreamReader's buffer untouched.
   *
   * Stop at the last shared newline: a matching fragment of a line is not a line we can safely skip. The caller drains
   * the old reader to EOF first and passes the old stream's position, so its buffered but not yet processed bytes are
   * never mistaken for consumed data. See docs/DesignNotes.md → "Following the live log across a midnight rollover".
   */
  internal static class LogFollowPrefix
  {
    private const int BlockSize = 64 * 1024;

    internal static async Task<long> SharedLineEndAsync(FileStream oldFile, FileStream replacement,
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

        return lastLineEnd;
      }
      finally
      {
        ArrayPool<byte>.Shared.Return(oldBlock);
        ArrayPool<byte>.Shared.Return(newBlock);
      }
    }
  }
}
