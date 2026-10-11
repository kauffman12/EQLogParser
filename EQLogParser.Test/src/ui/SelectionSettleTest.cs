using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser.Tests.derive;

/*
 * The fight list's announcement gate (EQLogParser.Core/src/ui/SelectionSettle.cs), tested without a dispatcher the same
 * way DeriveCadence is: the rule is "what is pending, and is this tick allowed to announce it", and pumping a real
 * DispatcherTimer cannot tell a deferral from a fire. Two user-visible defects this replaces, both reported from the
 * derived pane after the legacy table was deleted:
 *
 *   - right-clicking a row announced THAT row's boards a moment later (SfDataGrid moves its current cell when the menu
 *     opens), so "Select All" then looked like two selections: one rebuild for the row that was merely clicked, one for
 *     everything;
 *   - a drag announced while the button was still down;
 *   - a menu whose closing event never reached the pane, which - latched alone - parked that gesture and every later one in the session
 *     ("ctrl-clicked a row off and the summary never updated - other times it looks deselected but the event never worked"). The last two
 *     are why both deferrals are QUERIED rather than only counted.
 */
[TestClass]
public class SelectionSettleTest
{
  [TestMethod]
  [Description("A change with nothing in the way announces on the next tick, and the same change never fires twice")]
  public void AQuietChangeAnnouncesOnce()
  {
    var gate = new SelectionSettle();

    Assert.IsFalse(gate.ShouldAnnounce(), "an untouched pane has nothing to announce");

    gate.Changed();
    Assert.IsTrue(gate.Pending);
    Assert.IsTrue(gate.ShouldAnnounce());

    Assert.IsFalse(gate.Pending, "the announcement consumed the change");
    Assert.IsFalse(gate.ShouldAnnounce(), "a second tick over the same selection must not rebuild the boards again");
  }

  [TestMethod]
  [Description("While the right-click menu is open nothing announces; the pending change survives the tick")]
  public void AChangeBehindTheMenuWaits()
  {
    var gate = new SelectionSettle();

    // The click that opens the menu selects its row: SelectionChanged, then the menu is up before any tick lands.
    gate.Changed();
    gate.MenuOpen = true;

    Assert.IsFalse(gate.ShouldAnnounce(), "the boards must not rebuild for a row that was only right-clicked");
    Assert.IsTrue(gate.Pending, "the change is parked, not dropped - the pane keeps asking");
  }

  [TestMethod]
  [Description("Closing the menu announces exactly once; closing it again says nothing")]
  public void TheMenuCloseAnnouncesTheParkedChangeOnce()
  {
    var gate = new SelectionSettle();
    gate.Changed();
    gate.MenuOpen = true;
    Assert.IsFalse(gate.ShouldAnnounce());

    Assert.IsTrue(gate.CloseMenu(), "whatever the menu did - or the click that opened it - is due now");
    Assert.IsFalse(gate.Pending);
    Assert.IsFalse(gate.CloseMenu(), "a menu closed with nothing pending must not re-run the boards");
  }

  [TestMethod]
  [Description("A menu open and closed with no selection change in between announces nothing")]
  public void AnIdleMenuCloseIsSilent()
  {
    var gate = new SelectionSettle();
    gate.MenuOpen = true;

    Assert.IsFalse(gate.CloseMenu());
  }

  [TestMethod]
  [Description("A menu whose closing event never arrives stops parking gestures the moment it is actually gone")]
  public void AMenuThatNeverSaidGoodbyeCannotParkTheSession()
  {
    var open = false;
    var gate = new SelectionSettle(null, () => open);

    // Ctrl-click deselects a row; the right-click that follows puts a menu over the grid.
    gate.Changed();
    open = true;
    Assert.IsFalse(gate.ShouldAnnounce(), "while a menu is genuinely open the boards wait for the gesture to end");

    // Now the pane never hears about the close - no ContextMenuClosing, a click-through, anything. The probe is what notices.
    open = false;
    Assert.IsTrue(gate.ShouldAnnounce(),
      "the menu is gone and the deselection is still parked; a latch that outlives its menu would leave every later click unanswered too");
    Assert.IsFalse(gate.Pending, "and it announces once, like any other release");
  }

  [TestMethod]
  [Description("The open/close events still drive the deferral even where the probe cannot see the menu")]
  public void TheMenuEventsStillHoldTheirOwn()
  {
    var gate = new SelectionSettle(null, () => false);

    gate.Changed();
    gate.MenuOpen = true;
    Assert.IsFalse(gate.ShouldAnnounce(), "an opening event is honoured on its own - the menu may exist before the probe can read it");
    Assert.IsTrue(gate.CloseMenu(), "and the close still releases exactly what was parked behind it");
  }

  [TestMethod]
  [Description("A held button parks the announcement, and releasing it lets the same change through")]
  public void ADragAnnouncesOnlyAfterTheButtonIsUp()
  {
    var held = false;
    var gate = new SelectionSettle(() => held);

    gate.Changed();
    held = true;
    Assert.IsFalse(gate.ShouldAnnounce(), "mid-drag the boards stay put");
    Assert.IsTrue(gate.Pending);

    held = false;
    Assert.IsTrue(gate.ShouldAnnounce());
    Assert.IsFalse(gate.Pending);
  }

  [TestMethod]
  [Description("Dropping every row (new session, wholesale snapshot) discards a parked change with them")]
  public void AResetClearsWhatWasParked()
  {
    var gate = new SelectionSettle { MenuOpen = true };
    gate.Changed();
    Assert.IsTrue(gate.Pending);

    gate.Reset();
    Assert.IsFalse(gate.Pending);
    Assert.IsFalse(gate.CloseMenu(), "the rows those ids named are gone; announcing them would re-materialize a stale selection");
  }
}
