namespace EQLogParser;

[TestClass]
public sealed class MeterBuildRetryTest
{
  [TestMethod]
  public void RepeatedInvalidationDefersAfterThreeAttempts()
  {
    var attempts = 0;
    var result = MeterBuildRetry.Run(() =>
    {
      attempts++;
      return MeterBuildResult.Deferred;
    });

    Assert.AreEqual(3, attempts, "one refresh must not chase changing inputs indefinitely");
    Assert.IsTrue(result.IsDeferred);
    Assert.IsNull(result.Stats);
  }

  [TestMethod]
  [DataRow(1)]
  [DataRow(2)]
  [DataRow(3)]
  public void AStableAttemptAnswersWithoutSpendingTheRemainingBudget(int stableAt)
  {
    var stats = new DamageOverlayStats();
    var attempts = 0;
    var result = MeterBuildRetry.Run(() => ++attempts < stableAt
      ? MeterBuildResult.Deferred : MeterBuildResult.Completed(stats));

    Assert.AreEqual(stableAt, attempts);
    Assert.IsFalse(result.IsDeferred);
    Assert.AreSame(stats, result.Stats);
  }

  [TestMethod]
  public void AVerifiedEmptyScopeIsAnAnswerNotADeferral()
  {
    var attempts = 0;
    var result = MeterBuildRetry.Run(() =>
    {
      attempts++;
      return MeterBuildResult.Completed();
    });

    Assert.AreEqual(1, attempts);
    Assert.IsFalse(result.IsDeferred, "an actual empty result must still clear the old board");
    Assert.IsNull(result.Stats);
  }

  [TestMethod]
  public void CancellationBeforeAnAskDoesNotStartScanning()
  {
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var attempts = 0;
    Assert.ThrowsExactly<OperationCanceledException>(() => MeterBuildRetry.Run(() =>
    {
      attempts++;
      return MeterBuildResult.Deferred;
    }, cancellation.Token));
    Assert.AreEqual(0, attempts);
  }

  [TestMethod]
  [DataRow(1, false)]
  [DataRow(3, false)]
  [DataRow(1, true)]
  public void CancellationDuringAnAttemptCannotBecomeAnAnswerOrDeferral(int cancelAt, bool completed)
  {
    using var cancellation = new CancellationTokenSource();
    var attempts = 0;
    Assert.ThrowsExactly<OperationCanceledException>(() => MeterBuildRetry.Run(() =>
    {
      if (++attempts == cancelAt) cancellation.Cancel();
      return completed ? MeterBuildResult.Completed() : MeterBuildResult.Deferred;
    }, cancellation.Token));
    Assert.AreEqual(cancelAt, attempts, "Reset and close must not leave another scan queued in the same ask");
  }

  [TestMethod]
  public void AThrowingAttemptUsesTheExistingFailurePathInsteadOfRetrying()
  {
    var attempts = 0;
    Assert.ThrowsExactly<InvalidOperationException>(() => MeterBuildRetry.Run(() =>
    {
      attempts++;
      throw new InvalidOperationException("counting failed");
    }));
    Assert.AreEqual(1, attempts);
  }

  [TestMethod]
  [DataRow(0, 1)]
  [DataRow(10, 2)]
  public void OrdinaryOpeningAndTimedBoundInitializationFitWithinOneAsk(int mode, int expectedAttempts)
  {
    var lifecycle = new DamageOverlayLifecycle();
    var row = new DerivedFight { Name = "a rat", BeginTime = 100, LastTime = 100, DamageTotal = 600, FactEnd = 1 };
    var attempts = 0;
    var from = double.NaN;
    var result = MeterBuildRetry.Run(() =>
    {
      attempts++;
      if (!lifecycle.TrySelect(1, [row], 100, mode, new(1, 100), 1, out var scope))
        return MeterBuildResult.Deferred;
      Assert.HasCount(1, scope.Rows);
      from = scope.FromTime;
      return MeterBuildResult.Completed();
    });

    Assert.IsFalse(result.IsDeferred);
    Assert.AreEqual(expectedAttempts, attempts, "initializing a timed bound intentionally retries once");
    Assert.AreEqual(mode == 0 ? double.NegativeInfinity : 100 - mode, from);
  }

  [TestMethod]
  public void DeferralCannotCommitAnInitializedBoundOrSpendTheCheckpointRevision()
  {
    var checkpoint = new MeterLifecycleCheckpoint();
    var owner = checkpoint.Open();
    var work = checkpoint.Read(owner);
    var row = new DerivedFight { Name = "a rat", BeginTime = 100, LastTime = 100, DamageTotal = 600, FactEnd = 1 };
    var result = MeterBuildRetry.Run(() =>
    {
      work.State.TrySelect(1, [row], 100, 10, new(1, 100), 1, out _);
      return MeterBuildResult.Deferred;
    });

    Assert.AreEqual(90, work.State.SelectCandidates(1, [row], 10).FromTime);
    Assert.IsFalse(checkpoint.TryCommit(work, result));
    var next = checkpoint.Read(owner);
    Assert.AreEqual(work.Revision, next.Revision, "deferral is not a committed publication");
    Assert.AreEqual(double.NegativeInfinity, next.State.SelectCandidates(1, [row], 10).FromTime);

    var attempts = 0;
    var recovered = MeterBuildRetry.Run(() =>
    {
      attempts++;
      return next.State.TrySelect(1, [row], 100, 10, new(1, 100), 1, out _)
        ? MeterBuildResult.Completed() : MeterBuildResult.Deferred;
    });
    Assert.AreEqual(2, attempts, "a fresh ask can initialize and finish without inheriting discarded state");
    Assert.IsTrue(checkpoint.TryCommit(next, recovered));
  }

  [TestMethod]
  public void DeferralYieldsToTheNextRequestWithoutRenewingThePresentation()
  {
    var gate = new MeterRefreshGate();
    var presentation = new MeterPresentationState();
    presentation.Publish(1, 0, 10);
    Assert.IsTrue(gate.Request(0));
    Assert.IsFalse(gate.Request(1));
    var deferred = MeterBuildRetry.Run(() => MeterBuildResult.Deferred);
    Assert.IsFalse(gate.Complete(5, deferred.IsDeferred), "even a long ask must not immediately drain another retry budget");
    Assert.IsTrue(presentation.IsFresh(1, 0, 9), "deferral must not clear a still-valid presentation");
    Assert.IsFalse(presentation.IsFresh(1, 0, 11), "deferral must not prolong the old deadline either");

    Assert.IsTrue(gate.Request(11));
    var stats = new DamageOverlayStats();
    var recovered = MeterBuildRetry.Run(() => MeterBuildResult.Completed(stats));
    Assert.IsFalse(recovered.IsDeferred);
    Assert.AreSame(stats, recovered.Stats);
    Assert.IsFalse(gate.Complete(11.1, recovered.IsDeferred));
  }
}
