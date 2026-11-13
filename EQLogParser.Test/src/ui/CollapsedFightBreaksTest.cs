namespace EQLogParser;

[TestClass]
public sealed class CollapsedFightBreaksTest
{
  [TestMethod]
  public void HiddenSectionsBecomeOneGapBetweenTheVisibleFights()
  {
    Row[] all = [new(false, 0, 20), new(true, 20, 300), new(false, 300, 320, false),
      new(true, 320, 800), new(false, 800, 820)];
    var shown = Project(all);

    Assert.HasCount(3, shown);
    Assert.AreSame(all[0], shown[0]);
    Assert.AreSame(all[4], shown[2]);
    Assert.IsTrue(shown[1].Divider);
    Assert.AreEqual(20d, shown[1].Begin);
    Assert.AreEqual(800d, shown[1].Last);
    Assert.AreNotSame(all[1], shown[1]);
    Assert.AreNotSame(all[3], shown[1]);
    Assert.AreEqual(300d, all[1].Last, "source dividers must keep their original gaps");
    Assert.AreEqual(320d, all[3].Begin);
  }

  [TestMethod]
  public void AnOverlappingFightCannotMoveTheGapStartBackwards()
  {
    Row[] all = [new(false, 0, 200), new(false, 50, 100), new(true),
      new(false, 500, 520, false), new(true), new(false, 1_000, 1_020)];
    var shown = Project(all);
    Assert.HasCount(4, shown);
    Assert.AreEqual(200d, shown[2].Begin, "use the running latest end, not the previous row's end");
    Assert.AreEqual(1_000d, shown[2].Last);
  }

  [TestMethod]
  public void ThreeOrMoreDividersAlsoCollapseToOne()
  {
    Row[] all = [new(false, 0, 20), new(true), new(false, 300, 320, false), new(true),
      new(false, 600, 620, false), new(true), new(false, 900, 920)];
    var shown = Project(all);
    Assert.HasCount(3, shown);
    Assert.AreEqual(20d, shown[1].Begin);
    Assert.AreEqual(900d, shown[1].Last);
  }

  [TestMethod]
  public void AHiddenBridgeDoesNotInventANewBoundary()
  {
    Row[] all = [new(false, 0, 20), new(false, 30, 900, false), new(false, 910, 930)];
    var shown = Project(all);
    CollectionAssert.AreEqual(new[] { all[0], all[2] }, shown);
  }

  [TestMethod]
  public void ASingleExistingDividerKeepsItsInstanceAndLabelRange()
  {
    Row[] all = [new(false, 0, 20), new(true, 20, 800), new(false, 800, 820)];
    var shown = Project(all);
    CollectionAssert.AreEqual(all, shown);
    Assert.AreSame(all[1], shown[1]);
  }

  [TestMethod]
  public void LeadingTrailingAndEntirelyHiddenSectionsLeaveNoOrphanDividers()
  {
    Row[] all = [new(false, 0, 20, false), new(true), new(false, 300, 320),
      new(true), new(false, 600, 620, false), new(true), new(false, 900, 920, false)];
    CollectionAssert.AreEqual(new[] { all[2] }, Project(all));
    all[2].Shown = false;
    Assert.HasCount(0, Project(all));
    Assert.HasCount(0, Project([]));
  }

  [TestMethod]
  [DataRow(true, true, 5)]
  [DataRow(false, true, 3)]
  [DataRow(true, false, 3)]
  [DataRow(false, false, 2)]
  public void BothDialsComposeWithoutChangingGroupMembership(bool showBreaks, bool showTanking, int count)
  {
    Row[] all = [new(false, 0, 20), new(true), new(false, 300, 320, false),
      new(true), new(false, 800, 820)];
    bool Shown(Row row) => row.Divider ? showBreaks : showTanking || row.Shown;
    var shown = ShownRows.ProjectCollapsedBreaks(all, Shown, row => row.Divider,
      row => (row.Begin, row.Last), (from, to) => new Row(true, from, to));
    Assert.HasCount(count, shown);
    CollectionAssert.AreEqual(new[] { all[0] },
      GroupSectionSelect.VisibleMembers(all, 0, row => !row.Divider, Shown));
    CollectionAssert.AreEqual(new[] { all[4] },
      GroupSectionSelect.VisibleMembers(all, 4, row => !row.Divider, Shown));
    if (showBreaks && showTanking) CollectionAssert.AreEqual(all, shown);
  }

  [TestMethod]
  public void AHiddenFightBecomingVisibleRestoresItsSeparateBoundaries()
  {
    Row[] all = [new(false, 0, 20), new(true), new(false, 300, 320, false),
      new(true), new(false, 800, 820)];
    Assert.HasCount(3, Project(all));
    all[2].Shown = true;
    CollectionAssert.AreEqual(all, Project(all));
    all[2].Shown = false;
    Assert.HasCount(3, Project(all));
  }

  [TestMethod]
  public void MissingTimesKeepARealDividerInsteadOfInventingADuration()
  {
    Row[] all = [new(false, double.NaN, double.NaN), new(true), new(false, 300, 320, false),
      new(true), new(false, 800, 820)];
    var shown = Project(all);
    Assert.HasCount(3, shown);
    Assert.AreSame(all[1], shown[1]);
  }

  private static List<Row> Project(IReadOnlyList<Row> all) =>
    ShownRows.ProjectCollapsedBreaks(all, row => row.Divider || row.Shown, row => row.Divider,
      row => (row.Begin, row.Last), (from, to) => new Row(true, from, to));

  private sealed class Row(bool divider, double begin = 0, double last = 0, bool shown = true)
  {
    internal bool Divider { get; } = divider;
    internal double Begin { get; } = begin;
    internal double Last { get; } = last;
    internal bool Shown { get; set; } = shown;
  }
}
