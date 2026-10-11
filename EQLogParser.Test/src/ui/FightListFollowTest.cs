using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser.Tests.derive;

/*
 * The fight list's follow-the-newest rule (EQLogParser.Core/src/ui/FightListFollow.cs) — the behaviour the old table had and the derived one
 * lost: a capture keeps putting fights at the bottom of this list, and the pane used to leave the viewport wherever the reader had left it, so
 * a live raid's newest pull happened off-screen. It is tested as four separate refusals because the ONE permission (rows arrived) is cheap and
 * every way of losing it costs something different: scrolling while a hand is inside the grid yanks the rows out from under a selection, and
 * scrolling a pane nobody has opened spends layout work on an invisible table.
 */
[TestClass]
public class FightListFollowTest
{
  [TestMethod]
  [Description("Rows arrived on a visible, untouched list: go to the newest")]
  public void GrowthIsFollowed()
  {
    Assert.IsTrue(FightListFollow.ShouldFollowToNewest(rowsArrived: true, paneOnScreen: true, mouseOverGrid: false, shownRowCount: 12));
  }

  [TestMethod]
  [Description("A pass that changed no row count does not move the viewport")]
  public void StillnessIsNotFollowed()
  {
    Assert.IsFalse(FightListFollow.ShouldFollowToNewest(rowsArrived: false, paneOnScreen: true, mouseOverGrid: false, shownRowCount: 400),
      "the same fights re-counted are not news; the reader's place in the list is");
  }

  [TestMethod]
  [Description("A mouse inside the grid means somebody is reading — no scroll, however many rows arrived")]
  public void AHandOnTheGridKeepsTheViewport()
  {
    Assert.IsFalse(FightListFollow.ShouldFollowToNewest(rowsArrived: true, paneOnScreen: true, mouseOverGrid: true, shownRowCount: 400),
      "legacy's one exception, kept: being slid down mid-read is worse than missing a row");
  }

  [TestMethod]
  [Description("A pane that is not on screen spends nothing on its viewport")]
  public void AHiddenPaneStaysPut()
  {
    Assert.IsFalse(FightListFollow.ShouldFollowToNewest(rowsArrived: true, paneOnScreen: false, mouseOverGrid: false, shownRowCount: 400));
  }

  [TestMethod]
  [Description("One row is not growth — with nothing below the fold there is nowhere to follow")]
  public void ASingleRowIsNowhereToGo()
  {
    Assert.IsFalse(FightListFollow.ShouldFollowToNewest(rowsArrived: true, paneOnScreen: true, mouseOverGrid: false, shownRowCount: 1));
    Assert.IsFalse(FightListFollow.ShouldFollowToNewest(rowsArrived: true, paneOnScreen: true, mouseOverGrid: false, shownRowCount: 0));
    Assert.IsTrue(FightListFollow.ShouldFollowToNewest(rowsArrived: true, paneOnScreen: true, mouseOverGrid: false, shownRowCount: 2),
      "the floor is about a list with a fold, not about waiting for two fights");
  }
}
