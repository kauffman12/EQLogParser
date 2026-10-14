using EQLogParser;
using System.Text;

namespace EQLogParser;

/* Raw-byte handoff across two file objects: one reader, no duplicate capture and no skipped new-day head. */
[TestClass]
public class LogFollowPrefixTest
{
  [TestMethod]
  public async Task ExactCopyResumesAfterTheLastOldLine()
  {
    const string log = "[Sat Nov 07 23:59:58 2026] A says, 'one'\r\n" +
                       "[Sat Nov 07 23:59:59 2026] B says, 'two'\r\n";
    Assert.AreEqual(Encoding.UTF8.GetByteCount(log), await SharedEndAsync(log, log));
  }

  [TestMethod]
  public async Task CopyWithNewLinesResumesAtTheFirstNewLine()
  {
    const string old = "old-one\nold-two\n";
    Assert.AreEqual(Encoding.UTF8.GetByteCount(old),
      await SharedEndAsync(old, old + "new-day\n"));
  }

  [TestMethod]
  public async Task DifferentDayStartsAtZero()
  {
    Assert.AreEqual(0, await SharedEndAsync("yesterday\n", "today\n"));
  }

  [TestMethod]
  public async Task MatchingPrefixStopsAtTheFirstDifferentLine()
  {
    Assert.AreEqual(Encoding.UTF8.GetByteCount("same\r\n"),
      await SharedEndAsync("same\r\nyesterday\r\n", "same\r\ntoday\r\n"));
  }

  [TestMethod]
  public async Task MatchingFragmentOfALineIsNeverSkipped()
  {
    Assert.AreEqual(0, await SharedEndAsync("[Sat Nov 07] old\n", "[Sat Nov 07] new\n"));
  }

  [TestMethod]
  public async Task ComparisonNeverAssumesBufferedBytesWereProcessed()
  {
    // The StreamReader may have read a whole block ahead of the last line it actually handed to the processor.
    Assert.AreEqual(Encoding.UTF8.GetByteCount("first\n"),
      await SharedEndAsync("first\nnot-yet-read\n", "first\nnot-yet-read\nnew\n",
        Encoding.UTF8.GetByteCount("first\n")));
  }

  [TestMethod]
  public async Task IdenticalLargePrefixAcrossBlocksAndUtf8Names()
  {
    var old = string.Concat(Enumerable.Repeat("You say, 'Ésperñe and the long raid line'\r\n", 2_000));
    Assert.IsTrue(Encoding.UTF8.GetByteCount(old) > 64 * 1024);
    Assert.AreEqual(Encoding.UTF8.GetByteCount(old), await SharedEndAsync(old, old + "today\n"));
  }

  [TestMethod]
  public async Task EmptyReplacementStartsAtZero()
  {
    Assert.AreEqual(0, await SharedEndAsync("old\n", ""));
  }

  [TestMethod]
  public async Task IdenticalRegionWithNoLineBoundaryHasNoAnswer()
  {
    // A replacement still being copied: identical bytes, no newline yet. Neither zero nor its end is a safe start,
    // so the caller keeps the old handle and asks again on the next poll.
    Assert.IsNull(await SharedEndAsync("[Sat Nov 07 23:59:58 2026] A says, 'one'\n",
      "[Sat Nov 07 23:59:58 2026] A says", Encoding.UTF8.GetByteCount("[Sat Nov 07 23:59:58 2026] A says")));
  }

  private static async Task<long?> SharedEndAsync(string oldContent, string nextContent, long? oldReadLength = null)
  {
    var directory = Path.Combine(Path.GetTempPath(), "eqlp-follow-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "eqlog_Test_server.txt");
    var rotated = Path.Combine(directory, "rotated.txt");
    try
    {
      await File.WriteAllTextAsync(path, oldContent);
      using var oldFile = Open(path);
      File.Move(path, rotated); // oldFile's handle remains valid while the NAME changes hands
      await File.WriteAllTextAsync(path, nextContent);
      using var replacement = Open(path);
      return await LogFollowPrefix.SharedLineEndAsync(oldFile, replacement,
        oldReadLength ?? oldFile.Length, CancellationToken.None);
    }
    finally
    {
      Directory.Delete(directory, true);
    }
  }

  private static FileStream Open(string path) => new(path, new FileStreamOptions
  {
    Mode = FileMode.Open,
    Access = FileAccess.Read,
    Share = FileShare.ReadWrite | FileShare.Delete,
    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
  });
}
