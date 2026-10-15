using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class SummaryPublicationTest
{
  [TestMethod]
  public void AnAnswerQueuedWhileItsCaptureWasOpenCannotPaintTheReplacement()
  {
    var applied = false;
    var keptCurrent = false;
    Sta.Run(() =>
    {
      using var old = new DeriveEngine();
      old.Start();
      var pane = new SummaryTable();
      pane.DispatchGenerationStatus(new StatsGenerationEvent
      {
        SessionId = old.SessionId, Type = Labels.DamageParse, State = "COMPLETED"
      }, () =>
      {
        applied = true;
        pane.CurrentStats = new CombinedStats { TargetTitle = "Old capture" };
      });

      // Do not drain the dispatcher until the capture has changed: checking only at enqueue time must fail this test.
      old.Dispose();
      using var current = new DeriveEngine();
      current.Start();
      var stats = new CombinedStats { TargetTitle = "Current capture" };
      pane.CurrentStats = stats;
      Flush();
      keptCurrent = ReferenceEquals(stats, pane.CurrentStats);
    });
    Assert.IsFalse(applied);
    Assert.IsTrue(keptCurrent);
  }

  [TestMethod]
  public void ClosingWithoutAReplacementAlsoRejectsAQueuedAnswer()
  {
    var applied = false;
    Sta.Run(() =>
    {
      using var session = new DeriveEngine();
      session.Start();
      var pane = new SummaryTable();
      pane.DispatchGenerationStatus(new StatsGenerationEvent
      {
        SessionId = session.SessionId, Type = Labels.HealParse, State = "COMPLETED"
      }, () => applied = true);
      session.Dispose();
      Flush();
    });
    Assert.IsFalse(applied);
  }

  [TestMethod]
  public void LateOldEventsCannotChangeRowsOrBusyStateInTheNewCapture()
  {
    var applied = 0;
    Sta.Run(() =>
    {
      using var old = new DeriveEngine();
      old.Start();
      var oldId = old.SessionId;
      old.Dispose();
      using var current = new DeriveEngine();
      current.Start();
      var pane = new SummaryTable();

      // The shared door precedes all three panes' switches, including tanking's healing-completion branch.
      foreach (var type in new[] { Labels.DamageParse, Labels.HealParse, Labels.TankParse })
      {
        foreach (var state in new[] { "STARTED", "COMPLETED", "NONPC", "NODATA" })
        {
          pane.DispatchGenerationStatus(new StatsGenerationEvent
          {
            SessionId = oldId, Type = type, State = state
          }, () => applied++);
        }
      }
      Flush();
    });
    Assert.AreEqual(0, applied, "an old start must not arm busy state, nor may an old empty answer clear current rows");
  }

  [TestMethod]
  public void CurrentCaptureEventsStillReachThePaneInOrder()
  {
    var applied = new List<string>();
    Sta.Run(() =>
    {
      using var session = new DeriveEngine();
      session.Start();
      var pane = new SummaryTable();
      foreach (var state in new[] { "STARTED", "COMPLETED", "NONPC", "NODATA" })
      {
        pane.DispatchGenerationStatus(new StatsGenerationEvent
        {
          SessionId = session.SessionId, Type = Labels.TankParse, State = state
        }, () => applied.Add(state));
      }
      Flush();
    });
    CollectionAssert.AreEqual(new[] { "STARTED", "COMPLETED", "NONPC", "NODATA" }, applied);
  }

  private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Normal);
}
