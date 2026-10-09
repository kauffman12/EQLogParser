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
  /*
   * The identity strip in markup: SideInDockedMode="Right" PLUS CanDocument="False" — every one of the strip's panes is written
   * that way, and it is the PAIR that says "this window belongs to a strip". Panes are dockable (that is what let the old ladder
   * grab them and drop them over the tables), so neither property alone is the declaration.
   */
  [TestMethod]
  public void APaneOnAnEdgeThatCannotBeADocumentComesBackAutoHidden()
  {
    Sta.Run(() =>
    {
      foreach (var side in new[] { DockSide.Left, DockSide.Right, DockSide.Top, DockSide.Bottom })
      {
        Assert.AreEqual(DockState.AutoHidden, SyncFusionUtil.ShowStateFor(MakePane(side)),
                        $"a strip pane ({side}) was handed back a state other than AutoHidden — showing it moves it off the edge");
      }
    });
  }

  /*
   * THE CHART REGRESSION, reported from a Windows run: "the charts open in strange places — they used to open in the main tab
   * container, but one opened as an undocked popup and one docked on the left".
   *
   * The side property is NOT a trustworthy declaration of where a window belongs, and this assembly already knew it (see the note
   * below: a pane set to Tabbed reads back as an edge from the vendor's getter). `AddDocument` creates every chart with
   * SideInDockedMode=Tabbed, so a rule that asks the side FIRST hands charts whatever the coercion says — a strip on some edge —
   * and a window that does not affirm it can be a document falls through CanDock (which AddDocument's own _CreateControl turns
   * off) to Float. Asking document-ness first is the fix: the two properties markup controls are the two the getter answers
   * truthfully about, and the side only decides for windows that cannot be documents at all.
   */
  [TestMethod]
  public void AWindowThatCanBeADocumentComesBackAsADocumentWhateverSideItWears()
  {
    Sta.Run(() =>
    {
      foreach (var side in new[] { DockSide.Left, DockSide.Right, DockSide.Top, DockSide.Bottom, DockSide.Tabbed })
      {
        var chart = MakePane(side, document: true, dock: false, canFloat: true);
        Assert.AreEqual(DockState.Document, SyncFusionUtil.ShowStateFor(chart),
                        $"a document-capable window wearing side {side} was not handed back Document — the menu opens it outside " +
                        "the tab container (float or a strip), which is the reported regression");
      }
    });
  }

  /*
   * Nothing showable answers Hidden rather than somewhere arbitrary: with no document, dock or float permission the caller logs a
   * warning instead of inventing a place for the window.
   */
  [TestMethod]
  public void AWindowWithNoShowableStateIsReportedRatherThanPlaced()
  {
    Sta.Run(() =>
    {
      var stuck = MakePane(DockSide.None);
      Assert.AreEqual(DockState.Hidden, SyncFusionUtil.ShowStateFor(stuck),
                      "a window with nothing to show was handed a state anyway: ToggleWindow would place it somewhere arbitrary");
    });
  }

  /*
   * REMOVED, with the reason kept where the missing coverage will be missed: this method used to assert Document / Dock / Float
   * for panes whose `SideInDockedMode` the TEST set to Tabbed and None. On Windows a pane set to Tabbed came back AutoHidden from
   * `DockingManager.GetSideInDockedMode`, i.e. the getter does not return what was set, so every one of those assertions measured
   * the vendor's property coercion rather than the app's decision — a test that can only ever confirm the assumption it was
   * written from. The middle-window shape is now covered by what is actually checkable: `EQLogParser.Test/src/ui/DockingMarkupTest.cs`
   * pins what MainWindow DECLARES (the identity strip's Right/AutoHidden, the retired panes' Hidden), and docs/ReleaseChecklist.md
   * carries the two states that need a live layout — showing a pane hidden behind another tab, and restarting with a dockSite.xml
   * saved by an older build. `APaneOnAnEdgeComesBackAutoHidden` above stays: it is the shipped bug ("it stopped working after I
   * restart"), it is green on real vendor values, and ShowStateFor's edge branch is what prevents the relocation.
   */

  /*
   * Built as the XAML builds it: a strip pane is an edge with CanDocument=False (the default here), and the tests that want the
   * other shape pass document:true. CanDock/CanFloat are off by default so a test states which permission it is about — the one
   * thing a window must NOT have to earn its own shape back.
   */
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
