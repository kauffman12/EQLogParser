using System.Collections.Concurrent;

namespace EQLogParser;

[TestClass]
public class QuickShareImportQueueTest
{
  [TestMethod]
  public void RepeatedManualImportOfTheOnlyKeyStartsOneWorker()
  {
    var queue = new QuickShareImportQueue();
    var first = queue.Enqueue("A", "Sender", manual: true);
    Assert.IsNotNull(first);
    Assert.IsNull(queue.Enqueue("A", "Sender", manual: true));
    Assert.IsNull(queue.Enqueue("A", "Sender", manual: true));
    Assert.IsFalse(first.AutoMerge);
    Assert.IsEmpty(first.GetCharacterIds());
    Assert.IsNull(queue.Complete(first));
  }

  [TestMethod]
  public void DifferentKeysStartOnlyWhenTheirPredecessorFinishes()
  {
    var queue = new QuickShareImportQueue();
    var first = queue.Enqueue("A", "Sender", characterId: "P1");
    Assert.IsNotNull(first);
    Assert.IsNull(queue.Enqueue("B", "Other", manual: true));
    Assert.IsNull(queue.Enqueue("C", "Third", characterId: "P3"));

    var second = queue.Complete(first);
    Assert.IsNotNull(second);
    Assert.AreEqual("B", second.Key);
    Assert.IsNull(queue.Complete(first), "A repeated completion must not restart the active successor.");
    var third = queue.Complete(second);
    Assert.IsNotNull(third);
    Assert.AreEqual("C", third.Key);
    Assert.IsNull(queue.Complete(third));
  }

