using EQLogParser;

namespace EQLogParser;

/*
 * The single-flight rule in front of the summary boards (EQLogParser.Core/src/ui/SummaryBuildGate.cs), tested the way
 * SelectionSettle and DeriveCadence are: the scheduler arrives as a delegate, so "queued", "running" and "skipped" are
 * observable rather than inferred from timing.
 *
 * What this is for is a click. Materializing a selection allocates one record per selected fact and a whole-capture select-all
 * measures in seconds; announcements come from the command itself, the menu closing, the settle timer and every derive pass whose
 * content stamp moved. Before the gate, each announcement spawned its own task, so "select all" could mean two full
 * materializations allocated simultaneously (the builders' own lock serialised the WORK but not the allocation), and with a
 * whole-capture selection up during a pull it meant one build per pass with no bound — plus one line in the player's log each.
 */
[TestClass]
public sealed class SummaryBuildGateTest
{
  // A scheduler that hands out work instead of running it: the test decides when a build runs, which is the only way to see
  // the difference between "queued behind the run" and "ran alongside it". The gate marks itself busy when it STARTS a
  // request (before scheduling), so an undrained queue is exactly "a build in flight".
  private sealed class ManualScheduler
  {
    private readonly List<Action> _handed = [];

    internal int Ran { get; private set; }

    internal void Schedule(Action work) => _handed.Add(work);

    // Runs everything handed, including whatever a completion queues. A build that throws does not stop the drain (that is
    // the point of one of the tests below); the first exception is rethrown at the end so the test still sees it.
    internal void Drain()
    {
      Exception? first = null;
      while (_handed.Count > 0)
      {
        var work = _handed[0];
        _handed.RemoveAt(0);
        Ran++;
        try { work(); } catch (Exception ex) { first ??= ex; }
      }

      if (first is not null) throw first;
    }
  }

  private static (SummaryBuildGate gate, ManualScheduler scheduler, List<long> built) Fixture()
  {
    var scheduler = new ManualScheduler();
    var built = new List<long>();
    return (new SummaryBuildGate(scheduler.Schedule), scheduler, built);
  }

  [TestMethod]
  public void TheFirstRequestRunsImmediately()
  {
    var (gate, scheduler, built) = Fixture();

    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(10, () => built.Add(10)));
    Assert.IsTrue(gate.IsRunning, "a started request holds the gate until its work finishes");

