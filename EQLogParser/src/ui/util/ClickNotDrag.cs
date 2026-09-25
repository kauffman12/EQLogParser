using System;
using System.Windows;

namespace EQLogParser
{
  /*
   * Deciding whether the left button went *down and back*, or *down and dragged*, for a control that has to do two
   * jobs with the same button: click it to open a chooser, drag across it to select its text.
   *
   * This exists for the sound path cell of the trigger property grid (`TextSoundEditor`), where a file picked out of
   * the file system used to be a dead display: the only way back to the chooser was to move the options dropdown off
   * "Browse for Sound File" and pick it again, because re-picking the item already showing raises no SelectionChanged
   * at all. Making the path clickable answers that, but a click handler on its own buys back the same complaint in a
   * new shape — the mouse-down that begins a selection is the same event as the click, so anything attached to
   * `MouseLeftButtonDown` opens a dialog every time a player reaches over to copy the path. The two gestures differ in
   * the *middle* of the press rather than at its ends, so that is what gets measured here.
   *
   * Three rules, each covering a case the naive version — compare the press point against the release point — gets
   * wrong:
   *
   * - **The excursion latches.** `OnMouseMove` remembers once the pointer has been out past the tolerance, so a
   *   selection drag that wanders off and happens to come back over its own start still reads as a drag. Comparing
   *   only the press point against the release point let that one open a dialog on top of the selected text.
   * - **The tail of a double-click never arms.** `ClickCount` above 1 means this press belongs to the word-selecting
   *   gesture, and the first press of that pair already had its turn at the chooser; answering again would stack a
   *   second dialog behind the one that is opening. So the word stays the text box's business.
   * - **Nothing fires without a press.** A release with no armed press — the button went down somewhere else and
   *   came up here, or a drag left the control — answers false, so focus changes cannot summon a dialog.
   *
   * The tolerance is fixed rather than read from `SystemParameters.MinimumHorizontalDragDistance` on purpose: that
   * dial is the shell's `SM_CXDRAG` — a few pixels, and tweakable in the mouse control panel — so a test written
   * against it changes results when somebody adjusts their mouse. Six device-independent units sit past the jitter of
   * a click and at about the width of one character, so the movement lost to it selects nothing worth keeping.
   */
  internal sealed class ClickNotDrag
  {
    /// Past a click's jitter, about one character wide: see the note above on why this is not `SystemParameters`.
    internal const double DefaultTolerance = 6.0;

    private readonly double _tolerance;
    private bool _armed;
    private bool _moved;
    private Point _press;

    public ClickNotDrag(double tolerance = DefaultTolerance)
    {
      // A tolerance of zero (or junk from a caller) would make every tremor of a click a selection, so it is not
      // allowed to mean anything other than the default.
      _tolerance = tolerance > 0 ? tolerance : DefaultTolerance;
    }

    /// Call from `PreviewMouseLeftButtonDown`. Only a first click arms; a second one belongs to the text box.
    internal void OnMouseDown(Point position, int clickCount)
    {
      _press = position;
      _armed = clickCount == 1;
      _moved = false;
    }

    /// Call from `PreviewMouseMove`: latches that this gesture is a selection, permanently for this press.
    internal void OnMouseMove(Point position)
    {
      if (_armed && MovedFromPress(position))
      {
        _moved = true;
      }
    }

    /// Call from `MouseLeftButtonUp`. True only for a press that never dragged: the chooser may open.
    internal bool OnMouseUp(Point position)
    {
      if (!_armed)
      {
        return false;
      }

      _armed = false;
      return !_moved && !MovedFromPress(position);
    }

    private bool MovedFromPress(Point position) =>
      Math.Abs(position.X - _press.X) > _tolerance || Math.Abs(position.Y - _press.Y) > _tolerance;
  }
}
