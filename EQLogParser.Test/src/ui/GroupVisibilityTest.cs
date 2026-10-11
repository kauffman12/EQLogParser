using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser.Tests.Ui;

[TestClass]
public sealed class GroupVisibilityTest
{
  [TestMethod]
  [DataRow(true, true)]
  [DataRow(true, false)]
  [DataRow(false, true)]
  [DataRow(false, false)]
  public void HidingRowsNeverMergesGroups(bool showBreaks, bool showTanking)
  {
    var first = new Row("First", false, true);
    var untouched = new Row("Untouched", false, false);
    var second = new Row("Second", false, true);
    var last = new Row("Last", false, true);
    Row[] all = [first, untouched, new("Gap", true, false), second, new("Gap", true, false), last];
    bool Shown(Row row) => row.Divider ? showBreaks : showTanking || row.Damaged;

    var group = GroupSectionSelect.VisibleMembers(all, 0, row => !row.Divider, Shown);
    CollectionAssert.AreEqual(showTanking ? new[] { first, untouched } : new[] { first }, group);
    Assert.AreSame(first, group[0]);
    CollectionAssert.AreEqual(new[] { second },
      GroupSectionSelect.VisibleMembers(all, 3, row => !row.Divider, Shown));
    CollectionAssert.AreEqual(new[] { last },
      GroupSectionSelect.VisibleMembers(all, 5, row => !row.Divider, Shown));

    // Unselect Group must leave selections from other encounters alone, even with every divider hidden.
    var selected = new HashSet<Row>(all.Where(row => !row.Divider && Shown(row)));
    selected.ExceptWith(group);
    CollectionAssert.AreEquivalent(new[] { second, last }, selected.ToArray());
  }

  [TestMethod]
  public void AHiddenFightInsideAGroupDoesNotSplitIt()
  {
    Row[] all = [new("First", false, true), new("Untouched", false, false), new("Last", false, true)];
    var group = GroupSectionSelect.VisibleMembers(all, 2, row => !row.Divider, row => row.Damaged);
    CollectionAssert.AreEqual(new[] { all[0], all[2] }, group);
  }

  [TestMethod]
  public void MissingHiddenAndDividerTargetsSelectNothing()
  {
    Row[] all = [new("Untouched", false, false), new("Gap", true, false), new("Fight", false, true)];
    foreach (var target in new[] { -1, 0, 1, 3 })
    {
      Assert.AreEqual(0, GroupSectionSelect.VisibleMembers(all, target,
        row => !row.Divider, row => row.Divider || row.Damaged).Count);
    }
    Assert.AreEqual(0, GroupSectionSelect.VisibleMembers(Array.Empty<Row>(), 0,
      row => !row.Divider, _ => true).Count);
  }

  private sealed record Row(string Name, bool Divider, bool Damaged);
}
