using EQLogParser;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace EQLogParser.Wpf.Test
{
  /*
   * Real LogReader handoffs rather than just the Core decisions: this assembly builds on Linux, but these tests execute
   * only on Windows. The name stays the same while the file object changes, as it does during Log Management's move
   * and recreate or an EQ/client rotation. We assert the EXACT lines and their order, not just a progress number.
   */
  [TestClass]
  public class LogReaderRolloverTest
  {
    private const string OldLine = "[Sat Nov 07 23:59:59 2026] You say, 'old line'";
    private const string TailLine = "[Sat Nov 07 23:59:59 2026] You say, 'old tail'";
    private const string NewLine = "[Sun Nov 08 00:00:01 2026] You say, 'new day'";

    private sealed class CapturingProcessor : ILogProcessor
    {
      internal readonly ConcurrentQueue<string> Lines = new();
      private Task? _pump;

      public void LinkTo(BlockingCollection<LogReaderItem[]> collection)
      {
        _pump = Task.Run(() =>
        {
          foreach (var batch in collection.GetConsumingEnumerable())
          {
            foreach (var item in batch)
            {
              Lines.Enqueue(item.Line);
            }
          }
        });
      }

      public void Dispose()
      {
        try { _pump?.Wait(3000); } catch { }
      }
    }

    [TestMethod]
    public async Task CopyOfAlreadyReadLogDoesNotDoubleCountButItsNewHeadArrives()
    {
      var directory = NewDirectory();
      var file = Path.Combine(directory, "eqlog_RolloverTest_server.txt");
      var rolled = Path.Combine(directory, "rolled.txt");
      await File.WriteAllTextAsync(file, "");
      var processor = new CapturingProcessor();
      var reader = new LogReader(processor, file, origin: "test-copy", minBack: 0);
      var running = Task.Run(reader.StartAsync);
      try
      {
        await WaitForAsync(() => reader.GetProgress() == 100, "the monitor did not reach its tail");
        await File.AppendAllTextAsync(file, OldLine + "\r\n");
        await WaitForAsync(() => processor.Lines.Contains(OldLine), "the old line was never read");

        // A sync client may atomically replace the path with a byte-for-byte copy PLUS lines written while we were
        // following the first object. Different creation stamps alone would read OldLine again; seeking to End instead
        // would drop NewLine. Compare the consumed prefix and begin immediately after it.
        File.Move(file, rolled);
        await File.WriteAllTextAsync(file, OldLine + "\r\n" + NewLine + "\r\n");
        await WaitForAsync(() => processor.Lines.Contains(NewLine), "the replacement's first new line was skipped");
        await Task.Delay(400);
        CollectionAssert.AreEqual(new[] { OldLine, NewLine }, processor.Lines.ToArray(),
          "the replacement must contribute its NEW line once, and never replay an already-read one");
      }
      finally
      {
        reader.Dispose();
        try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        processor.Dispose();
        Directory.Delete(directory, true);
      }
    }

    [TestMethod]
    public async Task RenameDrainsOldTailBeforeTheNewDaysFirstLine()
    {
      var directory = NewDirectory();
      var file = Path.Combine(directory, "eqlog_RolloverTest_server.txt");
      var rolled = Path.Combine(directory, "rolled.txt");
      await File.WriteAllTextAsync(file, "");
      var processor = new CapturingProcessor();
      var reader = new LogReader(processor, file, origin: "test-tail", minBack: 0);
      var running = Task.Run(reader.StartAsync);
      try
      {
        await WaitForAsync(() => reader.GetProgress() == 100, "the monitor did not reach its tail");
        // Append and rename in one burst; the old handle still holds this line even if the polling reader was asleep.
        await File.AppendAllTextAsync(file, TailLine + "\r\n");
        File.Move(file, rolled);
        await File.WriteAllTextAsync(file, NewLine + "\r\n");

        await WaitForAsync(() => processor.Lines.Contains(NewLine), "the new file was never followed");
        await Task.Delay(400);
        CollectionAssert.AreEqual(new[] { TailLine, NewLine }, processor.Lines.ToArray(),
          "the old file's unread tail must precede the new file's head, once each");
      }
      finally
      {
        reader.Dispose();
        try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        processor.Dispose();
        Directory.Delete(directory, true);
      }
    }

    private static string NewDirectory()
    {
      var directory = Path.Combine(Path.GetTempPath(), "eqlp-reader-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(directory);
      return directory;
    }

    private static async Task WaitForAsync(Func<bool> condition, string failure)
    {
      var watch = Stopwatch.StartNew();
      while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(12))
      {
        await Task.Delay(20);
      }

      Assert.IsTrue(condition(), failure);
    }
  }
}
