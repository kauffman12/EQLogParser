namespace EQLogParser;

/*
 * The one question the post-load collection has to ask across windows: IS A CLEANUP STILL ON ITS WAY?
 *
 * MainWindow pays it (GC law's two moments — a load that read lines, and Clear All) and owns every decision about whether this
 * capture is still the open one. The fight list owns the loading band over its grid, and the band is what makes the pause legible:
 * a stop-the-world with a bar above it reads as work, the same pause in the middle of an answered grid reads as a hang ("it didn't
 * garbage collect after the log finished loading", 2026-10-13). Note what this does NOT buy: a blocking full collection suspends
 * every managed thread, dispatcher included, so nothing paints while the pass runs and no thread choice changes that — the hold makes
 * sure the bar is still standing WHEN THREADS RESUME, which is the moment the rows of a long capture land (the cadence starts the first
 * derive as soon as the lanes drain).
 *
 * So this is a counted mark, not a queue: nothing here cancels, coalesces or waits for a collection (GC.Collect is not cancellable, and
 * a second seam for it is exactly what an earlier cut built and then deleted).
 *
 *   - Mark/Clear pair one pass: MainWindow marks on the way in and clears in a finally on the way out. Counted rather than a flag because
 *     the UI stays live through the pass, so Clear All can mark while a load's collection is still running; a flag would let whichever
 *     finally ran first take the other one's bar down mid-pass. Each pass releases exactly its own hold, and a stuck mark is impossible:
 *     every Mark has a finally, and FightTable caps its own wait regardless (PostLoadCleanup holds the band with a deadline of its own).
 *   - A pass releases its hold ONLY while it still owns the capture. The only way a session stops being the open one is a door, and a door
 *     has already reset the count — clearing after that would steal the hold of the capture that replaced it.
 *   - Reset belongs to the capture doors (OpenLogFile/CloseLogFile): a debt never crosses a capture, and one in-flight pass of the file
 *     that just closed must not park the NEXT session's band for a cleanup that is not its business. Reset drops the whole count,
 *     including the hold of a pass that is still running — that pass cannot be cancelled, and the Clear in its finally stops at zero rather
 *     than digging under it.
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
