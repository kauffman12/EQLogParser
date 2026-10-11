using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * The fight list's visible-row machinery, tested without a grid (2026-11).
 *
 * These three functions decide WHICH rows the Fight List shows and how it gets there, and they used to be split between the pane and
 * SfDataGrid: the grid held the filter (View.Filter) and every index-taking call had to translate into "positions among the rows the view
 * renders". That split is what made the Tanking dial misbehave — re-filtering reshuffles which fight sits at a grid row while the control's
 * selection is stored by row INDEX, so the highlight could sit on one row and mean another ("i uncheck tanking … nothing actually happens"),
 * and the pane had to abandon its incremental patch whenever the filter was on because the shipped assembly has no live-filter refresh.
 *
 * What the laws below pin is the property the pane now leans the whole design on: **rows are shared references**. Hiding a fight moves a
 * reference, so a retained row keeps its identity. Visible-list indices match grid range positions only while the grid is unsorted.
 * A rewrite that copies rows instead of moving them would break these tests rather than ship a phantom selection.
 */
[TestClass]
public sealed class ShownRowsTest
{
  private sealed class Row
  {
    internal Row(string name) => Name = name;
    internal string Name { get; }
    internal bool Shown;

    // A divider row: shown by the Inactivity dial but never a thing a select can take, so it is a hole in every run.
    internal bool IsGap;

    // Deliberately NOT overridden: equality stays reference equality, which is what ShownRows promises and what makes a "which row is this?"
    // mistake visible in an AreSame rather than passing on a value comparison.
  }

  private static List<Row> Rows(int count)
  {
    var rows = new List<Row>(count);
    for (var i = 0; i < count; i++) rows.Add(new Row("row" + i));
    return rows;
  }

  [TestMethod]
  public void ProjectKeepsOrderAndTheRowsThemselves()
  {
    var all = Rows(6);
    for (var i = 0; i < all.Count; i++) all[i].Shown = i % 2 == 0;

    var visible = ShownRows.Project(all, r => r.Shown);

    CollectionAssert.AreEqual(new[] { all[0], all[2], all[4] }, visible);
    for (var i = 0; i < visible.Count; i++)
      Assert.AreSame(all[i * 2], visible[i], "the projection moves references; it never copies a row");

    // Everything hidden is still in the full list — hiding is a view decision, not a deletion.
    Assert.AreEqual(6, all.Count);
  }

  /*
   * The live case: a fight crosses the Tanking line during a pull and joins the list in front of rows that were already there. Two things
   * must hold — it lands at its own position, and the rows already displayed are THE SAME OBJECTS, which is how a selected row, the scroll
   * position and the search mark survive a pass that changed something else entirely.
   */
  [TestMethod]
  public void ReconcilePutsANewlyVisibleRowAtItsOwnPlaceAndLeavesTheOthersAlone()
  {
    var all = Rows(5);
    all[0].Shown = all[2].Shown = all[4].Shown = true;
    var shown = new ObservableCollection<Row>(ShownRows.Project(all, r => r.Shown));

    // Two more fights take their first player-damage hit: they cross into the list between rows already on screen.
    all[1].Shown = true;
    all[3].Shown = true;
    var (removals, insertions) = ShownRows.Reconcile(shown, ShownRows.Project(all, r => r.Shown));

    Assert.AreEqual(0, removals);
    Assert.AreEqual(2, insertions);
    CollectionAssert.AreEqual(new[] { all[0], all[1], all[2], all[3], all[4] }, shown);
    for (var i = 0; i < shown.Count; i++) Assert.AreSame(all[i], shown[i]);
  }

  /*
   * The other half of the live case, and the one with a board attached: a row stops being visible and leaves. The rows that stay must keep
   * their identities — if a reconcile rebuilt the list out of new objects, the grid's selection would point at addresses nobody owns any
   * more, which is precisely the bug this design exists to remove.
   */
  [TestMethod]
  public void ReconcileTakesAwayWhatHidWithoutMovingAnyoneElse()
  {
    var all = Rows(6);
    foreach (var row in all) row.Shown = true;
    var shown = new ObservableCollection<Row>(ShownRows.Project(all, r => r.Shown));

    all[2].Shown = false;
    all[5].Shown = false;
    var (removals, insertions) = ShownRows.Reconcile(shown, ShownRows.Project(all, r => r.Shown));

    Assert.AreEqual(2, removals);
    Assert.AreEqual(0, insertions);
    CollectionAssert.AreEqual(new[] { all[0], all[1], all[3], all[4] }, shown);
  }

