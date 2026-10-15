using System.Collections.Concurrent;
using System.Threading;

namespace EQLogParser;

[TestClass]
public sealed class ProcessedBatchTest
{
  private sealed class BlockingSink : IChatSink, ITriggerHook
  {
    internal readonly ManualResetEventSlim Entered = new();
    internal readonly ManualResetEventSlim Release = new();
    public void Init() { }
    public void Add(ChatType chat)
    {
      Entered.Set();
      Release.Wait();
    }
    public void CheckQuickShare(ChatType chat, string action, double beginTime) { }
  }

  [TestMethod]
  public void AHandedOffBatchIsNotAcknowledgedUntilItsLastLineIsParsed()
  {
    var sink = new BlockingSink();
    using var queue = new BlockingCollection<LogReaderItem[]>();
    using var processor = new LogProcessor("test", sink, sink);
    processor.LinkTo(queue);
    try
    {
      Assert.AreEqual(0L, processor.ProcessedLines);
      queue.Add([new("[Wed Oct 01 12:00:00 2025] Test says, 'hello'", 1, false),
                 new("[Wed Oct 01 12:00:01 2025] Test says, 'again'", 2, false)]);
      queue.CompleteAdding();
      Assert.IsTrue(sink.Entered.Wait(TimeSpan.FromSeconds(10)), "the parser did not reach the chat sink");
      Assert.AreEqual(0L, processor.ProcessedLines, "taking a batch off the queue is not parsing its lines");
      sink.Release.Set();
      Assert.IsTrue(processor.Completion.Wait(TimeSpan.FromSeconds(10)), "the parser did not drain");
      Assert.AreEqual(2L, processor.ProcessedLines);
    }
    finally
    {
      sink.Release.Set();
    }
  }
}
