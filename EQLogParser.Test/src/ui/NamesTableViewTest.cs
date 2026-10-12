using EQLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/*
 * The Identity Table's view arithmetic, out of the pane and onto a surface where it can be exercised without a 25,000-name
 * capture: what one page holds, which page a published list lands on, and when that landing is a jump. These are the
 * decisions docs/names-table-paging-plan.md → "The publish door" puts in front of the grid, and every one of them is a
 * hazard that only shows up at a boundary — 0 rows, exactly one page, one row past it, a filter that shrinks ten pages to
 * two, a selection that survives a rebuild on another page.
 */
[TestClass]
public class NamesTableViewTest
{
  /*
   * The slice decisions, in the plan's own sizes: a page holds 1,000, so the boundaries are 0, 1, 999, 1,000 and 1,001.
   */
  [TestMethod]
  public void APageHoldsAThousandRowsAndTheLastPageTakesTheRemainder()
  {
    Assert.AreEqual(0, NamesTableView.DecidePage(0, 1000, 0, -1).PageCount, "no rows, no pages: an empty page reads as a loading failure");
    Assert.AreEqual(1, NamesTableView.DecidePage(1, 1000, 0, -1).PageCount);
    Assert.AreEqual(1, NamesTableView.DecidePage(999, 1000, 0, -1).PageCount);
    Assert.AreEqual(1, NamesTableView.DecidePage(1000, 1000, 0, -1).PageCount, "exactly one page at the boundary");
    Assert.AreEqual(2, NamesTableView.DecidePage(1001, 1000, 0, -1).PageCount, "one row past the boundary is two pages");

    var full = NamesTableView.DecidePage(1000, 1000, 0, -1);
    Assert.AreEqual(0, full.SliceStart);
    Assert.AreEqual(1000, full.SliceCount);

    var partial = NamesTableView.DecidePage(1500, 1000, 1, -1);
    Assert.AreEqual(1000, partial.SliceStart, "the second page starts where the first one ended");
    Assert.AreEqual(500, partial.SliceCount, "the last page takes the remainder and nothing more");

    var single = NamesTableView.DecidePage(37, 1000, 0, -1);
    Assert.AreEqual(37, single.SliceCount, "a list shorter than a page is its own whole page");
  }

  [TestMethod]
  public void AFilterThatShrinksTheListClampsThePage()
  {
    // Ten pages of names, pager standing on page seven; the reader's filter shrinks the answer to three pages.
    var before = NamesTableView.DecidePage(10000, 1000, 7, -1);
    Assert.AreEqual(7, before.PageIndex);

    var after = NamesTableView.DecidePage(3000, 1000, 7, -1);
    Assert.AreEqual(2, after.PageIndex, "the list lands on the last page that exists, not an empty one");
    Assert.IsFalse(after.JumpToSelection, "nothing is selected, so the landing is a clamp, not a jump");

    // And a list that filters to nothing: zero pages, page index zero, and no slice at all.
    var empty = NamesTableView.DecidePage(0, 1000, 4, -1);
    Assert.AreEqual(0, empty.PageCount);
    Assert.AreEqual(0, empty.PageIndex);
    Assert.AreEqual(0, empty.SliceStart);
    Assert.AreEqual(0, empty.SliceCount);
  }

  [TestMethod]
  public void ASelectionThatSurvivesTheRebuildFollowsItsPage()
  {
    // The reader sits on page zero with a selection on it: no jump, the viewport is already where the name lives.
    var inPlace = NamesTableView.DecidePage(5000, 1000, 0, 42);
    Assert.IsFalse(inPlace.JumpToSelection);
    Assert.AreEqual(0, inPlace.PageIndex);

    // The same name on page two: the pager moves there before the selection is handed back.
    var moved = NamesTableView.DecidePage(5000, 1000, 0, 2042);
    Assert.IsTrue(moved.JumpToSelection, "a rebuild that moves the selection's page must be a JUMP the reader feels");
    Assert.AreEqual(2, moved.PageIndex);

    // A selection past the end of the list answers as none: clamp only, no jump onto a page that does not exist — and a
    // reader already sitting on a page that still exists is not moved by a dead selection.
    var stale = NamesTableView.DecidePage(1500, 1000, 9, 4000);
    Assert.IsFalse(stale.JumpToSelection);
    Assert.AreEqual(1, stale.PageIndex, "clamped to the last page that exists");

    // And the clamp and the jump compose: the list shrinks around a reader standing on page nine (clamped to two) whose
    // selection lives on page zero — the pager ends where the SELECTION is, and the movement is the jump.
    var shrunken = NamesTableView.DecidePage(2500, 1000, 9, 42);
    Assert.AreEqual(0, shrunken.PageIndex);
    Assert.IsTrue(shrunken.JumpToSelection);
  }

  [TestMethod]
  public void TheQuestionIsWhatABuildTravelsWith()
  {
    var a = NamesTableView.For("ann", new[] { IdentityKind.Player, IdentityKind.Pet }, "Type", false);
    var b = NamesTableView.For("ann", new[] { IdentityKind.Pet, IdentityKind.Player }, "Type", false);
    Assert.AreEqual(a, b, "{Player, Pet} and {Pet, Player} are the same question — order in the multi-select is not identity");

    var kinds = new[] { IdentityKind.Player, IdentityKind.Pet };
    Assert.AreNotEqual(a, NamesTableView.For("Ann", kinds, "Type", false), "the filter word is case-sensitive as IDENTITY: 'ann' and 'Ann' ask different builds, and only one of them paints");
    Assert.AreNotEqual(a, NamesTableView.For("", kinds, "Type", false), "an empty filter is a different question from a typed word");
    Assert.AreNotEqual(a, NamesTableView.For("ann", kinds, "Name", false), "a header click is a new question");
    Assert.AreNotEqual(a, NamesTableView.For("ann", kinds, "Type", true), "flipping the direction is a new question");
  }

  [TestMethod]
  public void TheBuildKeepsWhatPassesAndOrdersTheSurvivors()
  {
    var rows = new List<string> { "Dora", "Ann", "bob", "Cyr" };
    var kept = NamesTableView.FilterOrdered(rows, r => r.Contains("o", StringComparison.OrdinalIgnoreCase), (x, y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase));

    CollectionAssert.AreEqual(new[] { "bob", "Dora" }, kept, "the filter keeps case-insensitively and the survivors stay ordered");

    var none = NamesTableView.FilterOrdered(rows, _ => false, (x, y) => 0);
    Assert.AreEqual(0, none.Count, "a question nobody passes is an empty list, not a crash");

    var all = NamesTableView.FilterOrdered(rows, _ => true, (x, y) => string.Compare(y, x, StringComparison.OrdinalIgnoreCase));
    Assert.AreEqual(4, all.Count);
    Assert.AreEqual("Dora", all[0], "the comparison says descending, so the list obeys it");
  }
}