    scheduler.Drain();
    CollectionAssert.AreEqual(new[] { 10L }, built);
    Assert.IsFalse(gate.IsRunning, "a finished build leaves the gate idle");
  }

  [TestMethod]
  public void ARequestWhileOneRunsWaitsRatherThanRunningAlongsideIt()
  {
    var (gate, scheduler, built) = Fixture();

    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(10, () => built.Add(10)));
    Assert.AreEqual(SummaryBuildGate.Outcome.Queued, gate.Request(20, () => built.Add(20)));
    Assert.AreEqual(0, built.Count, "a second materialization must not start while the first is in flight");

    scheduler.Drain();
    CollectionAssert.AreEqual(new[] { 10L, 20L }, built, "the queued request runs behind it, not never");
  }

  [TestMethod]
  public void OnlyTheNewestWaitingRequestSurvives()
  {
    var (gate, scheduler, built) = Fixture();
    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(10, () => built.Add(10)));

    // Three more asks while the first is running - an operator clicking down the list. The older two describe selections
    // nobody is looking at any more; running them first would only delay the answer and hold their records in memory.
    Assert.AreEqual(SummaryBuildGate.Outcome.Queued, gate.Request(20, () => built.Add(20)));
    Assert.AreEqual(SummaryBuildGate.Outcome.Queued, gate.Request(30, () => built.Add(30)));
    Assert.AreEqual(SummaryBuildGate.Outcome.Queued, gate.Request(40, () => built.Add(40)));

    scheduler.Drain();
    CollectionAssert.AreEqual(new[] { 10L, 40L }, built);
    Assert.IsTrue(gate.CollapsedCount >= 2, "the superseded requests are counted, not silently lost");
  }

  [TestMethod]
  public void ARepeatOfWhatWasJustBuiltDoesNotRun()
  {
    var (gate, scheduler, built) = Fixture();
    gate.Request(10, () => built.Add(10));
    scheduler.Drain();

    // Same rows, same content stamp, same filters: the answer is already on screen. This is the duplicated announcement -
    // a command and the settle timer behind it, a menu close releasing a parked change that turned out to say the same thing.
    Assert.AreEqual(SummaryBuildGate.Outcome.SkippedSame, gate.Request(10, () => built.Add(10)));
    scheduler.Drain();
    CollectionAssert.AreEqual(new[] { 10L }, built);
    Assert.AreEqual(1, scheduler.Ran, "the second ask cost nothing at all, not a second pass");

    // And a different key after that still builds: skipping is equality of inputs, not a cooldown.
    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(11, () => built.Add(11)));
  }

  [TestMethod]
  public void ARequestMatchingTheBuildInFlightIsDroppedNotQueued()
  {
    var (gate, scheduler, built) = Fixture();
    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(10, () => built.Add(10)));

    // The very same question, asked again mid-build: what is running answers it, so a follow-up would be pure duplication.
    Assert.AreEqual(SummaryBuildGate.Outcome.SkippedSame, gate.Request(10, () => built.Add(10)));
    scheduler.Drain();
    CollectionAssert.AreEqual(new[] { 10L }, built);
  }

  [TestMethod]
  public void AThrowingBuildReturnsTheGateToIdle()
  {
    /*
     * The failure mode this pins is the loudest one in this pane's history: a runner left marked busy means every later request
     * queues behind a build that will never finish, so the boards stop updating for the rest of the session and it reads as
     * "the stats froze" rather than as an error. Production catches inside its own work; the gate still owes the same law.
     */
    var scheduler = new ManualScheduler();
    var gate = new SummaryBuildGate(scheduler.Schedule);

    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(10, () => throw new InvalidOperationException("builder died")));
    try { scheduler.Drain(); } catch (InvalidOperationException) { }

    Assert.IsFalse(gate.IsRunning, "a pass that threw must not leave the gate holding a running build");

    var ran = false;
    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(20, () => ran = true));
    scheduler.Drain();
    Assert.IsTrue(ran, "the next announcement still gets its boards");
  }

  [TestMethod]
  public void AQueuedRequestIsStillRunAfterAFailedBuild()
  {
    var scheduler = new ManualScheduler();
    var gate = new SummaryBuildGate(scheduler.Schedule);
    var secondRan = false;

    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(10, () => throw new InvalidOperationException("builder died")));
    Assert.AreEqual(SummaryBuildGate.Outcome.Queued, gate.Request(20, () => secondRan = true));

    try { scheduler.Drain(); } catch (InvalidOperationException) { }

    Assert.IsTrue(secondRan, "a debt owed to a newer selection is paid whatever the failed build did");
    Assert.IsFalse(gate.IsRunning);
  }

  [TestMethod]
  public void AFailedBuildIsNotRememberedAsAnAnswer()
  {
    /*
     * The quiet half of the same failure. Production's board build CATCHES ("Derived damage summary error": the panes keep showing
     * whatever they showed before), so a failed pass can look to the gate like a finished one - and `_doneKey` is precisely the thing
     * that makes an identical later ask free. Marking the attempt as an answer meant one failed select-all poisoned that selection:
     * clicking away and back, or pressing Refresh on the same rows with nothing else moved, was answered "already built" while the
     * grids still held stale numbers, and no later gesture could get them back short of changing a filter.
     *
     * Dedupe on what was computed, never on what was attempted.
     */
    var scheduler = new ManualScheduler();
    var gate = new SummaryBuildGate(scheduler.Schedule);
    var builds = 0;

    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(10, () => { builds++; throw new InvalidOperationException("builder died"); }));
    try { scheduler.Drain(); } catch (InvalidOperationException) { }
    Assert.AreEqual(1, builds);

    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(10, () => builds++),
      "the same selection after a failure is owed its boards - the failed pass produced no answer to duplicate");
    scheduler.Drain();
    Assert.AreEqual(2, builds, "the retry builds rather than being skipped as already answered");

    // The law on the other side still holds: what COMPLETED answers an identical ask for free.
    Assert.AreEqual(SummaryBuildGate.Outcome.SkippedSame, gate.Request(10, () => builds++));
    scheduler.Drain();
    Assert.AreEqual(2, builds, "a build that finished still answers the same question without working");
  }

  [TestMethod]
  public void WalkingBackToTheSelectionBeingBuiltCancelsTheAbandonedOne()
  {
    /*
     * The sequence behind "the stats panel shows somebody else's fight": select A, drag to B, click back on A. A answers the newest
     * question for free, but until now the queued B stayed behind it and painted its boards over A's grid a second later - so the
     * newest request won at ARRIVAL time and lost at completion time. Law 2 covers free answers too.
     */
    var (gate, scheduler, built) = Fixture();

    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(10, () => built.Add(10)));
    Assert.AreEqual(SummaryBuildGate.Outcome.Queued, gate.Request(20, () => built.Add(20)));
    Assert.AreEqual(SummaryBuildGate.Outcome.SkippedSame, gate.Request(10, () => built.Add(10)),
      "the build in flight already answers the selection the operator walked back to");

    scheduler.Drain();
    CollectionAssert.AreEqual(new[] { 10L }, built,
      "the abandoned selection may not build after the one on screen - newest wins, including when the newest is free");
  }

  [TestMethod]
  public void WalkingBackToTheSelectionJustBuiltCancelsWhatWasQueuedBehindAnother()
  {
    var (gate, scheduler, built) = Fixture();

    gate.Request(10, () => built.Add(10));
    scheduler.Drain();                                                          // A answered; _doneKey = 10
    Assert.AreEqual(SummaryBuildGate.Outcome.Started, gate.Request(20, () => built.Add(20)));
    Assert.AreEqual(SummaryBuildGate.Outcome.Queued, gate.Request(30, () => built.Add(30)));

    Assert.AreEqual(SummaryBuildGate.Outcome.SkippedSame, gate.Request(10, () => built.Add(10)));

    scheduler.Drain();
    /*
     * C is dropped - it is superseded and unstarted. B already began and the scheduler cannot un-start a build; that half of the
     * hazard (B painting last over a grid that answers A) belongs to the board pipeline's result-side staleness check, not here.
     */
    CollectionAssert.AreEqual(new[] { 10L, 20L }, built, "queued work for abandoned selections goes away; running work does not");
  }
}
