using EQLogParser;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParserTest
{
  [TestClass]
  public class DelayedActionTest
  {
    [TestMethod]
    public void DelayedAction_General_InvokesActionAfterDelay()
    {
      var invoked = false;
      using var action = new DelayedAction(TimeSpan.FromMilliseconds(50), () => invoked = true);
      action.Invoke();
      Thread.Sleep(100);
      Assert.IsTrue(invoked);
    }

    [TestMethod]
    public void DelayedAction_General_UsesLastInvokeValue()
    {
      int lastValue = -1;
      using var action = new DelayedAction<int>(TimeSpan.FromMilliseconds(50), v => lastValue = v);
      action.Invoke(1);
      action.Invoke(2);
      action.Invoke(3);
      Thread.Sleep(100);
      Assert.AreEqual(3, lastValue);
    }

    [TestMethod]
    public void DelayedAction_General_InvokesOnlyOnce()
    {
      int invokeCount = 0;
      using var action = new DelayedAction<int>(TimeSpan.FromMilliseconds(50), _ => invokeCount++);
      action.Invoke(1);
      action.Invoke(2);
      action.Invoke(3);
      Thread.Sleep(100);
      Assert.AreEqual(1, invokeCount);
    }

    [TestMethod]
    public void DelayedAction_General_ResetDelay_ExtendsTimer()
    {
      int invokeCount = 0;
      using var action = new DelayedAction<int>(TimeSpan.FromMilliseconds(50), _ => invokeCount++);
      action.Invoke(1, resetDelay: true);
      Thread.Sleep(30);
      action.Invoke(2, resetDelay: true);
      Thread.Sleep(30);
      action.Invoke(3, resetDelay: true);
      // Should not have fired yet because we reset the delay each time
      Assert.AreEqual(0, invokeCount);
      Thread.Sleep(100);
      Assert.AreEqual(1, invokeCount);
    }

    [TestMethod]
    public void DelayedAction_General_Dispose_PreventsInvocation()
    {
      var invoked = false;
      var action = new DelayedAction(TimeSpan.FromMilliseconds(50), () => invoked = true);
      action.Invoke();
      action.Dispose();
      Thread.Sleep(100);
      Assert.IsFalse(invoked);
    }

    [TestMethod]
    public void DelayedAction_General_InvokeAfterDispose_NoOp()
    {
      var invoked = false;
      var action = new DelayedAction(TimeSpan.FromMilliseconds(50), () => invoked = true);
      action.Dispose();
      action.Invoke(); // Should not throw
      Thread.Sleep(100);
      Assert.IsFalse(invoked);
    }

    [TestMethod]
    public void DelayedAction_Generic_InvokesActionAfterDelay()
    {
      int received = -1;
      using var action = new DelayedAction<int>(TimeSpan.FromMilliseconds(50), v => received = v);
      action.Invoke(42);
      Thread.Sleep(100);
      Assert.AreEqual(42, received);
    }

    [TestMethod]
    public void DelayedAction_Generic_MultipleInvokesWithoutReset_FiresOnce()
    {
      int invokeCount = 0;
      using var action = new DelayedAction<int>(TimeSpan.FromMilliseconds(50), _ => invokeCount++);
      action.Invoke(1);
      action.Invoke(2);
      // Without resetDelay, the timer is not reset, so it fires once
      Thread.Sleep(100);
      Assert.AreEqual(1, invokeCount);
    }

    [TestMethod]
    public void DelayedAction_Generic_Dispose_CleansUp()
    {
      int invokeCount = 0;
      var action = new DelayedAction<int>(TimeSpan.FromMilliseconds(50), _ => invokeCount++);
      action.Invoke(1);
      action.Dispose();
      action.Dispose(); // Double dispose should not throw
      Thread.Sleep(100);
      Assert.AreEqual(0, invokeCount);
    }

    [TestMethod]
    public void DelayedAction_General_Constructor_ShortInterval()
    {
      var invoked = false;
      using var action = new DelayedAction(TimeSpan.FromMilliseconds(10), () => invoked = true);
      action.Invoke();
      Thread.Sleep(50);
      Assert.IsTrue(invoked);
    }

    [TestMethod]
    public void DelayedAction_Generic_NullValue_IsHandled()
    {
      string received = "not set";
      using var action = new DelayedAction<string>(TimeSpan.FromMilliseconds(50), v => received = v);
      action.Invoke(null);
      Thread.Sleep(100);
      Assert.IsNull(received);
    }
  }
}
