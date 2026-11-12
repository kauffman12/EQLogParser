namespace EQLogParser;

[TestClass]
public sealed class NpcStatsRefreshTest
{
  [TestMethod]
  public void LoadingBeforeTheFirstSnapshotOwesAnAnswerUntilPublication()
  {
    var refresh = new NpcStatsRefresh();
    refresh.WaitFor(100);
    Assert.IsFalse(refresh.Ask(1, 0));
    Assert.IsTrue(refresh.Pending);
    Assert.IsTrue(refresh.Ask(2, 100));
    Assert.IsTrue(refresh.Pending, "starting work is not an answer");
    Assert.IsTrue(refresh.Complete(2, 100));
    Assert.IsFalse(refresh.Pending, "ordinary live growth must not owe a whole-capture scan every pass");
  }

  [TestMethod]
  public void AMidReadProjectionDoesNotPayForTheCompletedRead()
  {
    var refresh = new NpcStatsRefresh();
    Assert.IsTrue(refresh.Ask(1, 50));
    refresh.WaitFor(100);
    Assert.IsFalse(refresh.CanPublish(1, 50));
    Assert.IsFalse(refresh.Complete(1, 50), "loading may finish while a mid-read build is in flight");
    Assert.IsTrue(refresh.Pending);
    Assert.IsTrue(refresh.Ask(2, 110));
    Assert.IsTrue(refresh.Complete(2, 110));
    Assert.IsFalse(refresh.Pending);
  }

  [TestMethod]
  public void AskingWhileTheSnapshotIsMissingCannotForgetTheReadWatermark()
  {
    var refresh = new NpcStatsRefresh();
    refresh.WaitFor(100);
    refresh.WaitFor(0);
    refresh.Invalidate();
    Assert.IsFalse(refresh.Ask(1, 50));
    Assert.IsTrue(refresh.Ask(2, 100));
    Assert.IsTrue(refresh.Pending);
    Assert.IsTrue(refresh.Complete(2, 100));
  }

