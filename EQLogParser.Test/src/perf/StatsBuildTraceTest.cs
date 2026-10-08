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
}
