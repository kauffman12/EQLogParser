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

  /*
   * The hand-off is the one place this class can latch. A build that throws is covered by Run's finally; a scheduler that refuses to take
   * the work means no Run ever arrives to clear `_runningKey`, so every later announcement queues behind a build that does not exist — and
   * at the far end "the stats froze" is the only thing anybody can see (the same latched-flag failure as the timer overlay's _isRendering).
   * Both directions are pinned: the ask survives as a RE-ASK rather than as a phantom pending item (a queued item would let a later request
   * of the same key collapse to SkippedSame and eat the build), and the follow-up path must not replace the exception the build was already
   * carrying — the reason a board failed outranks the reason its follow-up could not start.
   */
  [TestMethod]
  public void ASchedulerThatRefusesTheWorkLeavesTheGateIdle()
  {
    var queue = new Queue<Action>();
    var down = true;
    var built = new List<int>();
    var gate = new SummaryBuildGate(a =>
    {
      if (down) throw new InvalidOperationException("scheduler closed");
      queue.Enqueue(a);
    });

    // The caller hears about it (nothing else is in flight on this path) — and nothing is left "running".
    Assert.ThrowsExactly<InvalidOperationException>(() => gate.Request(1, () => { built.Add(1); return true; }));
    Assert.IsFalse(gate.IsRunning, "a refused hand-off would otherwise read as a build in flight forever");
    Assert.IsEmpty(built);

    down = false;
    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(1, () => { built.Add(1); return true; }),
                    "the refused key may not come back answered — nothing was built");
    while (queue.TryDequeue(out var work)) work();
    CollectionAssert.AreEqual(new[] { 1 }, built);
  }

  [TestMethod]
  public void ARefusedFollowUpDoesNotMaskTheBuildsOwnThrow()
  {
    /*
     * An inline scheduler, because it is the only way a hand-off can fail while an exception is already in flight: the follow-up's dispatch
     * lives in Run's finally, so the queued ask has to arrive from inside the build that is about to fail.
     */
    var calls = 0;
    var ranSecond = false;
    var gate = new SummaryBuildGate(a =>
    {
      if (++calls == 2) throw new InvalidOperationException("scheduler closed");
      a();
    });

    var failure = Assert.ThrowsExactly<InvalidOperationException>(
      () => gate.Request(1, () =>
      {
        gate.Request(2, () => { ranSecond = true; return true; });   // queued behind the build that is about to fail
        throw new InvalidOperationException("bad record");
      }));

    // The build's own reason is what travels; "scheduler closed" would point a reader at the wrong door entirely.
    Assert.AreEqual("bad record", failure.Message);
    Assert.IsFalse(gate.IsRunning);
    Assert.IsFalse(ranSecond, "the refused follow-up never ran");

    // Nothing latched and nothing was marked answered on its behalf: both questions build on the next ask.
    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(1, () => true));
    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(2, () => { ranSecond = true; return true; }));
    Assert.IsTrue(ranSecond);
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
