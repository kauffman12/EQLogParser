namespace EQLogParser;

/*
 * The fight list's viewport, entire: the list moves to its newest row ONCE per capture, when that capture's file has finished reading, and for no
 * other reason ever. Not when rows arrive, not when a derive pass lands, not on a timer — and nothing in this rule asks where the mouse pointer is
 * or whether anybody is looking at the tab (docs/DesignNotes.md → "The fight list moves its own viewport once").
 *
 * Legacy scrolled on EVERY pass that added rows, excused only by `!dataGrid.IsMouseOver`. At one row per pull that read as liveness; with a derive
 * pump handing this pane a pass about twice a second it reads as a hijacked pane — and the excuse was never protection, since the moment the pointer
 * left the list slid down again. The operator's word for it: don't move the list except after the whole log has been read.
 *
 * Why "finished reading" is its own fact rather than "the first snapshot": the order is not fixed. A short log reads out before any derive runs, so
 * rows arrive after the read; a big one reaches 100 % while its first capture is still being projected — the loading band itself gives up at the
 * first snapshot ("real rows beat a progress bar"), so keying off rows landing would fire the jump a third of the way through a file and then never
 * fire again. Hence two halves: `ReadFinished` from the reader's own 100 % announcement, `Arm` per capture (a reopen or Clear All is a new load), and
 * `TryPay` asked by both doors so either order produces exactly one payment.
 *
 * `TryPay` withholds payment only while there is nothing to reveal — a capture can finish reading before its first rows exist, and an empty grid is
 * not an answer. Once an answer exists the debt is spent whatever the caller then does with it: a jump left owed into somebody's pull is precisely
 * the surprise this rule retires. No scroll position is stored anywhere, so there is nothing to restore and no heuristic guessing intent; after the
 * one reveal the only thing that moves these rows is a search, and a search is a gesture.
 */
internal sealed class FightListReveal
{
  private bool _owed = true;
  private bool _readFinished;

  /// <summary>A capture opened: the jump to its newest row is owed again, and this read has not finished.</summary>
  internal void Arm()
  {
    _owed = true;
    _readFinished = false;
  }

  /// <summary>The reader reported the file done (100 %), which is the moment an operator watches.</summary>
  internal void ReadFinished() => _readFinished = true;

  /// <summary>May the viewport go to the newest row now? Consumes the debt on the first answer that has rows in it.</summary>
  internal bool TryPay(bool hasRows)
  {
    if (!_owed || !_readFinished || !hasRows) return false;

    _owed = false;
    return true;
  }
}
