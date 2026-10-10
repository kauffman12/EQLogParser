using System;
using System.Windows.Threading;

namespace EQLogParser
{
  /*
   * A busy state that waits to be earned, for a pane whose work runs on another thread.
   *
   * The old shape of this was a switch statement told "the build started", which cleared the table and wrote
   * "Calculating DPS..." on the spot. That cannot be made reliable, because the answer arrives from wherever the build
   * ran and both messages queue at the dispatcher's Normal priority: when the two land in the same drain — a fast build,
   * or a UI thread stuck behind a chart update or a garbage collection — the pending paint of the cleared table is behind
   * the item that fills it back in, so the busy state is never drawn and the pane goes stale-to-fresh without a word. A
   * slow build usually wins its frame, which is why this looked random rather than broken.
   *
   * So the pane does not promise anything at t=0. It arms, and only a build STILL running after ShowAfterMs says so —
   * said from this class's own timer tick, on the UI thread, with the build demonstrably still out there. That is the
   * one moment the promise can be kept: there is nothing left to race, because the state change and its paint are both
   * ahead of a completion that has not happened yet.
   *
   * It also removes the flicker that racing used to hide. A single fight's board rebuilds in 2 ms and a group of fifteen
   * in ~50 (measured, EQLogParser.log); blanking a table to announce work that finishes before the next frame is not
   * information, it is a strobe — every settle tick of a live raid, three tables at once. What a reader should see during
   * those is the previous table, which is wrong by milliseconds and legible, rather than nothing at all.
   */
  internal sealed class DeferredBusyState
  {
    /*
     * Long enough that a pane which answers inside it never shows a busy state at all — one frame at 60 Hz is 17 ms, so
     * anything under this was going to be invisible anyway — and short enough that the wait past it is what a person
     * starts to call frozen. It was `PerfJournal.SlowPassMs` while that existed - one number for "this took long enough to be
     * worth telling somebody about", shared by the log and the interface - and it keeps the same value now that the journal is
     * gone, because the reasoning that picked 150 ms still holds: one frame at 60 Hz is 17 ms, so anything shorter was never
     * going to be seen, and a person reading a table that is milliseconds stale is reading the right answer.
     */
    internal const int ShowAfterMs = 150;

    private readonly DispatcherTimer _timer;
    private Action _show;

    internal DeferredBusyState(int showAfterMs = ShowAfterMs)
    {
      _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(showAfterMs) };
      _timer.Tick += (_, _) =>
      {
        // One shot per request: stopping here is what keeps a build that takes ten seconds from repeating its own
        // "still working" state every interval, which would re-trigger whatever the action invalidates.
        _timer.Stop();
        _show?.Invoke();
      };
    }

    /// <summary>Promise nothing yet, and say so when this much time has passed with nobody cancelling.</summary>
    internal void Arm(Action showBusy)
    {
      _show = showBusy;
      _timer.Stop();
      _timer.Start();
    }

    /*
     * Called for EVERY state of a build's event stream, STARTED included (which arms again immediately after), because
     * the danger is an armed timer whose answer already arrived: a late tick would then blank a table that is showing
     * real rows. Cancelling first makes each event replace the last request rather than queue behind it.
     *
     * The same reason covers the two paths that are NOT build events: hiding a pane and clearing the capture. Both empty
     * the surface for a reason no build will answer, so both cancel — otherwise the tick fires later at a table nobody is
     * looking at (and, after a hide, at whatever is on screen when it is shown again: blank grid plus "Calculating DPS..."
     * over an answer that never comes).
     */
    internal void Cancel()
    {
      _show = null;
      _timer.Stop();
    }
  }
}
