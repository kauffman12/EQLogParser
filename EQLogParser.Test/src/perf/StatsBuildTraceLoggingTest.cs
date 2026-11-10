using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

namespace EQLogParser;

[TestClass]
public sealed class StatsBuildTraceLoggingTest
{
  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void NamedOverlapsAreDebugOnlyButAlwaysCounted(bool debug)
  {
    var before = StatsBuildTrace.OverlapBuilds;
    var events = Capture(debug, () =>
    {
      var first = StatsBuildTrace.Begin("damage", "derived selection");
      try
      {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A blocked Task.Run can be inlined by the test runner; overlap requires a genuinely different thread.
        var visitor = new Thread(() =>
        {
          try
          {
            for (var i = 0; i < 2; i++)
            {
              var meter = StatsBuildTrace.Begin("meter", "damage meter overlay", "incremental");
              StatsBuildTrace.End(meter);
            }
            completed.SetResult();
          }
          catch (Exception ex)
          {
            completed.SetException(ex);
          }
        }) { IsBackground = true };
        visitor.Start();
        completed.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Assert.AreEqual(2, StatsBuildTrace.OverlapsInCurrentEpisode);
      }
      finally
      {
        StatsBuildTrace.End(first);
      }
    });

    Assert.AreEqual(before + 2, StatsBuildTrace.OverlapBuilds);
    Assert.AreEqual(0, StatsBuildTrace.OverlapsInCurrentEpisode);
    Assert.AreEqual(0, StatsBuildTrace.ActiveBuilds);
    Assert.IsFalse(events.Any(e => e.Level >= Level.Info), "Independent meter/summary work must not warn in a normal log.");
    if (debug)
    {
      Assert.AreEqual(1, events.Count(e => e.RenderedMessage?.Contains(" started while ") == true));
      Assert.AreEqual(1, events.Count(e =>
        e.RenderedMessage?.StartsWith("stats builds overlapped", StringComparison.Ordinal) == true));
      Assert.IsTrue(events.All(e => e.Level == Level.Debug));
    }
    else
    {
      Assert.IsEmpty(events, "Overlap counters must not require an ordinary log entry.");
    }
  }

  [TestMethod]
  public void AnUnlabelledDoorRemainsAWarningWithoutDebug()
  {
    var events = Capture(false, () =>
    {
      var build = StatsBuildTrace.Begin("damage", null);
      StatsBuildTrace.End(build);
    });

    Assert.HasCount(1, events);
    Assert.AreEqual(Level.Warn, events[0].Level);
    StringAssert.Contains(events[0].RenderedMessage, "did not name itself");
  }

  private static LoggingEvent[] Capture(bool debug, Action body)
  {
    var logger = (Logger)LogManager.GetLogger(typeof(StatsBuildTrace)).Logger;
    var repository = logger.Repository ?? throw new InvalidOperationException("The stats logger has no repository.");
    var previousLevel = logger.Level;
    var previousThreshold = repository.Threshold;
    var previousConfigured = repository.Configured;
    var previousAdditivity = logger.Additivity;
    var appender = new MemoryAppender();
    appender.ActivateOptions();
    try
    {
      logger.AddAppender(appender);
      logger.Level = debug ? Level.Debug : Level.Info;
      logger.Additivity = false;
      repository.Threshold = Level.All;
      repository.Configured = true;
      body();
      return appender.GetEvents();
    }
    finally
    {
      logger.RemoveAppender(appender);
      logger.Level = previousLevel;
      logger.Additivity = previousAdditivity;
      repository.Threshold = previousThreshold;
      repository.Configured = previousConfigured;
      appender.Close();
    }
  }
}
