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
  public void OverlappingStartsAreCountedOnceWarnedUntilTheBoardsGoQuiet()
  {
    /*
     * The warning exists to say "two doors did not talk", but on a live pull with the boards open several doors ask independently all
     * night, and a per-build WARN turned an operator's log into thousands of lines restating "a raid is happening". So: every collision
     * counts forever, and only the one that opens an episode speaks. Pinned here by the counters, since the test assemblies do not carry
     * a log appender — `OverlapsInCurrentEpisode` is exactly "how many collisions are held behind the warning already printed".
     */
    var first = StatsBuildTrace.Begin("damage", "derived selection");
    Assert.AreEqual(0, StatsBuildTrace.OverlapsInCurrentEpisode, "a lone build opens no episode");

    for (var i = 0; i < 2; i++)
    {
      var door = i == 0 ? "healing pane shown" : "tanking pane dial";
      var visitor = new Thread(() =>
      {
        var build = StatsBuildTrace.Begin("damage", door);
        Assert.AreEqual(1, build.OverlappedWith, $"{door} started over the build still inside");
        StatsBuildTrace.End(build);
      });
      visitor.Start();
      visitor.Join(TimeSpan.FromSeconds(5));
    }

    Assert.AreEqual(2, StatsBuildTrace.OverlapsInCurrentEpisode,
      "both collisions are counted behind the one warning, while the episode is open");

    StatsBuildTrace.End(first);
    Assert.AreEqual(0, StatsBuildTrace.ActiveBuilds);
    Assert.AreEqual(0, StatsBuildTrace.OverlapsInCurrentEpisode, "quiet closes the episode and re-arms the warning");
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
   * The group law behind the board fan-out (docs/improve-stat-building §3, MainWindow.BuildBoards): ONE request that builds
   * damage, tanking and healing at once is one door wearing three hats, so its three builds must not warn about each other — while
   * a genuinely foreign build under them still does. Pinned in both directions on purpose: silence for the siblings is easy to get,
   * and so is silencing everything, which is the failure that costs the reader the one warning this class exists to print.
   */
  [TestMethod]
  public void TheThreeBoardsOfOneRequestAreSiblingsRatherThanAnOverlap()
  {
    var before = StatsBuildTrace.OverlapBuilds;
    var group = StatsBuildTrace.NextGroupId();

    var damage = StatsBuildTrace.Begin("damage", "derived [SelectCommand]", "full", group);
    Assert.AreEqual(group, damage.Group, "a caller-supplied group is the one the handle reports");

    // The other two boards of the SAME request, each on its own worker — the shape a fanned-out select-all actually is.
    var others = new Thread(() =>
    {
      var tanking = StatsBuildTrace.Begin("tanking", "derived [SelectCommand]", "full", group);
      var healing = StatsBuildTrace.Begin("healing", "derived [SelectCommand]", "full", group);
      Assert.AreEqual(0, tanking.OverlappedWith, "the second board of one request does not start OVER anything");
      Assert.AreEqual(0, healing.OverlappedWith, "nor does the third");
      StatsBuildTrace.End(healing);
      StatsBuildTrace.End(tanking);
    });

    others.Start();
    others.Join(TimeSpan.FromSeconds(5));

    Assert.AreEqual(before, StatsBuildTrace.OverlapBuilds, "three siblings cost zero overlap warnings");
    StatsBuildTrace.End(damage);
    Assert.AreEqual(0, StatsBuildTrace.ActiveBuilds);
  }

  [TestMethod]
  public void AForeignBuildUnderAFannedOutRequestStillWarns()
  {
    /*
     * The half that must NOT be silenced: a pane's own dial rebuild (its own door, its own task, no group in common) landing under a
     * fanned-out selection is two doors that did not talk to each other, and the whole reason this trace exists is to say so.
     */
    var before = StatsBuildTrace.OverlapBuilds;
    var group = StatsBuildTrace.NextGroupId();

    var damage = StatsBuildTrace.Begin("damage", "derived [SettleTick]", "full", group);
    var tanking = StatsBuildTrace.Begin("tanking", "derived [SettleTick]", "full", group);
    Assert.AreEqual(0, tanking.OverlappedWith);

    var foreign = new Thread(() =>
    {
      // A pane's door: no group at all, which means "I stand alone".
      var dial = StatsBuildTrace.Begin("healing", "healing pane options [time window]", "rebuild");
      Assert.AreEqual(2, dial.OverlappedWith, "the foreign build sees both siblings and says so");
      StatsBuildTrace.End(dial);
    });

    foreign.Start();
    foreign.Join(TimeSpan.FromSeconds(5));

    Assert.AreEqual(before + 1, StatsBuildTrace.OverlapBuilds, "one foreign arrival, one warning — the group did not eat it");
    StatsBuildTrace.End(tanking);
    StatsBuildTrace.End(damage);
    Assert.AreEqual(0, StatsBuildTrace.ActiveBuilds);
  }

  [TestMethod]
  public void ARequestThatDoesNotAskForAGroupStandsAlone()
  {
    /*
     * "Every existing call site is bit-for-bit unchanged" is a law about the DEFAULT: no group passed = a fresh private one, so two
     * ungrouped builds are foreign to each other exactly as they were before groups existed.
     */
    var first = StatsBuildTrace.Begin("damage", "first door");
    var second = StatsBuildTrace.Begin("tanking", "second door");

    Assert.IsTrue(first.Group > 0, "group 0 means 'not given'; a real build never reports it");
    Assert.AreNotEqual(first.Group, second.Group, "two ungrouped builds do not share a group by accident");

    StatsBuildTrace.End(second);
    StatsBuildTrace.End(first);
    Assert.AreEqual(0, StatsBuildTrace.ActiveBuilds);
  }

  [TestMethod]
  public void GroupingDoesNotUndoNestingOnTheSameThread()
  {
    // The two rules are independent: a nested same-thread build is one door whether or not its group matches, and a group never
    // makes a DIFFERENT thread's foreign build disappear (pinned by AForeignBuildUnderAFannedOutRequestStillWarns).
    var before = StatsBuildTrace.OverlapBuilds;
    var group = StatsBuildTrace.NextGroupId();

    var outer = StatsBuildTrace.Begin("healing", "healing pane options [time window]", "rebuild", group);
    var inner = StatsBuildTrace.Begin("healing", "healing pane options [time window]", "full", group);

    Assert.AreEqual(0, inner.OverlappedWith);
    Assert.AreEqual(before, StatsBuildTrace.OverlapBuilds);

    StatsBuildTrace.End(inner);
    StatsBuildTrace.End(outer);
  }

  /*
   * Where the lines go is not tested here, because nothing in this suite captures logs (the app's own pattern: levels are
   * decided once from settings.txt in App.SetLoggingLevel and read, never intercepted). The law - every build's line is a
   * Debug line, and the only things of this class that reach an ordinary log are the two anomaly Warns pinned by their
   * counters above - lives in the StatsBuildTrace header and docs/DesignNotes -> "Name every door". A line drifting back to
   * Info is found by grepping a normal session's EQLogParser.log, which is also how the original flood was found.
   */
}
