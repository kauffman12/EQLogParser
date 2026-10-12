using EQLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class NamesTableViewBuildTest
{
  private sealed record Row(string Name, IdentityKind Kind, string Type = "", string? PlayerClass = null,
    string Owner = "", string Why = "") : IIdentityCensusRow;

  [TestMethod]
  public void AllTypesIncludesUnplacedRowsButNarrowedTypesDoNot()
  {
    Row[] rows = [new("Unplaced", IdentityKind.Unknown), new("Taken Back", IdentityKind.Unknown),
      new("Ann", IdentityKind.Player), new("Pet", IdentityKind.Pet)];
    Assert.AreEqual(4, NamesTableView.Build(rows, Question()).Count);
    CollectionAssert.AreEqual(new[] { "Ann" }, NamesTableView.Build(rows, Question(kinds: [IdentityKind.Player]))
      .Select(row => row.Name).ToArray());
    Assert.AreEqual(0, NamesTableView.Build(rows, Question(kinds: [])).Count);
    Assert.AreEqual(0, NamesTableView.Build(rows, Question(kinds: [IdentityKind.Spell])).Count);
    Assert.AreEqual(4, NamesTableView.Build(rows, Question()).Count, "widening starts from the complete census again");
  }

  [TestMethod]
  public void ClearingAQueryRecoversNamesFromEveryPage()
  {
    var rows = Enumerable.Range(0, 2500).Select(i => new Row($"Name{i:D4}", IdentityKind.Player)).ToArray();
    var found = NamesTableView.Build(rows, Question("ME2042"));
    Assert.AreEqual(1, found.Count);
    Assert.AreEqual("Name2042", found[0].Name, "search is across the whole census, not its first page");
    Assert.AreEqual(0, NamesTableView.Build(rows, Question("absent")).Count);
    Assert.AreEqual(2500, NamesTableView.Build(rows, Question()).Count);
    Assert.AreEqual(3, NamesTableView.DecidePage(rows.Length, 1000, 0, -1).PageCount);
  }

  [TestMethod]
  public void BothPredicatesApplyBeforePagingAndDirectionWordsStaySpell()
  {
    Row[] rows = [new("Ann", IdentityKind.Player), new("An animal", IdentityKind.Npc),
      new("An NPC spell", IdentityKind.Spell, "NPC Spell"),
      new("An allied spell", IdentityKind.Spell, "Player Spell")];
    var built = NamesTableView.Build(rows, Question("AN", [IdentityKind.Spell]));
    CollectionAssert.AreEqual(new[] { "An allied spell", "An NPC spell" }, built.Select(row => row.Name).ToArray());
    Assert.AreEqual(2, NamesTableView.DecidePage(built.Count, 1000, 0, -1).SliceCount);
  }

  [TestMethod]
  public void GlobalOrderAndNameTieBreaksHoldAcrossThePageBoundary()
  {
    var rows = Enumerable.Range(0, 1001).Reverse()
      .Select(i => new Row($"Name{i:D4}", IdentityKind.Player, "Player", i == 1000 ? "Bard" : "Wizard")).ToArray();
    var ascending = NamesTableView.Build(rows, Question(column: IdentityRowOrder.TypeColumn));
    Assert.AreEqual("Name0999", ascending[999].Name);
    Assert.AreEqual("Name1000", ascending[1000].Name);
    var descending = NamesTableView.Build(rows, Question(column: IdentityRowOrder.TypeColumn, descending: true));
    Assert.AreEqual("Name0001", descending[999].Name);
    Assert.AreEqual("Name0000", descending[1000].Name, "descending reverses the tiebreak too");
    var byClass = NamesTableView.Build(rows, Question(column: IdentityRowOrder.ClassColumn));
    Assert.AreEqual("Name1000", byClass[0].Name, "class ordering must not fall back to Name");
  }

  [TestMethod]
  public void CueTextIsNeverAQueryOnStartupBlurEscapeOrReset()
  {
    var cue = "Name Filter";
    Assert.AreEqual("", NamesTableView.QueryText(cue, cue));
    Assert.AreEqual("", NamesTableView.QueryText("", cue));
    Assert.AreEqual("Ann", NamesTableView.QueryText("Ann", cue));
    Assert.AreEqual("name filter", NamesTableView.QueryText("name filter", cue), "only the exact cue is a watermark");
  }

  [TestMethod]
  public void EveryWorldAndQuestionChangeRefusesAnOldAnswer()
  {
    var stamp = new NamesTableView.BuildStamp(1, 42, 7, 9, Question("ann"));
    Assert.IsTrue(NamesTableView.IsCurrent(stamp, stamp));
    NamesTableView.BuildStamp[] moved =
    [
      stamp with { Epoch = 2 },
      stamp with { SessionId = 43 },
      stamp with { CensusRevision = 8 },
      stamp with { WriteGeneration = 10 },
      stamp with { DeriveRequestGeneration = 1 },
      stamp with { Question = Question("bob") },
      stamp with { Question = Question("ann", [IdentityKind.Player]) },
      stamp with { Question = Question("ann", column: IdentityRowOrder.TypeColumn) },
      stamp with { Question = Question("ann", descending: true) },
    ];
    foreach (var current in moved)
      Assert.IsFalse(NamesTableView.IsCurrent(stamp, current), $"old result must not paint after {current}");
  }

  [TestMethod]
  public void HeldResultsCheckTheirStampAgainWhenTheGestureEnds()
  {
    var stamp = new NamesTableView.BuildStamp(1, 42, 7, 9, Question());
    var current = stamp;
    var painted = false;
    var hold = new EditorHold();
    hold.Open();
    Assert.IsTrue(hold.Defer(() => painted = NamesTableView.IsCurrent(stamp, current)));
    current = current with { CensusRevision = 8 };
    hold.Close();
    Assert.IsFalse(painted, "a result current when held can be stale on release");
  }

  [TestMethod]
  public void AnIndexBuildPaysManyOverlappingAsksWithOneFollowUp()
  {
    var lane = new NamesViewBuildLane();
    Assert.IsTrue(lane.TryBegin());
    for (var i = 0; i < 10; i++) Assert.IsFalse(lane.TryBegin());
    Assert.IsTrue(lane.Complete());
    Assert.IsTrue(lane.TryBegin());
    Assert.IsFalse(lane.Complete(), "beginning the follow-up consumed the coalesced debt");
    Assert.IsTrue(lane.TryBegin(), "completion releases the lane on success or failure");
    Assert.IsFalse(lane.Complete());
  }

  [TestMethod]
  public void AbandoningACaptureDoesNotStartASecondWorkerOverTheFirst()
  {
    var lane = new NamesViewBuildLane();
    Assert.IsTrue(lane.TryBegin());
    Assert.IsFalse(lane.TryBegin());
    lane.Abandon();
    Assert.IsFalse(lane.Complete(), "abandon drops the old capture's follow-up");

    Assert.IsTrue(lane.TryBegin());
    lane.Abandon();
    Assert.IsFalse(lane.TryBegin(), "a new capture's ask waits until the old worker finishes");
    Assert.IsTrue(lane.Complete(), "the new capture's debt survives the old worker completing");
    Assert.IsTrue(lane.TryBegin());
    Assert.IsFalse(lane.Complete());
  }

  private static NamesTableView.Question Question(string query = "", IdentityKind[]? kinds = null,
    string column = IdentityRowOrder.DefaultColumn, bool descending = false)
    => NamesTableView.For(query, kinds ?? IdentityVocabulary.TypeOptions.Select(option => option.Kind).ToArray(),
      column, descending);
}
