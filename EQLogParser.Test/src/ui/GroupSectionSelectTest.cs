using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser.Tests.derive;

/*
 * The fight list's group gestures (EQLogParser.Core/src/ui/GroupSectionSelect.cs), reported from the field as two separate failures of the
 * same mechanism: "if i select multiple groups and right click unselect group it works if im on the group i did the select all from. if i
 * right-click on another group area and do it then it only unselects the group i was previously in. its like the select cursor/item stays
 * where it was." That is the sentence the deleted legacy table answered with one line in its ContextMenuOpening — point the grid's current
 * row at whatever the pointer opened the menu over — and every position-based item then read that row. These assert the two decisions the
 * pane makes from it: WHICH row a gesture is about, and WHICH RUN OF ROWS that row's section covers.
 *
 * Rows are one bool each (`namesSection`: fight row = true, inactivity divider = false), so `F F - F F - F` reads
 * `{ true, true, false, true, true, false, true }`.
 */
[TestClass]
public class GroupSectionSelectTest
{
  // Three sections: rows 0-1, rows 3-4, row 6, separated by dividers at 2 and 5.
  private static readonly bool[] Layout = [true, true, false, true, true, false, true];

  [TestMethod]
  [Description("A section is the run of fight rows between the two dividers around it")]
  public void ASectionIsTheRunAroundTheRow()
  {
    Assert.AreEqual((3, 4), GroupSectionSelect.BoundsOf(Layout, 3));
    Assert.AreEqual((3, 4), GroupSectionSelect.BoundsOf(Layout, 4), "the bounds belong to the section, not to the row that asked");
    Assert.AreEqual((0, 1), GroupSectionSelect.BoundsOf(Layout, 0), "a section with nothing above it is bounded by the top of the list");
    Assert.AreEqual((6, 6), GroupSectionSelect.BoundsOf(Layout, 6), "a lone fight between a divider and the end is a section of one");
  }

  [TestMethod]
  [Description("A divider names no section: it is the gap BETWEEN two, and belongs to neither")]
  public void ADividerCarriesNoSection()
  {
    Assert.AreEqual((GroupSectionSelect.NoRow, GroupSectionSelect.NoRow), GroupSectionSelect.BoundsOf(Layout, 2));

    // ...which is why a gesture ON a divider falls THROUGH to the next link rather than inheriting the gap: the cursor's row (0)
    // is the subject here, never row 2 — and every enable rule keys off the same answer.
    Assert.AreEqual(0, GroupSectionSelect.TargetFor(2, Layout[2], 0, Layout[0], 4));
    Assert.AreEqual((GroupSectionSelect.NoRow, GroupSectionSelect.NoRow), GroupSectionSelect.BoundsOf(Layout, 2),
      "the divider itself is not a section: taking its bounds would give the gap between two pulls as something to select");
  }

  [TestMethod]
  [Description("An out-of-range or missing target answers 'no section' rather than throwing")]
  public void ATargetThatIsNotThereNamesNothing()
  {
    Assert.AreEqual((GroupSectionSelect.NoRow, GroupSectionSelect.NoRow), GroupSectionSelect.BoundsOf(Layout, GroupSectionSelect.NoRow));
    Assert.AreEqual((GroupSectionSelect.NoRow, GroupSectionSelect.NoRow), GroupSectionSelect.BoundsOf(Layout, 99));
    Assert.AreEqual((GroupSectionSelect.NoRow, GroupSectionSelect.NoRow), GroupSectionSelect.BoundsOf(null, 0));
    Assert.AreEqual((GroupSectionSelect.NoRow, GroupSectionSelect.NoRow), GroupSectionSelect.BoundsOf([false], 0));
  }

  [TestMethod]
  [Description("The row the pointer opened the menu on wins over both the cursor and the selection")]
  public void ThePointerIsTheSubject()
  {
    // Cursor at 0, selection at 3, right-click landed on 6: the gesture is about 6. This is the reported bug exactly —
    // right-click another group's area and THAT group is the one selected or unselected.
    Assert.AreEqual(6, GroupSectionSelect.TargetFor(6, true, 0, true, 3));
    Assert.AreEqual((6, 6), GroupSectionSelect.BoundsOf(Layout, GroupSectionSelect.TargetFor(6, true, 0, true, 3)));
  }

  [TestMethod]
  [Description("With no row under the pointer, the grid's current row speaks; failing that, the selection")]
  public void TheFallbackChainKeepsTheMenuUsable()
  {
    // Keyboard-opened menu (nothing under the pointer) with a cursor on row 3.
    Assert.AreEqual(3, GroupSectionSelect.TargetFor(GroupSectionSelect.NoRow, false, 3, true, 0));

    // Pointer ON A DIVIDER and cursor on one too: fall through to the first selected fight row, so "unselect group" from
    // the gap still takes the group that is on screen — the case a divider-click used to lose entirely.
    Assert.AreEqual(3, GroupSectionSelect.TargetFor(2, false, 5, false, 3));

    // Nothing anywhere: no target, and both menu items are greyed rather than acting on an arbitrary row.
    Assert.AreEqual(GroupSectionSelect.NoRow, GroupSectionSelect.TargetFor(GroupSectionSelect.NoRow, false, GroupSectionSelect.NoRow, false, GroupSectionSelect.NoRow));
  }

