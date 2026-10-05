using Microsoft.VisualStudio.TestTools.UnitTesting;
using Syncfusion.Windows.Tools.Controls;
using System.Windows.Controls;

namespace EQLogParser;

/*
 * "A menu item shows or hides a pane, it never relocates it" — pinned where that policy actually lives.
 *
 * The identity panes sit in the right-hand strip as AutoHidden, and SyncFusionUtil.ToggleWindow used to decide what
 * "show" meant from CanDocument/CanDock/CanFloat alone. For an edge pane those ladder entries mean "put this in the
 * middle of the layout", so a menu item pressed to PEEK at a list yanked it out of its strip — and the operator cannot
 * undo that from the same item, because pressing it again only hides the window where it landed. The decision therefore
 * asks about the pane's OWN declared side before any ladder (SyncFusionUtil.ShowStateFor).
 *
 * Extracted as a pure decision over attached properties for exactly this reason: the ladder's effect needs a realized
 * DockingManager, which needs a live window, and a windowless test host gets neither. What can be measured here is which
 * state a pane is handed back — that is the whole law — and ContentControl + attached properties need only an STA thread.
 */
[TestClass]
public class DockingPaneToggleTest
{
  [TestMethod]
  public void APaneOnAnEdgeComesBackAutoHidden()
  {
    Sta.Run(() =>
    {
      // The identity strip in markup: SideInDockedMode="Right", and the panes are dockable (that is what let the old
      // ladder grab them). The side has to win, or showing Pet Owners / Player-NPC Identity moves it off the edge.
      Assert.AreEqual(DockState.AutoHidden, SyncFusionUtil.ShowStateFor(MakePane(DockSide.Right)),
                      "a right-hand pane was handed back a docking state: the menu item moves the window it opens");

      foreach (var side in new[] { DockSide.Left, DockSide.Right, DockSide.Top, DockSide.Bottom })
      {
        Assert.AreEqual(DockState.AutoHidden, SyncFusionUtil.ShowStateFor(MakePane(side, document: true)),
                        $"an edge pane ({side}) was handed back a state other than AutoHidden — documentable or not");
      }
    });
  }

  [TestMethod]
  public void AWindowInTheMiddleKeepsItsOwnShape()
  {
    Sta.Run(() =>
    {
      // Tabbed is what the markup writes for a window that joins another one's tab group: no edge of its own, so the old
      // ladder still applies to it in its documented order — document, dock, float.
      Assert.AreEqual(DockState.Document,
                      SyncFusionUtil.ShowStateFor(MakePane(DockSide.Tabbed, document: true, dock: true, canFloat: true)));
      Assert.AreEqual(DockState.Dock,
                      SyncFusionUtil.ShowStateFor(MakePane(DockSide.None, dock: true, canFloat: true)));
      Assert.AreEqual(DockState.Float, SyncFusionUtil.ShowStateFor(MakePane(DockSide.None, canFloat: true)));

      // Nothing showable answers Hidden rather than inventing a place for the window; ToggleWindow warns on that.
      Assert.AreEqual(DockState.Hidden, SyncFusionUtil.ShowStateFor(MakePane(DockSide.None)));
    });
  }

  private static ContentControl MakePane(DockSide side, bool document = false, bool dock = false, bool canFloat = false)
  {
    var pane = new ContentControl();
    DockingManager.SetSideInDockedMode(pane, side);
    DockingManager.SetCanDocument(pane, document);
    DockingManager.SetCanDock(pane, dock);
    DockingManager.SetCanFloat(pane, canFloat);
    return pane;
  }
}
