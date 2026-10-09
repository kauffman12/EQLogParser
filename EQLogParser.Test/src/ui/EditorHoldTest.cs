using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * The rule a live pane needs and easily forgets: while the reader is inside an editor, nothing repaints (EditorHold). The field
 * report this came from — "after changing a player to NPC from the damage summary i wasnt able to change the type or reset claim in
 * the identity window. id select an option from the dropdown and nothing happened" — is a census landing mid-gesture: the merge
 * replaces the cell the popup is anchored to, WPF closes the popup, the close hook clears the row being edited, and the click
 * arrives with no subject. These pin the mechanism the pane relies on; the pane itself is the Wpf assembly's
 * ACensusWaitsForAnOpenCellEditorRatherThanYankingTheRowOutFromUnderIt.
 */
[TestClass]
public sealed class EditorHoldTest
{
  [TestMethod]
  public void NothingIsHeldUntilAnEditorOpens()
  {
    var hold = new EditorHold();

    Assert.IsFalse(hold.IsOpen);

    var landed = false;
    Assert.IsFalse(hold.Defer(() => landed = true), "with no editor open the caller applies its own work");
    Assert.IsFalse(landed, "and Defer did not run it behind the caller's back");
  }

  [TestMethod]
  public void HeldWorkRunsWhenTheLastEditorCloses()
  {
    var hold = new EditorHold();
    var landed = false;

    hold.Open();
    Assert.IsTrue(hold.Defer(() => landed = true), "an open editor takes the work off the caller's hands");
    Assert.IsFalse(landed, "and nothing landed while the gesture held");

    hold.Close();
    Assert.IsTrue(landed, "the moment the gesture ends, the held work runs");
  }

  [TestMethod]
  public void OnlyTheNewestHeldWorkSurvives()
  {
    var hold = new EditorHold();
    var olderRan = false;
    var newestRan = false;

    hold.Open();
    Assert.IsTrue(hold.Defer(() => olderRan = true));
    Assert.IsTrue(hold.Defer(() => newestRan = true));

    hold.Close();

    Assert.IsTrue(newestRan);
    Assert.IsFalse(olderRan, "an older repaint describes a capture that has already moved on — chaining it would paint the stale one");
  }

  [TestMethod]
  public void AnIntermediateCloseDoesNotPaintWhileAnotherEditorIsOpen()
  {
    var hold = new EditorHold();
    var landed = false;

    hold.Open();   // the Type popup
    hold.Open();   // and a Class popup, in a pane that can hold both

    Assert.IsTrue(hold.Defer(() => landed = true));

    hold.Close();
    Assert.IsFalse(landed, "one of two editors closing is not the end of the repaint block");

    hold.Close();
    Assert.IsTrue(landed);
  }

  [TestMethod]
  public void AStrayCloseCannotMakeTheNextOpenShort()
  {
    var hold = new EditorHold();

    hold.Close();   // nothing was open: a mismatched hook must not park the count below zero
    Assert.IsFalse(hold.IsOpen);

    hold.Open();
    var landed = false;
    Assert.IsTrue(hold.Defer(() => landed = true), "the next real editor still gets its hold");
    Assert.IsFalse(landed);

    hold.Close();
    Assert.IsTrue(landed);
  }

  [TestMethod]
  public void AbandoningDropsTheHeldWorkAndTheHold()
  {
    var hold = new EditorHold();
    var landed = false;

    hold.Open();
    Assert.IsTrue(hold.Defer(() => landed = true));

    hold.Abandon();   // the capture this repaint described is gone

    Assert.IsFalse(landed, "painting a closed log's rows over whatever is open now is the bug this path exists to avoid");
    Assert.IsFalse(hold.IsOpen);

    Assert.IsFalse(hold.Defer(static () => { }), "and the pane is live again straight away — nothing latched the hold shut");
  }
}
