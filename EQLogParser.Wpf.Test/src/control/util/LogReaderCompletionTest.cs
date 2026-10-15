using EQLogParser;
using System.Threading;

namespace EQLogParser;

[TestClass]
public sealed class LogReaderCompletionTest
{
  private sealed class BlockingChatSink : IChatSink, ITriggerHook
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
  public async Task ReachingEndOfFileDoesNotAnnounceCompletionBeforeParsingFinishes()
  {
    var file = Path.Combine(Path.GetTempPath(), $"eqlp-completion-{Guid.NewGuid():N}.txt");
    File.WriteAllText(file, "[Wed Oct 01 12:00:00 2025] Test says, 'hello'\n");
    var sink = new BlockingChatSink();
    var reader = new LogReader(new LogProcessor(file, sink, sink), file, "test", -1);
    try
    {
      var running = Task.Run(reader.StartAsync);
      Assert.IsTrue(sink.Entered.Wait(TimeSpan.FromSeconds(10)), "the parser never entered the blocking sink");
      Assert.IsTrue(SpinWait.SpinUntil(() => reader.GetProgress() >= 100 && reader.HandedOverLines == 1,
        TimeSpan.FromSeconds(10)), "the reader did not reach the end of the file");
      Assert.IsFalse(reader.InitialLoadComplete, "file position and handoff do not prove the parser consumed the batch");

      sink.Release.Set();
      Assert.IsTrue(SpinWait.SpinUntil(() => reader.InitialLoadComplete, TimeSpan.FromSeconds(10)),
        "the initial read never became complete after the parser finished");
      reader.Dispose();
      await running.WaitAsync(TimeSpan.FromSeconds(10));
    }
    finally
    {
      sink.Release.Set();
      reader.Dispose();
      File.Delete(file);
    }
  }

  [TestMethod]
  public async Task ClosingWhileWaitingForTheFileDoesNotStartAnOrphanedRead()
  {
    var file = Path.Combine(Path.GetTempPath(), $"eqlp-missing-{Guid.NewGuid():N}.txt");
    var sink = new BlockingChatSink();
    var reader = new LogReader(new LogProcessor(file, sink, sink), file, "test", -1);
    var running = Task.Run(reader.StartAsync);
    try
    {
      reader.Dispose();
      await running.WaitAsync(TimeSpan.FromSeconds(10));
      Assert.IsFalse(reader.InitialLoadComplete);
    }
    finally
    {
      reader.Dispose();
    }
  }
}
