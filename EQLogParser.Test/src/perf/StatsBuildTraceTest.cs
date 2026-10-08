using EQLogParser;

namespace EQLogParser;

/*
 * The doors into the summary boards, made countable (EQLogParser.Core/src/perf/StatsBuildTrace.cs).
 *
 * Reported from the field: load a large capture, select all immediately, and the damage grid fills THREE times; do the same click
 * a minute later and it fills once. That is a sentence about doors, not about one door — the fight list's selection goes through
 * MainWindow's single-flight gate, while each summary pane rebuilds from its own triggers (a dial, the pane being shown, the hourglass
 * timer that finishes after the saved time-window values move the dependency properties at load). A trace that only printed durations
 * could not tell those apart, so it prints who asked and warns when two builds run over each other; this test pins the two properties
 * the log lines depend on, because a trace that lies is worse than no trace: the reader follows it into the wrong file.
 */
[TestClass]
public sealed class StatsBuildTraceTest
{
  [TestMethod]
  public void EveryBuildTakesANumberAndLeavesWhenItIsClosed()
  {
    var before = StatsBuildTrace.TotalBuilds;

    var handle = StatsBuildTrace.Begin("damage", "test door");
    Assert.AreEqual(before + 1, handle.Seq, "builds are numbered across the whole app so lines order readably");
    Assert.AreEqual(1, StatsBuildTrace.ActiveBuilds);

    StatsBuildTrace.End(handle, "npcs=0");
    Assert.AreEqual(0, StatsBuildTrace.ActiveBuilds, "a closed build stops counting; a leaked handle would hide the next overlap");
    Assert.AreEqual(before + 1, StatsBuildTrace.TotalBuilds);
  }

  /*
   * The phase split, reported. "The damage board takes four seconds" is not actionable; "walk 2810 ms present 940 ms"
   * says what a per-name accumulator buys and what it does not — the number that decides whether an edit refresh can
   * ever be milliseconds (it re-runs `present` alone). Pinned here so the builder cannot lose the reporting quietly.
   */
  [TestMethod]
  public void PhasesAreReportedOnTheFinishedLine()
  {
    var handle = StatsBuildTrace.Begin("damage", "phase test");
    StatsBuildTrace.Stage(handle, "walk");
    StatsBuildTrace.Stage(handle, "present");

    // A stage after End belongs to no build: it must not throw and must not land on anybody's line.
    StatsBuildTrace.End(handle, "npcs=1");
    StatsBuildTrace.Stage(handle, "late");

    var line = StatsBuildTrace.LastFinishedLineOf("damage");
    Assert.IsNotNull(line);
    StringAssert.Contains(line, "walk ", "the record walk names its own cost");
    StringAssert.Contains(line, "present ", "the arithmetic over the rows names its own cost");
    Assert.IsFalse(line.Contains("late"), "a stage opened after the build closed is not reported as part of it");
  }

  [TestMethod]
  public void ADoorThatDoesNotNameItselfIsCounted()
  {
    var before = StatsBuildTrace.UnlabelledBuilds;

    // The whole point of the label: an anonymous pass over two million records is what gets defended as "probably necessary".
    var anonymous = StatsBuildTrace.Begin("damage", null);
    Assert.AreEqual(StatsBuildTrace.UnlabelledDoor, anonymous.Door);
    Assert.AreEqual(before + 1, StatsBuildTrace.UnlabelledBuilds);
    StatsBuildTrace.End(anonymous);

    var labelled = StatsBuildTrace.Begin("damage", "derived [SelectCommand] select all");
    Assert.AreEqual(before + 1, StatsBuildTrace.UnlabelledBuilds, "a named door costs nothing on that counter");
    StatsBuildTrace.End(labelled);
  }

  [TestMethod]
  public void TwoThreadsBuildingAtOnceAreReportedAsOverlap()
  {
    var before = StatsBuildTrace.OverlapBuilds;
    var first = StatsBuildTrace.Begin("damage", "derived selection");

    // A second door on another thread, while the first is still inside: this is the shape "it built three times" actually is,
    // since every pane's own rebuild arrives on its own Task.Run.
    var other = new Thread(() =>
    {
      var second = StatsBuildTrace.Begin("damage", "pane dial");
      Assert.AreEqual(1, second.OverlappedWith, "the second build knows it started over another");
      StatsBuildTrace.End(second);
    });

    other.Start();
    other.Join(TimeSpan.FromSeconds(5));

    Assert.AreEqual(before + 1, StatsBuildTrace.OverlapBuilds);
    StatsBuildTrace.End(first);
    Assert.AreEqual(0, StatsBuildTrace.ActiveBuilds);
  }