  [TestMethod]
  public void AFailedBuildCanRetryTheSameQuestionWithoutRearmingAnything()
  {
    var scheduled = new Queue<Action>();
    var gate = new SummaryBuildGate(scheduled.Enqueue);
    var refresh = new NpcStatsRefresh();
    refresh.WaitFor(100);
    Assert.IsTrue(refresh.Ask(1, 100));
    gate.Request(1, () => false);
    scheduled.Dequeue()();
    Assert.IsTrue(refresh.Pending, "a caught failure must not consume the completed-read debt");
    Assert.IsTrue(refresh.Ask(1, 100));
    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(1, () => refresh.Complete(1, 100)));
    scheduled.Dequeue()();
    Assert.IsFalse(refresh.Pending);
  }

  [TestMethod]
  public void OnlyTheNewestQueuedQuestionCanPublishAndPayTheDebt()
  {
    var scheduled = new Queue<Action>();
    var gate = new SummaryBuildGate(scheduled.Enqueue);
    var refresh = new NpcStatsRefresh();
    var published = new List<int>();
    refresh.WaitFor(100);
    for (var i = 1; i <= 3; i++)
    {
      var key = i;
      refresh.Ask(key, 100);
      gate.Request(key, () =>
      {
        if (!refresh.CanPublish(key, 100)) return false;
        published.Add(key);
        return refresh.Complete(key, 100);
      });
    }

    scheduled.Dequeue()();
    Assert.IsTrue(refresh.Pending, "the superseded worker did not answer the newest question");
    scheduled.Dequeue()();
    CollectionAssert.AreEqual(new[] { 3 }, published);
    Assert.IsEmpty(scheduled, "the middle request was coalesced, not built");
    Assert.IsFalse(refresh.Pending);
  }

  [TestMethod]
  public void ReturningToTheRunningQuestionAllowsItsAnswerWithoutAnotherBuild()
  {
    var refresh = new NpcStatsRefresh();
    refresh.Ask(1, 100);
    refresh.Ask(2, 100);
    Assert.IsFalse(refresh.CanPublish(1, 100));
    Assert.IsTrue(refresh.Ask(1, 100));
    Assert.IsTrue(refresh.CanPublish(1, 100));
    Assert.IsTrue(refresh.Complete(1, 100));
    Assert.IsFalse(refresh.Ask(1, 100), "an already displayed identical answer requires no new walk");
  }

  [TestMethod]
  public void AFailedReplacementCannotLeaveAPartiallyPaintedAnswerMarkedReusable()
  {
    var refresh = new NpcStatsRefresh();
    refresh.Ask(1, 100);
    refresh.Complete(1, 100);
    refresh.Ask(2, 100);
    Assert.IsTrue(refresh.Begin(2, 100));
    // Replacement publication can throw after touching some rows; it never calls Complete.
    Assert.IsTrue(refresh.Ask(1, 100), "returning to the earlier question must repaint, not trust an older cached answer");
  }

  [TestMethod]
  public void ReturningBeforeAReplacementStartsCanReuseTheStillDisplayedAnswer()
  {
    var refresh = new NpcStatsRefresh();
    refresh.Ask(1, 100);
    refresh.Complete(1, 100);
    refresh.Ask(2, 100);
    refresh.WaitFor(100);
    Assert.IsFalse(refresh.Ask(1, 100), "the replacement is only queued and has not touched the displayed answer");
    Assert.IsFalse(refresh.Pending, "a return while the original worker is finishing must not strand the read debt");
    Assert.IsFalse(refresh.Begin(2, 100), "the abandoned replacement cannot invalidate the reusable answer");
  }

  [TestMethod]
  public void AReadCompletedJustAfterPublicationCanReuseTheCoveringAnswer()
  {
    var refresh = new NpcStatsRefresh();
    refresh.Ask(1, 100);
    refresh.Complete(1, 100);
    refresh.WaitFor(100);
    Assert.IsFalse(refresh.Ask(1, 100));
    Assert.IsFalse(refresh.Pending, "deduplication must not leave a read debt with no worker left to pay it");
  }

  [TestMethod]
  public void AReplacementCaptureDoesNotInheritTheOldReadWatermarkOrAcceptItsAnswer()
  {
    var refresh = new NpcStatsRefresh();
    refresh.WaitFor(100);
    refresh.Ask(1, 100);
    var generation = refresh.Generation;
    refresh.Reset();
    Assert.IsGreaterThan(generation, refresh.Generation);
    Assert.IsFalse(refresh.Pending);
    refresh.WaitFor(0);
    Assert.IsTrue(refresh.Ask(2, 0));
    Assert.IsFalse(refresh.Complete(1, 100), "neither success nor failure of an old worker may settle a new capture");
    Assert.IsTrue(refresh.Pending);
    Assert.IsTrue(refresh.Complete(2, 0), "an honestly empty answer is also an answer");
  }

  [TestMethod]
  public void AProjectionThatMovedBeforePreparationOwesANewQuestionEvenWhileTheOldWorkerRuns()
  {
    var scheduled = new Queue<Action>();
    var gate = new SummaryBuildGate(scheduled.Enqueue);
    var refresh = new NpcStatsRefresh();
    refresh.Ask(1, 100);
    gate.Request(1, () =>
    {
      // Preparation refused a list whose live rows have already been continued by another pass.
      refresh.Invalidate();
      refresh.WaitFor(0);
      Assert.IsFalse(refresh.CanPublish(1, 100));
      Assert.IsTrue(refresh.Pending);
      Assert.IsTrue(refresh.Ask(2, 100));
      Assert.AreEqual(SummaryBuildGate.Outcome.Queued,
        gate.Request(2, () => refresh.Complete(2, 100)));
      return false;
    });

    scheduled.Dequeue()();
    Assert.IsTrue(refresh.Pending);
    scheduled.Dequeue()();
    Assert.IsFalse(refresh.Pending);
  }

  [TestMethod]
  public void ClearingThePaneInvalidatesBothDisplayedAndInFlightAnswers()
  {
    var refresh = new NpcStatsRefresh();
    refresh.Ask(1, 100);
    refresh.Complete(1, 100);
    refresh.WaitFor(200);
    refresh.Ask(2, 200);
    refresh.Invalidate();
    Assert.IsFalse(refresh.CanPublish(2, 200));
    Assert.IsFalse(refresh.Complete(2, 200));
    Assert.IsTrue(refresh.Pending, "clearing while waiting does not erase the completed-read watermark");
    Assert.IsTrue(refresh.Ask(1, 200), "a cleared answer is no longer reusable");
  }
}
