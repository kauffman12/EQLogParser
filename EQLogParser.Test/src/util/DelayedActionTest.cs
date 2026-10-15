using EQLogParser;

namespace EQLogParserTest;

[TestClass]
public class DelayedActionTest
{
  private static readonly TimeSpan TimeoutBudget = TimeSpan.FromSeconds(5);

  [TestMethod]
  public void NonGenericInvocationRunsAfterTheDelay()
  {
    using var called = new ManualResetEventSlim();
    using var action = new DelayedAction(TimeSpan.FromMilliseconds(30), called.Set);

    action.Invoke();

    Assert.IsTrue(called.Wait(TimeoutBudget), "the non-generic wrapper never ran its callback");
  }

  [TestMethod]
  public void RepeatedInvocationsDeliverOnlyTheLastValueOnce()
  {
    using var called = new ManualResetEventSlim();
    var count = 0;
    var received = -1;
    using var action = new DelayedAction<int>(TimeSpan.FromMilliseconds(50), value =>
    {
      received = value;
      Interlocked.Increment(ref count);
      called.Set();
    });

    action.Invoke(1);
    action.Invoke(2);
    action.Invoke(3);

    Assert.IsTrue(called.Wait(TimeoutBudget), "the generic callback never ran");
    Assert.AreEqual(3, received, "a later invoke must replace the pending value");
    Assert.AreEqual(1, Volatile.Read(ref count), "several invokes before the timer fires are one callback");
  }

  [TestMethod]
  public void ResetDelayDefersTheCallbackAndStillRunsOnlyOnce()
  {
    using var called = new ManualResetEventSlim();
    var count = 0;
    using var action = new DelayedAction<int>(TimeSpan.FromMilliseconds(200), _ =>
    {
      Interlocked.Increment(ref count);
      called.Set();
    });

    action.Invoke(1, resetDelay: true);
    Assert.IsFalse(called.Wait(TimeSpan.FromMilliseconds(100)), "the timer fired before its first deadline");
    action.Invoke(2, resetDelay: true);
    Assert.IsFalse(called.Wait(TimeSpan.FromMilliseconds(120)), "the original deadline fired despite a reset");
    Assert.IsTrue(called.Wait(TimeoutBudget), "the reset timer never fired");
    Assert.AreEqual(1, Volatile.Read(ref count));
  }

  [TestMethod]
  public void DisposePreventsPendingAndLaterInvocations()
  {
    using var called = new ManualResetEventSlim();
    var action = new DelayedAction(TimeSpan.FromMilliseconds(50), called.Set);
    action.Invoke();
    action.Dispose();
    action.Dispose();
    action.Invoke();

    Assert.IsFalse(called.Wait(TimeSpan.FromMilliseconds(150)), "a disposed timer invoked its callback");
  }

  [TestMethod]
  public void GenericCallbackCanReceiveNull()
  {
    using var called = new ManualResetEventSlim();
    string? received = "not set";
    using var action = new DelayedAction<string?>(TimeSpan.FromMilliseconds(30), value =>
    {
      received = value;
      called.Set();
    });

    action.Invoke(null);

    Assert.IsTrue(called.Wait(TimeoutBudget), "the null payload never reached the callback");
    Assert.IsNull(received);
  }
}