  [TestMethod]
  public void MultipleCharactersJoinBothWaitingAndActiveKeysWithoutNewWorkers()
  {
    var queue = new QuickShareImportQueue();
    var first = queue.Enqueue("A", "Sender", characterId: "P1");
    Assert.IsNotNull(first);
    Assert.IsNull(queue.Enqueue("B", "Other", characterId: "P2"));
    Assert.IsNull(queue.Enqueue("B", "Other", characterId: "P3"));
    Assert.IsNull(queue.Enqueue("A", "Sender", characterId: "P4"));
    CollectionAssert.AreEquivalent(new[] { "P1", "P4" }, first.GetCharacterIds().ToArray());

    var second = queue.Complete(first);
    Assert.IsNotNull(second);
    Assert.IsNull(queue.Enqueue("B", "Other", characterId: "P5"));
    Assert.IsNull(queue.Enqueue("B", "Other", characterId: "P2"));
    CollectionAssert.AreEquivalent(new[] { "P2", "P3", "P5" }, second.GetCharacterIds().ToArray());
    Assert.IsNull(queue.Complete(second));
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void DuplicateRequestsDoNotRewriteTheFirstSendersImportPolicy(bool manualFirst)
  {
    var queue = new QuickShareImportQueue();
    var first = queue.Enqueue("A", "Original", autoMerge: !manualFirst, isTrigger: true,
      characterId: manualFirst ? null : "P1", manual: manualFirst);
    Assert.IsNotNull(first);
    Assert.IsNull(queue.Enqueue("A", "Different", autoMerge: manualFirst, characterId: "P2", manual: !manualFirst));
    Assert.AreEqual("Original", first.Sender);
    Assert.AreEqual(!manualFirst, first.AutoMerge);
    Assert.IsTrue(first.IsTrigger);
    Assert.IsTrue(first.GetCharacterIds().Contains("P2"));
    Assert.IsNull(queue.Complete(first));
  }

  [TestMethod]
  public void TheSameKeyCanBeExplicitlyImportedAgainAfterCompletion()
  {
    var queue = new QuickShareImportQueue();
    var first = queue.Enqueue("A", "Sender", manual: true);
    Assert.IsNotNull(first);
    Assert.IsNull(queue.Complete(first));
    var replacement = queue.Enqueue("A", "Sender", manual: true);
    Assert.IsNotNull(replacement);
    Assert.AreNotSame(first, replacement);
    Assert.IsNull(queue.Complete(first));
    Assert.IsNull(queue.Enqueue("B", "Other", manual: true));
    var next = queue.Complete(replacement);
    Assert.IsNotNull(next, "A stale completion must not retire a new import of the same key.");
    Assert.AreEqual("B", next.Key);
    Assert.IsNull(queue.Complete(next));
  }

  [TestMethod]
  public void UnknownCompletionCannotReleaseTheActiveWorker()
  {
    var queue = new QuickShareImportQueue();
    var active = queue.Enqueue("A", "Sender", manual: true);
    Assert.IsNotNull(active);
    Assert.IsNull(queue.Complete(new QuickShareImport("A", "Sender", false, false)));
    Assert.IsNull(queue.Enqueue("B", "Other", manual: true));
    var next = queue.Complete(active);
    Assert.IsNotNull(next);
    Assert.AreEqual("B", next.Key);
    Assert.IsNull(queue.Complete(next));
  }

  [TestMethod]
  public void AutomaticBacklogIsBoundedButDuplicatesStillJoinAndManualImportsAreAccepted()
  {
    var queue = new QuickShareImportQueue();
    var first = queue.Enqueue("0", "Sender", characterId: "P1");
    Assert.IsNotNull(first);
    for (var i = 1; i < QuickShareImportQueue.MaxAutomaticPending; i++)
    {
      Assert.IsNull(queue.Enqueue(i.ToString(), "Sender", characterId: "P1"));
    }

    Assert.IsNull(queue.Enqueue("Dropped", "Sender", characterId: "P1"));
    Assert.IsNull(queue.Enqueue("0", "Sender", characterId: "P2"));
    Assert.IsNull(queue.Enqueue("Manual", "Sender", manual: true));
    CollectionAssert.AreEquivalent(new[] { "P1", "P2" }, first.GetCharacterIds().ToArray());
    var keys = new List<string> { first.Key };
    for (var next = queue.Complete(first); next is not null; next = queue.Complete(next))
    {
      keys.Add(next.Key);
    }

    Assert.HasCount(QuickShareImportQueue.MaxAutomaticPending + 1, keys);
    Assert.AreEqual("Manual", keys[^1]);
    CollectionAssert.DoesNotContain(keys, "Dropped");
    Assert.IsNotNull(queue.Enqueue("Fresh", "Sender", characterId: "P1"));
  }

  [TestMethod]
  public void CharacterSnapshotsStayStableWhileLaterReadersJoin()
  {
    var queue = new QuickShareImportQueue();
    var active = queue.Enqueue("A", "Sender", characterId: "P1");
    Assert.IsNotNull(active);
    var before = active.GetCharacterIds();
    Assert.IsNull(queue.Enqueue("A", "Sender", characterId: "P2"));
    CollectionAssert.AreEquivalent(new[] { "P1" }, before.ToArray());
    CollectionAssert.AreEquivalent(new[] { "P1", "P2" }, active.GetCharacterIds().ToArray());
    before.Clear();
    Assert.HasCount(2, active.GetCharacterIds());
  }

  [TestMethod]
  public void ConcurrentSightingsAndManualRequestsClaimOnlyOneWorker()
  {
    var queue = new QuickShareImportQueue();
    var starts = new ConcurrentBag<QuickShareImport>();
    Parallel.For(0, 100, i =>
    {
      if (queue.Enqueue("A", "Sender", characterId: $"P{i}", manual: (i % 2) == 0) is { } work)
      {
        starts.Add(work);
      }
    });

    Assert.HasCount(1, starts);
    var active = starts.Single();
    Assert.HasCount(100, active.GetCharacterIds());
    Assert.IsNull(queue.Complete(active));
  }

  [TestMethod]
  public void CompletionRacingANewKeyHandsOutThatKeyExactlyOnce()
  {
    var queue = new QuickShareImportQueue();
    var active = queue.Enqueue("A", "Sender", manual: true);
    Assert.IsNotNull(active);
    var starts = new ConcurrentBag<QuickShareImport>();
    Parallel.Invoke(() =>
    {
      if (queue.Complete(active) is { } work) starts.Add(work);
    }, () =>
    {
      if (queue.Enqueue("B", "Other", manual: true) is { } work) starts.Add(work);
    });

    Assert.HasCount(1, starts);
    var successor = starts.Single();
    Assert.AreEqual("B", successor.Key);
    Assert.IsNull(queue.Complete(successor));
  }
}
