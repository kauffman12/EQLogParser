namespace EQLogParser;

[TestClass]
public sealed class WindowInstanceSlotTest
{
  [TestMethod]
  public void AQueuedCloseCannotCloseTheReplacementWindow()
  {
    var slot = new WindowInstanceSlot<Window>(window => window.Closes++);
    var original = new Window();
    var replacement = new Window();
    slot.Replace(original);
    var captured = slot.Current;
    slot.Replace(replacement);
    Assert.IsFalse(slot.Close(captured));
    Assert.AreSame(replacement, slot.Current);
    Assert.AreEqual(1, original.Closes);
    Assert.AreEqual(0, replacement.Closes);
  }

  [TestMethod]
  public void ClosedNotificationFromTheRetiredWindowCannotClearItsReplacement()
  {
    WindowInstanceSlot<Window>? slot = null;
    slot = new WindowInstanceSlot<Window>(window =>
    {
      window.Closes++;
      slot?.Forget(window);
    });
    var original = new Window();
    var replacement = new Window();
    slot.Replace(original);
    slot.Replace(replacement);
    Assert.AreSame(replacement, slot.Current);
    Assert.IsTrue(slot.Close(replacement));
    Assert.IsNull(slot.Current);
    Assert.AreEqual(1, replacement.Closes);
  }

  [TestMethod]
  public void DirectCloseReleasesTheSlotAndReplacingAnInstanceWithItselfDoesNotCloseIt()
  {
    var slot = new WindowInstanceSlot<Window>(window => window.Closes++);
    var window = new Window();
    slot.Replace(window);
    slot.Replace(window);
    Assert.AreEqual(0, window.Closes);
    Assert.IsTrue(slot.Forget(window));
    Assert.IsNull(slot.Current);
    Assert.IsFalse(slot.Close(window));
    Assert.IsFalse(slot.Close(null));
  }

  private sealed class Window
  {
    internal int Closes;
  }
}
