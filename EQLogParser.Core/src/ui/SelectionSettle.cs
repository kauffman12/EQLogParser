namespace EQLogParser;

/*
 * When a fight-list selection may be announced to the boards. Pure logic, no dispatcher: the pane owns the timer,
 * this decides whether a tick is allowed to fire. It lives in Core for the same reason DeriveCadence does - the rule
 * is what needs testing, and a test that has to pump a real Dispatcher cannot see the difference between "deferred"
 * and "fired".
 *
 * Two deferrals, both learned by users clicking rather than by measurement:
 *
 *   1. **While the right-click menu is open.** A context menu opens on top of a selection change - SfDataGrid moves
 *      its current cell to the row under the cursor, which raises SelectionChanged like any click. Announcing THAT
 *      means the boards rebuild once for the row that was merely right-clicked and again for whatever the menu
 *      actually did (Select All being the loudest case: the pane materializes a whole capture's rows twice, seconds
 *      apart, which is what "it ends up doing two selections" felt like). Legacy handled this with a
 *      `_needSelectionChange` flag and `rightClickMenu.IsOpen`, and it is ported here rather than reinvented: pending
 *      work announced exactly once, when the menu closes.
 *
 *   2. **While the mouse button is still down.** A drag across twenty rows raises SelectionChanged per row; the
 *      settle window alone handles that, but a drag that ENDS inside the window would still spend a rebuild mid-gesture.
 *      The pane passes a probe (WPF: `Mouse.LeftButton == MouseButtonState.Pressed`) so the state is queried, never
 *      accumulated - a missed button-up cannot wedge the gate shut the way a flag set on down and cleared on up can.
 */
internal sealed class SelectionSettle(Func<bool> pointerHeld = null)
{
  private bool _pending;

  /// <summary>True while the context menu is open (the pane sets this from ContextMenuOpening/Closing).</summary>
  internal bool MenuOpen { get; set; }

  /// <summary>Something changed and has not been announced yet - the pane restarts its timer while this is true.</summary>
  internal bool Pending => _pending;

  /// <summary>A selection changed. The caller restarts its own timer; nothing fires from here.</summary>
  internal void Changed() => _pending = true;

  /*
   * The timer fired: may the pane announce? No while the menu is open (CloseMenu will announce), no while the button
   * is held (the caller restarts the timer, which is why Pending stays true here rather than being consumed).
   */
  internal bool ShouldAnnounce()
  {
    if (!_pending) return false;
    if (MenuOpen) return false;
    if (pointerHeld?.Invoke() == true) return false;

    _pending = false;
    return true;
  }

  /// <summary>The menu closed. Returns true when a change was parked behind it and must be announced now, once.</summary>
  internal bool CloseMenu()
  {
    MenuOpen = false;
    if (!_pending) return false;

    _pending = false;
    return true;
  }

  /// <summary>The pane dropped every row (new session, new snapshot wholesale): no pending work survives it.</summary>
  internal void Reset() => _pending = false;
}
