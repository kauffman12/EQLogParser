namespace EQLogParser;

[TestClass]
public sealed class CaptureTidyGateTest
{
  [TestMethod]
  public void AQueuedCleanupDiesOnCloseEvenAfterConsumption()
  {
    var gate = new CaptureTidyGate();
    var ticket = gate.Arm(1);
    Assert.IsTrue(gate.TryTake(1, out var consumed));
    Assert.AreEqual(ticket, consumed);
    Assert.IsTrue(gate.IsCurrent(ticket, 1));
    gate.Invalidate();
    Assert.IsFalse(gate.IsCurrent(ticket, 1));
    Assert.IsFalse(gate.TryTake(1, out _));
  }

  [TestMethod]
  public void AnOldWorkerCannotSpendTheNextCapturesArm()
  {
    var gate = new CaptureTidyGate();
    var old = gate.Arm(1);
    gate.Invalidate();
    var next = gate.Arm(2);
    Assert.IsFalse(gate.TryTake(1, out _));
    Assert.IsFalse(gate.IsCurrent(old, 2));
    Assert.IsTrue(gate.TryTake(2, out var consumed));
    Assert.AreEqual(next, consumed);
  }

  [TestMethod]
  public void ANewArmInvalidatesAQueuedCleanupEvenInTheSameCapture()
  {
    var gate = new CaptureTidyGate();
    var old = gate.Arm(1);
    Assert.IsTrue(gate.TryTake(1, out _));
    var next = gate.Arm(1);
    Assert.IsFalse(gate.IsCurrent(old, 1));
    Assert.IsTrue(gate.IsCurrent(next, 1));
  }

  [TestMethod]
  public void ARenderYieldDoesNotGiveAnInvalidatedTicketItsOwnershipBack()
  {
    var gate = new CaptureTidyGate();
    var ticket = gate.Arm(1);
    Assert.IsTrue(gate.TryTake(1, out _));
    Assert.IsTrue(gate.IsCurrent(ticket, 1)); // callback entered before its render yield
    gate.Invalidate();                       // close/open while the callback is suspended
    gate.Arm(2);
    Assert.IsFalse(gate.IsCurrent(ticket, 2)); // neither collect nor restore the old status
  }

  [TestMethod]
  public void BoardWorkersAndFallbackCanPayAnArmOnlyOnce()
  {
    var gate = new CaptureTidyGate();
    gate.Arm(1);
    var paid = 0;
    Parallel.For(0, 20, worker =>
    {
      if (gate.TryTake(1, out _)) Interlocked.Increment(ref paid);
    });
    Assert.AreEqual(1, paid);
  }
}
