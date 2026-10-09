using System;
using System.Threading;

/*
 * Annotations only, no null-flow analysis: the project builds with Nullable=disable, and this type's one optional member is a
 * queued repaint that legitimately may not exist yet.
 */
#nullable enable annotations

namespace EQLogParser;

/*
 * A pane must not repaint out from under a gesture the reader is in the middle of making.
 *
 * The shape that made this necessary (2026-10-09, from "after I changed a name from the damage summary, the identity window's
 * dropdown stopped responding"): the Names window follows the derive and repaints every couple of seconds, a verdict written from
 * ANOTHER pane forces a pass immediately, and a census landing while a cell popup is open tears out the very cell the popup is
 * anchored to. WPF closes the popup, its close hook clears "which row am I editing", and the click that was already on its way
 * arrives with no subject — so the write is silently dropped and the operator sees a dropdown that does nothing. Re-pointing the
 * edited row by name (MergeRows does) cannot save it: the popup is gone before the re-point matters.
 *
 * So the hold is not an optimisation, it is the correctness boundary: while an editor is open, no repaint lands; the newest one
 * runs on the last close. Newest-wins because an older census describes a capture that has already moved on — running two would
 * paint the stale one last depending on thread timing. Counting (rather than a bool) because a pane can hold more than one kind
 * of editor and each closes on its own gesture.
 */
internal sealed class EditorHold
{
  private int _open;
  private Action? _pending;

  internal bool IsOpen => Volatile.Read(ref _open) > 0;

  /// <summary>An editor opened. Pair every call with Close().</summary>
  internal void Open() => Interlocked.Increment(ref _open);

  /*
   * Close one editor. The queued work runs on the LAST close and never while another editor is still open — an intermediate
   * close is not the end of the gesture. A stray Close with nothing open cannot push the count negative, because a negative
   * count would make the NEXT Open leave the pane one repaint short.
   */
  internal void Close()
  {
    if (Interlocked.Decrement(ref _open) > 0) return;

    Volatile.Write(ref _open, 0);
    Interlocked.Exchange(ref _pending, null)?.Invoke();
  }

  /*
   * True when the work was STORED because an editor is open — and the caller must then not do it itself. False means nothing is
   * open: the caller applies its own work as it always did. Any older queued work is dropped rather than chained.
   */
  internal bool Defer(Action work)
  {
    if (!IsOpen) return false;

    Interlocked.Exchange(ref _pending, work);
    return true;
  }

  /*
   * Forget both the hold and the queued work: the capture this repaint described is gone (or the pane is), so landing it later
   * would paint a closed log's names over whatever is open now. Callers pair this with their own clear.
   */
  internal void Abandon()
  {
    Volatile.Write(ref _open, 0);
    Interlocked.Exchange(ref _pending, null);
  }
}
