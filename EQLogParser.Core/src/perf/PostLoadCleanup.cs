namespace EQLogParser;

/*
 * The one question the post-load collection asks across windows: IS A CLEANUP STILL ON ITS WAY? MainWindow pays the collection and owns every
 * decision about which capture is open; FightTable owns the band, and the band is what makes a stop-the-world legible. It buys only that the bar
 * is still standing when threads resume — a blocking full collection suspends every managed thread, so nothing paints during it and no thread
 * choice changes that.
 *
 * A counted mark, not a queue: GC.Collect is not cancellable, so nothing here cancels, coalesces or waits.
 *   - Mark/Clear pair one pass (cleared in a finally), counted because the UI stays live through it and Clear All can mark while a load's
 *     collection runs; each pass releases exactly its own hold, and FightTable caps its wait with a deadline of its own.
 *   - A pass releases its hold only while it still owns the capture: a door already reset the count, and clearing after that steals the
 *     replacement's hold.
 *   - Reset belongs to the capture doors (OpenLogFile/CloseLogFile): a debt never crosses a capture.
 */
internal static class PostLoadCleanup
{
  /// <summary>The words this pause wears, in the status line and on the fight list's band — one string so the two cannot drift.</summary>
  internal const string Notice = "Cleaning up..";

  private static int _passes;

  /// <summary>A collection is running (or about to) for this capture: hold the loading band.</summary>
  internal static void Mark() => Interlocked.Increment(ref _passes);

  /// <summary>One pass is over: release its hold, never crossing zero — a capture door may already have reset the count under a pass
  /// that could not be cancelled, and a lingering negative would swallow the NEXT capture's hold (Mark and this read agree on 0).</summary>
  internal static void Clear()
  {
    int current;
    do
    {
      current = Volatile.Read(ref _passes);
      if (current <= 0)
      {
        return;
      }
    }
    while (Interlocked.CompareExchange(ref _passes, current - 1, current) != current);
  }

  /// <summary>A capture opened or closed: nobody's outstanding pass may keep holding a band for a session that is gone.</summary>
  internal static void Reset() => Volatile.Write(ref _passes, 0);

  /// <summary>What the fight list asks before hiding its band on the first rows of a session.</summary>
  internal static bool Owes => Volatile.Read(ref _passes) > 0;

  /// <summary>Take only this capture's debt; an older build must leave a replacement's request intact.</summary>
  internal static bool TryTake(ref int owedSessionId, int sessionId)
    => sessionId != 0 && Interlocked.CompareExchange(ref owedSessionId, 0, sessionId) == sessionId;

  // Invoke and post marshal to the same UI thread as the capture doors. The completion check and Clear must run
  // together there: checking on a worker would let a door Reset/Mark a new hold between that check and Clear.
  // No scheduling policy or retained state here; MainWindow supplies the ownership question and dispatcher calls.
  internal static void RunOwned(Func<bool> ownsCapture, Action<Action> invoke, Action<Action> post,
    Action begin, Action end, Action collect)
  {
    try
    {
      invoke(() =>
      {
        if (ownsCapture()) begin();
      });

      // The capture may have changed while the worker waited for the dispatcher, or after the label was set.
      if (ownsCapture()) collect();
    }
    finally
    {
      post(() =>
      {
        if (!ownsCapture()) return;
        Clear();
        end();
      });
    }
  }
}
