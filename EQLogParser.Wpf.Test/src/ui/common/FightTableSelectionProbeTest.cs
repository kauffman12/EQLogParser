using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * The fight list's "is a button still down" probe (FightTable.PointerIsDown), which SelectionSettle consults on every
 * settle tick before letting a selection reach the boards.
 *
 * Why this is worth an STA thread and two asserts: the probe runs INSIDE the timer's tick, on the pane's own dispatcher,
 * with no window guaranteed to exist (the docked pane can be hidden, and the timer is armed long before anything is
 * arranged). If the query threw — or answered "pressed" while nothing is pressed — the failure would be silent in the worst
 * direction: ShouldAnnounce() returns false, the pane keeps restarting its timer, and the boards simply never update again.
 * That reads to a user as "the stats froze", which is the exact complaint class this pane has been rebuilt around.
 *
 * Both buttons are in the contract on purpose: SfDataGrid selects on a right-drag as well as a left one, so watching only
 * the left button would let a right-drag announce mid-gesture — which is what the operator asked about ("right click …
 * immediately selects a row", "if you still held the button down it wouldnt do that").
 */
[TestClass]
public sealed class FightTableSelectionProbeTest
{
  [TestMethod]
  [Description("The held-button probe answers without throwing, and says no when nothing is pressed")]
  public void ThePointerProbeAnswersOnAThreadWithNoWindow()
    => Sta.Run(() =>
    {
      // No Application, no window, no input device touched: exactly the state a hidden docked pane's timer runs in.
      Assert.IsFalse(FightTable.PointerIsDown(), "nothing is held, so a settle tick may announce");

      // Queried twice, because the pane asks it on every tick and a first-call lazy-init failure would look intermittent.
      Assert.IsFalse(FightTable.PointerIsDown());
    });
}
