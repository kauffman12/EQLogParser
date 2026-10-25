using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

#nullable enable annotations

namespace EQLogParser;

[TestClass]
public sealed class NameCensusDisposalTest
{
  [TestMethod]
  public void DisposingAnEngineDoesNotWaitForItsCensusGate()
  {
    var closedBeforeRelease = false;
    var detached = false;
    Sta.Run(() =>
    {
      using var session = new DeriveEngine();
      var gate = Field(session, "_nameCensusGate");
      using var entered = new ManualResetEventSlim();
      using var closed = new ManualResetEventSlim();

      // Keep the build gate held until disposal returns. The deadline only releases a broken implementation for teardown.
      var holder = Task.Run(() =>
      {
        lock (gate)
        {
          entered.Set();
          closedBeforeRelease = closed.Wait(TimeSpan.FromSeconds(5));
        }
      });
      try
      {
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)), "The worker must hold the census gate before closing.");
        session.Dispose();
        detached = EngineField("_nameCensusCarry").GetValue(session) is null;
      }
      finally
      {
        closed.Set();
        holder.GetAwaiter().GetResult();
      }
    });

    Assert.IsTrue(closedBeforeRelease, "Closing a log must not wait for a background census to release its gate.");
    Assert.IsTrue(detached, "A disposed engine must no longer own the census carry.");
  }

  [TestMethod]
  public void ACensusQueuedBeforeDisposalDoesNotBuildAfterTheGateOpens()
  {
    var refused = false;
    Sta.Run(() =>
    {
      using var session = new DeriveEngine();
      var gate = Field(session, "_nameCensusGate");
      // Avoid the first-pass classification fallback: this test is about ownership after waiting, not rule execution.
      EngineField("_carriedTimeline").SetValue(session, new EntityTimeline());
      ClassificationReport? report = null;
      Exception? failure = null;
      var worker = new Thread(() =>
      {
        try
        {
          report = session.BuildNameCensus();
        }
        catch (Exception ex)
        {
          failure = ex;
        }
      }) { IsBackground = true };

      try
      {
        lock (gate)
        {
          worker.Start();
          Assert.IsTrue(SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(5)), "The request must already be waiting on the census gate.");
          session.Dispose();
        }
      }
      finally
      {
        Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)), "The queued census must finish after the gate opens.");
      }

      Assert.IsNull(failure);
      refused = report is null;
      Assert.IsNull(session.BuildNameCensus(), "Later requests must also be refused.");
    });

    Assert.IsTrue(refused, "A request queued before closing must re-check disposal after acquiring the gate.");
  }

  private static object Field(DeriveEngine session, string name) =>
    EngineField(name).GetValue(session) ?? throw new InvalidOperationException($"Empty engine field {name}");

  private static FieldInfo EngineField(string name) =>
    typeof(DeriveEngine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException($"Missing engine field {name}");
}
