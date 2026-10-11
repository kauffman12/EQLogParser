namespace EQLogParser;

[TestClass]
public sealed class SummaryBuildGateTest
{
  [TestMethod]
  public void SchedulingIsDeferredAndOnlyTheNewestWaitingQuestionRuns()
  {
    var scheduled = new Queue<Action>();
    var built = new List<int>();
    var gate = new SummaryBuildGate(scheduled.Enqueue);
    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(1, Build(1)));
    Assert.AreEqual(SummaryBuildGate.Outcome.Queued, gate.Request(2, Build(2)));
    Assert.AreEqual(SummaryBuildGate.Outcome.Queued, gate.Request(3, Build(3)));
    Assert.IsTrue(gate.IsRunning);
    Assert.IsEmpty(built);
    while (scheduled.TryDequeue(out var work)) work();
    CollectionAssert.AreEqual(new[] { 1, 3 }, built);
    Assert.IsFalse(gate.IsRunning);
    Assert.AreEqual(SummaryBuildGate.Outcome.SkippedSame, gate.Request(3, Build(3)));
    Assert.AreEqual(2L, gate.CollapsedCount);
    Func<bool> Build(int key) => () => { built.Add(key); return true; };
  }

  [TestMethod]
  public void ReturningToTheRunningQuestionCancelsItsAbandonedSuccessor()
  {
    var scheduled = new Queue<Action>();
    var built = new List<int>();
    var gate = new SummaryBuildGate(scheduled.Enqueue);
    gate.Request(1, () => { built.Add(1); return true; });
    gate.Request(2, () => { built.Add(2); return true; });
    Assert.AreEqual(SummaryBuildGate.Outcome.SkippedSame, gate.Request(1, () => true));
    while (scheduled.TryDequeue(out var work)) work();
    CollectionAssert.AreEqual(new[] { 1 }, built);
  }

  [TestMethod]
  public void ReturningToAnEarlierAnswerWhileAnotherBuildRunsRebuildsItLast()
  {
    var scheduled = new Queue<Action>();
    var painted = new List<int>();
    var gate = new SummaryBuildGate(scheduled.Enqueue);
    gate.Request(1, Paint(1));
    scheduled.Dequeue()();
    gate.Request(2, Paint(2));
    gate.Request(3, Paint(3));
    Assert.AreEqual(SummaryBuildGate.Outcome.Queued, gate.Request(1, Paint(1)));
    while (scheduled.TryDequeue(out var work)) work();
    CollectionAssert.AreEqual(new[] { 1, 2, 1 }, painted, "B cannot be the final board under selection A");
    Func<bool> Paint(int key) => () => { painted.Add(key); return true; };
  }

  [TestMethod]
  public void AReportedFailureCanRetryAndInvalidatesThePreviousAnswer()
  {
    var gate = new SummaryBuildGate(work => work());
    gate.Request(1, () => true);
    gate.Request(2, () => false); // caught inside the board pipeline, not a throw from work
    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(2, () => false));
    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(1, () => true));
    Assert.AreEqual(SummaryBuildGate.Outcome.SkippedSame, gate.Request(1, () => true));
  }

  [TestMethod]
  public void AThrowReleasesTheGateAndStillRunsItsSuccessor()
  {
    var scheduled = new Queue<Action>();
    var gate = new SummaryBuildGate(scheduled.Enqueue);
    gate.Request(1, () => throw new InvalidOperationException());
    var ran = false;
    gate.Request(2, () => ran = true);
    Assert.ThrowsExactly<InvalidOperationException>(() => scheduled.Dequeue()());
    scheduled.Dequeue()();
    Assert.IsTrue(ran);
    Assert.IsFalse(gate.IsRunning);
    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(1, () => true));
  }

  [TestMethod]
  public void ZeroIsAValidKeyNotAnIdleSentinel()
  {
    var scheduled = new Queue<Action>();
    var gate = new SummaryBuildGate(scheduled.Enqueue);
    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(0, () => true));
    Assert.IsTrue(gate.IsRunning);
    scheduled.Dequeue()();
    Assert.AreEqual(SummaryBuildGate.Outcome.SkippedSame, gate.Request(0, () => true));
  }
}