  [TestMethod]
  public void ReconcileIsFreeWhenNothingMovedAndHoldsOnASixthPass()
  {
    var all = Rows(9);
    for (var i = 0; i < all.Count; i++) all[i].Shown = i % 3 != 0;

    var shown = new ObservableCollection<Row>(ShownRows.Project(all, r => r.Shown));
    var wantedCount = shown.Count;

    // Reconciling against the same state is the ordinary derive pass: no notifications may be spent at all.
    for (var pass = 0; pass < 6; pass++)
    {
      var (removals, insertions) = ShownRows.Reconcile(shown, ShownRows.Project(all, r => r.Shown));
      Assert.AreEqual((0, 0), (removals, insertions), $"pass {pass}");
      Assert.AreEqual(wantedCount, shown.Count);
    }
  }

  /*
   * A dial toggle expressed as a reconcile rather than a swap: from "everything" to "only what the raid damaged", in one call. Ends exactly at
   * the wanted sequence regardless of how many rows leave, which is the property that lets the pane hand this to an ObservableCollection without
   * pre-computing indices (removals run descending; insertions place at their own index).
   */
  [TestMethod]
  public void ReconcileMovesBetweenTwoDialStatesInOneCall()
  {
    var all = Rows(20);
    foreach (var row in all) row.Shown = true;
    var shown = new ObservableCollection<Row>(ShownRows.Project(all, r => r.Shown));

    for (var i = 0; i < all.Count; i++) all[i].Shown = i % 4 == 1;
    ShownRows.Reconcile(shown, ShownRows.Project(all, r => r.Shown));

    var expected = new List<Row>();
    for (var i = 1; i < 20; i += 4) expected.Add(all[i]);
    CollectionAssert.AreEqual(expected, shown);

    // And back to nothing at all: an empty wanted list empties the display without emptying the capture.
    foreach (var row in all) row.Shown = false;
    ShownRows.Reconcile(shown, ShownRows.Project(all, r => r.Shown));
    Assert.AreEqual(0, shown.Count);
    Assert.AreEqual(20, all.Count, "the rows themselves are not the dial's to remove");
  }

  /*
   * Grid selects want contiguous ranges, so a selection is walked as runs — and the walk's positions are PLAIN indices into the visible list
   * now. The version this replaced counted shown rows while walking the FULL list, because the grid held the filter; that translation was where
   * a range could land one row off for every hidden row above it. Asserted literally: with gaps at positions 1 and 4 of the displayed list, the
   * runs are (0,0), (2,3), (5,6) — nothing skipped twice, nothing shifted.
   */
  [TestMethod]
  public void RunsCutsTheVisibleListIntoContiguousRangesWithNothingToTranslate()
  {
    var all = Rows(7);
    for (var i = 0; i < all.Count; i++) all[i].Shown = true;
    all[1].IsGap = true;      // two inactivity spacers sit among the fights, at displayed positions 1 and 4
    all[4].IsGap = true;

    var visible = ShownRows.Project(all, r => r.Shown);
    var runs = ShownRows.Runs(visible, row => !row.IsGap);

    CollectionAssert.AreEqual(new[] { (0, 0), (2, 3), (5, 6) }, runs);

    // Select All takes exactly those rows: the pairs index the SAME objects the list holds, so the grid and the pane cannot disagree.
    var selected = new List<Row>();
    foreach (var (from, to) in runs)
      for (var i = from; i <= to; i++) selected.Add(visible[i]);
    CollectionAssert.AreEqual(new[] { all[0], all[2], all[3], all[5], all[6] }, selected);
  }

  [TestMethod]
  public void RunsRespectsThePredicateAndAnswersEmptyWhenNothingMatches()
  {
    var all = Rows(5);
    foreach (var row in all) row.Shown = true;

    var runs = ShownRows.Runs(all, r => r.Name == "row1" || r.Name == "row3" || r.Name == "row4");
    CollectionAssert.AreEqual(new[] { (1, 1), (3, 4) }, runs);

    Assert.AreEqual(0, ShownRows.Runs(all, _ => false).Count);
    Assert.AreEqual(0, ShownRows.Runs(new List<Row>(), _ => true).Count, "an empty list is an answer, not a crash");
  }
}