  [TestMethod]
  public void ANestedBuildOnTheSameThreadIsOneDoorRatherThanAnOverlap()
  {
    /*
     * HealingStatsBuilder.RebuildTotalStats calls BuildTotalStats on the calling thread while holding its lock. If nesting counted as
     * overlap, every healing rebuild from a pane dial would warn, and a warning that fires on ordinary work is a warning nobody reads —
     * the one that matters ("two doors really did collide") would be lost in it.
     */
    var before = StatsBuildTrace.OverlapBuilds;

    var outer = StatsBuildTrace.Begin("healing", "healing pane options [time window]", "rebuild");
    var inner = StatsBuildTrace.Begin("healing", "healing pane options [time window]");

    Assert.AreEqual(0, inner.OverlappedWith, "the same thread nesting is one door, not two colliding");
    Assert.AreEqual(before, StatsBuildTrace.OverlapBuilds);

    StatsBuildTrace.End(inner);
    StatsBuildTrace.End(outer);
    Assert.AreEqual(0, StatsBuildTrace.ActiveBuilds);
  }

  [TestMethod]
  public void AThrowingBuildStillClosesWhenTheCallerUsesFinally()
  {
    // Production wraps each builder body in try/finally, so a throw cannot leave the trace claiming a build is running: an
    // always-"active" build would make every later line report an overlap that ended minutes ago.
    var before = StatsBuildTrace.TotalBuilds;
    try
    {
      var handle = StatsBuildTrace.Begin("tanking", "test door");
      try { throw new InvalidOperationException("builder died"); }
      finally { StatsBuildTrace.End(handle); }
    }
    catch (InvalidOperationException) { }

    Assert.AreEqual(before + 1, StatsBuildTrace.TotalBuilds);
    Assert.AreEqual(0, StatsBuildTrace.ActiveBuilds);
  }

  /*
   * What an ordinary log holds. The full line for every build used to print at Info, which on a live pull is several lines a
   * second restating one door - thousands of identical sentences per raid night, and the reader still has to scan them to find
   * the place where the door CHANGED. So a build's line is Debug, and Info carries the door's first appearance plus a periodic
   * restatement with what was held back. Pinned here because the whole point is that the signal survives the demotion: a door
   * that started building is always visible without Debug, and a repetition never floods.
   */
  [TestInitialize]
  public void Setup() => _interval = StatsBuildTrace.EchoIntervalSeconds;

  [TestCleanup]
  public void Cleanup() => StatsBuildTrace.EchoIntervalSeconds = _interval;

  private double _interval;

  [TestMethod]
  public void ARepeatingDoorEchoesOnceAndCountsWhatItHeldBack()
  {
    StatsBuildTrace.EchoIntervalSeconds = 1_000_000;   // no clock re-echo in this test; only the door may speak

    for (var i = 1; i <= 3; i++)
    {
      var handle = StatsBuildTrace.Begin("damage", $"derived [SelectCommand] stamp {i} fights {i}");
      StatsBuildTrace.End(handle);
    }

    Assert.AreEqual("derived [SelectCommand]", StatsBuildTrace.LastEchoOf("damage"),
      "the door word - not the numbers that move within it - is what the log echoes");
    Assert.AreEqual(2, StatsBuildTrace.SuppressedSinceEcho("damage"),
      "the two repeats were held back at Debug rather than printed again");

    // Nothing is lost by holding them: the last build's own line still reads with its own counters.
    var line = StatsBuildTrace.LastFinishedLineOf("damage");
    Assert.IsNotNull(line);
    StringAssert.Contains(line, "fights 3", "the suppressed build's full line is still what a Debug reader retrieves");
  }

  [TestMethod]
  public void ANewDoorIsReportedImmediatelyEvenIfTheLastOneWasLoud()
  {
    StatsBuildTrace.EchoIntervalSeconds = 1_000_000;

    var first = StatsBuildTrace.Begin("healing", "derived [SelectCommand] stamp 1 fights 1");
    StatsBuildTrace.End(first);
    var repeat = StatsBuildTrace.Begin("healing", "derived [SelectCommand] stamp 2 fights 2");
    StatsBuildTrace.End(repeat);

    var other = StatsBuildTrace.Begin("healing", "healing pane [ContentLoaded]");
    StatsBuildTrace.End(other);

    Assert.AreEqual("healing pane [ContentLoaded]", StatsBuildTrace.LastEchoOf("healing"),
      "a door nobody has seen for that board gets its line right away - this is the one the reader is waiting for");
    Assert.AreEqual(0, StatsBuildTrace.SuppressedSinceEcho("healing"), "counting starts over for the new door");
  }

  [TestMethod]
  public void ASilentDoorStillRestatesItselfOnItsOwnClock()
  {
    // A door that keeps building for an hour must not vanish from a log the way it would with pure change-detection:
    // the periodic line, with its suppressed count, is what proves the app was alive and building all along.
    StatsBuildTrace.EchoIntervalSeconds = 0;

    var a = StatsBuildTrace.Begin("tanking", "derived [SettleTick] stamp 1 fights 1");
    StatsBuildTrace.End(a);
    Assert.AreEqual(0, StatsBuildTrace.SuppressedSinceEcho("tanking"));

    var b = StatsBuildTrace.Begin("tanking", "derived [SettleTick] stamp 2 fights 2");
    StatsBuildTrace.End(b);
    Assert.AreEqual(0, StatsBuildTrace.SuppressedSinceEcho("tanking"),
      "the interval elapsed, so the second build echoed instead of counting up");
  }
}