  [TestMethod]
  [Description("Select Group needs a section; Unselect Group needs a section AND something selected")]
  public void TheTwoItemsAskDifferentQuestions()
  {
    Assert.IsTrue(GroupSectionSelect.CanSelect(true));
    Assert.IsFalse(GroupSectionSelect.CanSelect(false), "no section named = nothing to add");

    Assert.IsTrue(GroupSectionSelect.CanUnselect(true, 12));
    Assert.IsFalse(GroupSectionSelect.CanUnselect(true, 0), "there is nothing to take away from an empty selection");
    Assert.IsFalse(GroupSectionSelect.CanUnselect(false, 12));
  }

  /*
   * What the gesture LEAVES selected (2026-10-14, from "select group should not keep the other groups selected unless I am holding
   * control"). The mode comes off the modifiers riding the press that opened the menu, so the headless question is only: given a
   * standing selection and a section, which rows survive plain, and which survive additive? These are the two answers the pane has
   * to produce; the WPF pair (FightTableGroupGestureTest) pins that a real grid reaches them and where the modifier comes from.
   */
  [TestMethod]
  [Description("Without a modifier the group stands alone: the selection it replaces does not ride along")]
  public void AGestureWithoutAModifierLeavesOnlyTheGroup()
  {
    var (a, b, x, y) = (new Row("A1"), new Row("A2"), new Row("B1"), new Row("B2"));
    var section = new List<Row> { a, b };

    var kept = GroupSectionSelect.SelectedAfterGesture(section, new List<Row> { x, y }, additive: false);

    CollectionAssert.AreEqual(new List<Row> { a, b }, kept, "exactly this encounter, in list order");
    Assert.IsFalse(kept.Contains(x), "a row from another group may not survive a plain 'select group'");
  }

  [TestMethod]
  [Description("Rows already selected inside the group are not counted twice when Ctrl or Shift rode the press")]
  public void AModifierOnThePressAddsTheGroupToTheSelection()
  {
    var (a, b, x, y) = (new Row("A1"), new Row("A2"), new Row("B1"), new Row("B2"));
    var section = new List<Row> { a, b };

    // The everyday additive shape: the operator held Ctrl, so part of THIS group is already in, and another group is too.
    var kept = GroupSectionSelect.SelectedAfterGesture(section, new List<Row> { b, x, y }, additive: true);

    CollectionAssert.AreEquivalent(new List<Row> { a, b, x, y }, kept, "the union, so 'select all then add my group' stays harmless");
    Assert.AreEqual(1, kept.Count(r => ReferenceEquals(r, b)), "a row in both sets appears once");
  }

  [TestMethod]
  [Description("Rows are compared as objects, because a derive pass replaces every row instance wholesale")]
  public void TwoRowObjectsWithOneNameAreTwoRows()
  {
    var (twin1, twin2) = (new Row("Same"), new Row("Same"));

    CollectionAssert.AreEquivalent(new List<Row> { twin1, twin2 },
      GroupSectionSelect.SelectedAfterGesture([twin1], [twin2], additive: true),
      "name equality would silently drop the second life of a name repeated in one capture");
  }

  [TestMethod]
  [Description("An empty standing selection answers either mode the same way — the group")]
  public void WithNothingSelectedBothModesGiveTheGroup()
  {
    var (a, b) = (new Row("A1"), new Row("A2"));
    var section = new List<Row> { a, b };

    foreach (var additive in new[] { false, true })
      CollectionAssert.AreEqual(section, GroupSectionSelect.SelectedAfterGesture(section, [], additive),
        $"additive: {additive} — 'select my group' on an empty list cannot produce anything but the group");
  }

  [TestMethod]
  [Description("An empty selection with the pointer on a fight row still offers Select Group ('give me this whole pull')")]
  public void PointingAtARowIsEnoughToSelectItsGroup()
  {
    // The legacy menu enabled these whenever the grid had rows at all; the derived pane needs one better — a named
    // section — and that is satisfied by the row under the pointer, selection or no selection.
    var target = GroupSectionSelect.TargetFor(4, true, GroupSectionSelect.NoRow, false, GroupSectionSelect.NoRow);
    Assert.IsTrue(GroupSectionSelect.CanSelect(GroupSectionSelect.BoundsOf(Layout, target).From >= 0));
    Assert.IsFalse(GroupSectionSelect.CanUnselect(GroupSectionSelect.BoundsOf(Layout, target).From >= 0, 0));
  }

  // One displayed fight row. Identity is the whole point of this fixture: the pane compares rows by instance, because
  // DerivedFightRows.Build hands out brand-new objects every derive pass (docs/DesignNotes.md → "Select Group stands alone").
  private sealed class Row(string name)
  {
    public string Name { get; } = name;
    public override string ToString() => Name;
  }
}
