using EQLogParser;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;
using System.Collections.Concurrent;

namespace EQLogParser.Wpf.Test;

[TestClass]
public class LogReaderCancellationTest
{
  [TestMethod]
  [DataRow(true)]
  [DataRow(false)]
  public async Task TheOuterReaderBoundaryTreatsOnlyItsOwnCancellationAsExpected(bool cancelReader)
  {
    var logger = (Logger)LogManager.GetLogger(typeof(LogReader)).Logger;
    var repository = logger.Repository ?? throw new InvalidOperationException("The reader logger has no repository");
    var previousLevel = logger.Level;
    var previousThreshold = repository.Threshold;
    var previousConfigured = repository.Configured;
    var previousAdditivity = logger.Additivity;
    var appender = new MemoryAppender();
    appender.ActivateOptions();
    var file = Path.GetTempFileName();
    LogReader? reader = null;
    try
    {
      logger.AddAppender(appender);
      logger.Level = Level.All;
      logger.Additivity = false;
      repository.Threshold = Level.All;
      repository.Configured = true;
      var processor = new InterruptingProcessor(() =>
      {
        if (cancelReader) reader?.Dispose();
      });
      reader = new LogReader(processor, file, "cancellation boundary test");

      // An exception escaping startup or a recovery catch reaches this same outer boundary.
      // Dispose cancels the real reader token; the negative case must still report an unexpected cancellation.
      await reader.StartAsync();
      Assert.IsTrue(reader.IsInValid());
      var failures = appender.GetEvents().Where(e => e.Level >= Level.Error).ToList();
      if (cancelReader)
      {
        Assert.HasCount(0, failures, "closing a reader must not tell the operator that loading failed");
        var cancellation = appender.GetEvents().Single(e =>
          e.RenderedMessage?.StartsWith("load: cancelled", StringComparison.Ordinal) == true);
        Assert.AreEqual(Level.Debug, cancellation.Level);
        Assert.IsNull(cancellation.ExceptionObject, "expected cancellation has no exception stack");
      }
      else
      {
        Assert.HasCount(1, failures, "an unrelated OperationCanceledException is still a failure");
        StringAssert.Contains(failures[0].RenderedMessage, "Error Loading File");
        Assert.IsInstanceOfType<OperationCanceledException>(failures[0].ExceptionObject);
      }
    }
    finally
    {
      reader?.Dispose();
      logger.RemoveAppender(appender);
      logger.Level = previousLevel;
      logger.Additivity = previousAdditivity;
      repository.Threshold = previousThreshold;
      repository.Configured = previousConfigured;
      appender.Close();
      File.Delete(file);
    }
  }

  private sealed class InterruptingProcessor(Action beforeThrow) : ILogProcessor
  {
    public void LinkTo(BlockingCollection<LogReaderItem[]> collection)
    {
      beforeThrow();
      throw new OperationCanceledException("test interruption");
    }

    public void Dispose()
    {
    }
  }
}
