#nullable enable annotations

namespace EQLogParser;

// Deterministic dispatcher interleavings at the production handoff. No collection or allocation probes run here.
[TestClass]
public class PostLoadCleanupOwnershipTest
{
  [TestInitialize]
  public void Init() => PostLoadCleanup.Reset();

  [TestCleanup]
  public void CleanUp() => PostLoadCleanup.Reset();

  [TestMethod]
  public void AnOldBuildCannotConsumeTheReplacementCapturesDebt()
  {
    var handoff = new Handoff();
    handoff.ReplaceCapture();
    handoff.Run();

    Assert.AreEqual(2, handoff.OwedSessionId);
    Assert.AreEqual(0, handoff.Events.Count);
    Assert.AreEqual(0, handoff.Posted.Count);
    Assert.IsTrue(PostLoadCleanup.Owes);
    Assert.IsTrue(PostLoadCleanup.TryTake(ref handoff.OwedSessionId, 2), "the replacement still gets to pay");
  }

  [TestMethod]
  public void OnlyTheOwningCaptureCanTakeItsDebtAndOnlyOnce()
  {
    var owed = 1;
    Assert.IsFalse(PostLoadCleanup.TryTake(ref owed, 2));
    Assert.AreEqual(1, owed);
    Assert.IsTrue(PostLoadCleanup.TryTake(ref owed, 1));
    Assert.AreEqual(0, owed);
    Assert.IsFalse(PostLoadCleanup.TryTake(ref owed, 1));
    Assert.IsFalse(PostLoadCleanup.TryTake(ref owed, 0), "no capture is not a cleanup request");
  }

  [TestMethod]
  public void AReplacementBeforeTheDispatcherCallbackGetsNoOldLabelOrCollection()
  {
    var handoff = new Handoff();
    handoff.BeforeInvoke = handoff.ReplaceCapture;
    handoff.Run();
    handoff.Drain();

    Assert.AreEqual(0, handoff.Events.Count);
    Assert.AreEqual(2, handoff.OwedSessionId);
    Assert.IsTrue(PostLoadCleanup.Owes, "the abandoned finally must not clear the replacement's hold");
  }

  [TestMethod]
  public void AReplacementAfterTheLabelButBeforeCollectionSkipsCollection()
  {
    var handoff = new Handoff();
    handoff.AfterInvoke = handoff.ReplaceCapture;
    handoff.Run();
    handoff.Drain();

    CollectionAssert.AreEqual(new[] { "begin" }, handoff.Events);
    Assert.IsTrue(PostLoadCleanup.Owes);
  }

  [TestMethod]
  public void ACollectionThatOutlivesItsCaptureCannotReleaseTheNextHold()
  {
    var handoff = new Handoff();
    handoff.DuringCollection = handoff.ReplaceCapture;
    handoff.Run();
    handoff.Drain();

    CollectionAssert.AreEqual(new[] { "begin", "collect" }, handoff.Events);
    Assert.AreEqual(2, handoff.OwedSessionId);
    Assert.IsTrue(PostLoadCleanup.Owes);
  }

  [TestMethod]
  public void AReplacementBeforeQueuedCompletionGetsNoOldReleaseOrUiUpdate()
  {
    var handoff = new Handoff();
    handoff.Run();
    handoff.ReplaceCapture();
    handoff.Drain();

    CollectionAssert.AreEqual(new[] { "begin", "collect" }, handoff.Events);
    Assert.IsTrue(PostLoadCleanup.Owes);
  }

  [TestMethod]
  public void SuccessfulCompletionReleasesOnTheDispatcherBeforeUpdatingTheBand()
  {
    var handoff = new Handoff();
    handoff.Run();
    Assert.IsTrue(PostLoadCleanup.Owes, "release belongs to the queued ownership check, not the worker");
    handoff.Drain();

    CollectionAssert.AreEqual(new[] { "begin", "collect", "end" }, handoff.Events);
    Assert.IsFalse(PostLoadCleanup.Owes);
  }

  [TestMethod]
  public void AThrowingCollectionStillQueuesItsOwnedRelease()
  {
    var handoff = new Handoff { DuringCollection = () => throw new InvalidOperationException("collection failed") };
    Assert.ThrowsExactly<InvalidOperationException>(handoff.Run);
    Assert.IsTrue(PostLoadCleanup.Owes);
    handoff.Drain();

    CollectionAssert.AreEqual(new[] { "begin", "collect", "end" }, handoff.Events);
    Assert.IsFalse(PostLoadCleanup.Owes);
  }

  private sealed class Handoff
  {
    internal int OwedSessionId = 1;
    internal readonly Queue<Action> Posted = new();
    internal readonly List<string> Events = [];
    internal Action? BeforeInvoke;
    internal Action? AfterInvoke;
    internal Action? DuringCollection;
    private int _currentSessionId = 1;
    private bool _onDispatcher;

    internal Handoff() => PostLoadCleanup.Mark();

    internal void Run()
    {
      if (!PostLoadCleanup.TryTake(ref OwedSessionId, 1)) return;
      PostLoadCleanup.RunOwned(() => _currentSessionId == 1, Invoke, Posted.Enqueue,
        begin: () =>
        {
          Assert.IsTrue(_onDispatcher);
          Events.Add("begin");
        },
        end: () =>
        {
          Assert.IsTrue(_onDispatcher);
          Assert.IsFalse(PostLoadCleanup.Owes, "the band reads the released hold");
          Events.Add("end");
        },
        collect: () =>
        {
          Assert.IsFalse(_onDispatcher);
          Events.Add("collect");
          DuringCollection?.Invoke();
        });
    }

    internal void ReplaceCapture()
    {
      _currentSessionId = 2;
      OwedSessionId = 2;
      PostLoadCleanup.Reset();
      PostLoadCleanup.Mark();
    }

    internal void Drain()
    {
      _onDispatcher = true;
      try
      {
        while (Posted.TryDequeue(out var callback)) callback();
      }
      finally
      {
        _onDispatcher = false;
      }
    }

    private void Invoke(Action callback)
    {
      BeforeInvoke?.Invoke();
      _onDispatcher = true;
      try
      {
        callback();
      }
      finally
      {
        _onDispatcher = false;
      }
      AfterInvoke?.Invoke();
    }
  }
}
