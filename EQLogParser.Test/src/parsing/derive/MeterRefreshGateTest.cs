namespace EQLogParser;

[TestClass]
public class MeterRefreshGateTest
{
  [TestMethod]
  public void DeriveAndTimerRequestsShareOneMonotonicCadence()
  {
    var gate = new MeterRefreshGate();
    Assert.IsTrue(gate.Request(0));
    Assert.IsFalse(gate.Request(0.1));
    Assert.IsFalse(gate.Complete(0.2));
    Assert.IsFalse(gate.Request(0.5));
    Assert.IsTrue(gate.Request(1));
    Assert.IsFalse(gate.Complete(1.1));
  }

  [TestMethod]
  public void ALongBuildPaysTheNewestRequestWithoutStartingParallelWork()
  {
    var gate = new MeterRefreshGate();
    Assert.IsTrue(gate.Request(0));
    Assert.IsFalse(gate.Request(0.5));
    Assert.IsFalse(gate.Request(1));
    Assert.IsFalse(gate.Request(2));
    Assert.IsTrue(gate.Complete(2.5));
    Assert.IsFalse(gate.Complete(2.6), "one pending slot, not one queued task per event");
  }

  [TestMethod]
  public void ResetCanBypassTheCadenceButNotTheInFlightWorker()
  {
    var gate = new MeterRefreshGate();
    Assert.IsTrue(gate.Request(0));
    Assert.IsFalse(gate.Request(0.1, true));
    Assert.IsTrue(gate.Complete(0.2));
    Assert.IsFalse(gate.Complete(0.3));
    Assert.IsTrue(gate.Request(0.4, true));
  }

  [TestMethod]
  public void ResetImmediatelyRejectsAnOlderWorkerCheckpoint()
  {
    var checkpoint = new MeterLifecycleCheckpoint();
    var owner = checkpoint.Open();
    var old = checkpoint.Read(owner);
    checkpoint.Reset(1, 200);
    old.State.Reset(1, 100);
    Assert.IsFalse(checkpoint.TryCommit(old));
    var row = new DerivedFight { Name = "a rat", BeginTime = 150, LastTime = 160, DamageTotal = 100 };
    row.FactEnd = 160;
    Assert.IsTrue(checkpoint.Read(owner).State.TrySelect(1, [row], 160, 0, new(160, 160), 160, out var scope));
    Assert.HasCount(0, scope.Rows);
  }

  [TestMethod]
  public void ClosingAndReopeningCannotGiveTheOldWindowOwnershipAgain()
  {
    var checkpoint = new MeterLifecycleCheckpoint();
    var oldOwner = checkpoint.Open();
    checkpoint.Reset(1, 100);
    var old = checkpoint.Read(oldOwner);
    var currentOwner = checkpoint.Open();
    old.State.Reset(1, 200);
    Assert.IsFalse(checkpoint.TryCommit(old));
    var current = checkpoint.Read(currentOwner);
    var row = new DerivedFight { Name = "a rat", BeginTime = 150, LastTime = 160, DamageTotal = 100 };
    row.FactEnd = 160;
    Assert.IsTrue(current.State.TrySelect(1, [row], 160, 0, new(160, 160), 160, out var scope));
    Assert.HasCount(1, scope.Rows);
    Assert.IsTrue(checkpoint.TryCommit(current));
    Assert.IsFalse(checkpoint.TryCommit(current), "an already consumed checkpoint cannot overwrite a newer answer");
  }

  [TestMethod]
  public void ScalarLifecycleCopiesCannotApplyResetToEachOther()
  {
    var saved = new DamageOverlayLifecycle();
    saved.Reset(1, 100);
    var worker = saved.Copy();
    worker.Reset(1, 200);
    var row = new DerivedFight { Name = "a rat", BeginTime = 150, LastTime = 160, DamageTotal = 100 };
    row.FactEnd = 160;
    Assert.IsTrue(saved.TrySelect(1, [row], 160, 0, new(160, 160), 160, out var savedScope));
    Assert.IsTrue(worker.TrySelect(1, [row], 160, 0, new(160, 160), 160, out var workerScope));
    Assert.HasCount(1, savedScope.Rows);
    Assert.HasCount(0, workerScope.Rows);
  }
}
