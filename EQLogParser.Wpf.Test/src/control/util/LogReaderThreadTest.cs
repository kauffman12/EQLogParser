using EQLogParser;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace EQLogParser.Wpf.Test
{
  /// <summary>
  /// Who runs the read loop, pinned as a property rather than as a comment.
  ///
  /// A bulk open spends most of its life parked in <c>LogReader</c>'s full-queue <c>Add</c>: the reader runs far ahead of
  /// the parse lane (measured on the reference capture - reading runs at hundreds of MB/s against a consumer an order of
  /// magnitude slower), so the 100,000-item queue is full within a second and every handoff blocks. Before this class made
  /// its awaits context-free, a caller who started it from a dispatcher callback (MainWindow's open path is exactly that)
  /// captured WPF's synchronization context, every continuation came back to the UI thread, and reads suspend only once per
  /// ~144 KB buffer - about a thousand lines - so almost every iteration continued inline there anyway. The steady state of
  /// opening a 350 MB capture was the UI thread reading EQ's log and then blocking on the parser.
  ///
  /// The test below is the failure mode as a fixture: it starts the reader on a thread whose <see cref="SynchronizationContext"/>
  /// accepts posts and NEVER runs them. A single continuation that still wants the starting context means the read stops,
  /// the timeout expires and this fails by name - which is what "the loader may not own a message pump" means in practice.
  /// </summary>
  [TestClass]
  public class LogReaderThreadTest
  {
    /* Enough lines to cross several batches (LogReader flushes every 5,000) and the whole file, so a loop that dies after
       one lucky handoff cannot pass. */
    private const int Lines = 20_000;
    private const int BatchSeen = 5_000;

    /// <summary>Consumes whatever the reader hands over and counts it - no parsing, no dispatcher, no state to clean.</summary>
    private sealed class CountingProcessor : ILogProcessor
    {
      internal long Seen;
      private Task? _pump;

      public void LinkTo(BlockingCollection<LogReaderItem> collection)
      {
        _pump = Task.Run(() =>
        {
          try
          {
            foreach (var _ in collection.GetConsumingEnumerable()) Interlocked.Increment(ref Seen);
          }
          catch (InvalidOperationException)
          {
            // CompleteAdding during Dispose.
          }
        });
      }

      public void Dispose()
      {
        try { _pump?.Wait(2000); } catch { }
      }
    }

    /// <summary>A context that records what was posted to it and deliberately never runs any of it.</summary>
    private sealed class NoPumpContext : SynchronizationContext
    {
      internal long Posted;

      public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posted);

      // A synchronous Send would block this thread until the owner ran it - and the owner never does, which is the point:
      // reaching here is a failure of the code under test, not of the fixture.
      public override void Send(SendOrPostCallback d, object? state)
        => throw new InvalidOperationException("the read loop asked its starting thread to run a continuation inline");

      public override SynchronizationContext CreateCopy() => this;
    }

    private static string WriteCapture()
    {
      var path = Path.Combine(Path.GetTempPath(), $"eqlog_LogReaderThreadTest_{Guid.NewGuid():N}.txt");
      var sb = new System.Text.StringBuilder(64 * Lines);
      // HandleLine refuses a line without a parseable [DDD MMM dd HH:mm:ss yyyy] header, so these are ordinary lines.
      for (var i = 0; i < Lines; i++)
        sb.Append("[Wed Oct 01 12:00:00 2025] You hit a training dummy for ").Append(i).AppendLine(".");
      File.WriteAllText(path, sb.ToString());
      return path;
    }

    [TestMethod]
    public void AReaderStartedOnAContextThatNeverPumpsStillReadsTheWholeFile()
    {
      var file = WriteCapture();
      var context = new NoPumpContext();
      var processor = new CountingProcessor();
      var previous = SynchronizationContext.Current;

      try
      {
        SynchronizationContext.SetSynchronizationContext(context);

        // minBack in years, not minutes: the reader seeks to that date, and this capture is dated 2025-10-01.
        var reader = new LogReader(processor, file, minBack: 60 * 24 * 365 * 10);
        var running = reader.StartAsync();
        Assert.IsFalse(running.IsCompleted, "a static file still ends in the monitor loop; it must not have finished already");

        var watch = Stopwatch.StartNew();
        while (Interlocked.Read(ref processor.Seen) < Lines && watch.Elapsed.TotalSeconds < 60)
        {
          Thread.Sleep(25);
          // Nothing pumps this thread, so if a continuation were posted to it the count stops rising and we time out.
          Assert.IsFalse(running.IsFaulted, $"the read loop faulted without a pump: {running.Exception?.GetBaseException().Message}");
        }

        Assert.IsTrue(Interlocked.Read(ref processor.Seen) >= Lines,
          $"the reader stopped after {Interlocked.Read(ref processor.Seen)} of {Lines} lines because a continuation "
          + "was posted back to the thread that started it");
        Assert.AreEqual(0L, Interlocked.Read(ref context.Posted),
          "the read loop posted work to its starting synchronization context; every await must be ConfigureAwait(false)");

        reader.Dispose();
      }
      finally
      {
        SynchronizationContext.SetSynchronizationContext(previous);
        processor.Dispose();
        try { File.Delete(file); } catch { }
      }
    }
  }
}
