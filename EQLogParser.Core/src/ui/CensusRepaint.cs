using System.Threading;

namespace EQLogParser
{
  /*
   * A repaint somebody asked for while a census was already running, kept from evaporating.
   *
   * The sibling of `DeriveRequests`, one door over — and the same hole. The identity pane answers an operator's verdict with
   * `RederiveAsync()` plus a census of its own to repaint the grid from, and that census answered "one is already in flight" by
   * RETURNING: the answer to a click was nothing at all whenever a build happened to be running. On a finished log nothing else ever
   * asks again, so the store holds the verdict while the grid shows what used to be true, and the repair is to click twice — which is
   * exactly what an operator reports as "the dropdown did nothing the first time". The derived lane learned this and kept a debt for
   * it; a pane that rebuilds on its own needs the same law.
   *
   * Three facts, because two questions are being asked at once:
   *
   *   ONE AT A TIME   a census is a full classification pass over the capture; running two so they can paint over each other is waste
   *                   whose order nobody controls. An ask that arrives during one is not refused, it is DELAYED: `_pending` remembers it.
   *   WRITES ARE WATER `Invalidate()` marks every census started before an operator's write as unpaintable. `_running` alone cannot
   *                   tell "an answer" from "an old answer", and an old one is worse than none: it erases the click in front of the
   *                   person who made it. This is what `EditorHold`'s held census needs too — "keep the newest build" is right about
   *                   NEWEST and silent about WRITTEN-UNDERNEATH, and a verdict can be written from another pane's menu while this
   *                   pane's popup holds a census.
   *   CONVERGES       `Complete` answers FRESH (paint it, the world is still as described) or RERUN (something asked, or the world
   *                   moved): one hop — not a queue, not a timer — turns "dropped" into "one census later", and ten asks behind one
   *                   build coalesce into a single follow-up, which is what makes keeping the promise affordable at two passes a second.
   *
   * A dropped ask is the bug this type exists for; a delayed one is the design. Nothing here refuses anything forever.
   */
  internal sealed class CensusRepaint
  {
    /// <summary>What a finished census may do with its answer.</summary>
    internal enum Outcome
    {
      /// <summary>Paint it: nothing asked during the run and no write landed under it.</summary>
      Fresh,

      /// <summary>Do not paint this one as the last word — another census is owed (an ask piled up, or the world moved).</summary>
      Rerun,
    }

    private int _running;
    private long _writeGen;
    private int _pending;

    /// <summary>The generation to stamp a census with when it starts. Cheap; readable for logs and assertions.</summary>
    internal long Generation => Volatile.Read(ref _writeGen);

    /*
     * Is a repaint still outstanding? True from the moment an ask is delayed or a paint is refused, and cleared when a census BEGINS (the run
     * that pays it), so the flag answers the question anybody actually has — "is the list going to fix itself?" — instead of mirroring the last
     * value handed back.
     */
    internal bool OwesRerun => Volatile.Read(ref _pending) == 1;

    /// <summary>
    /// An operator wrote identity state: what is painted is stale and whatever is BUILDING describes a world that no longer exists.
    /// One increment, called on the UI thread beside the write it describes.
    /// </summary>
    internal void Invalidate() => Interlocked.Increment(ref _writeGen);

    /// <summary>Does this answer still describe the world its census was built from? Asked before painting, twice: at completion and again at release from a hold.</summary>
    internal bool IsFresh(long stamp) => stamp == Volatile.Read(ref _writeGen);

    /// <summary>
    /// Take permission to build. False means one is already running AND this ask has been remembered — so the caller may walk away
    /// without losing it, which is precisely what it used to do by walking away and losing it.
    /// </summary>
    internal bool TryBegin(out long stamp)
    {
      stamp = Volatile.Read(ref _writeGen);
      if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
      {
        // Beginning the census IS the payment: the debt is cleared here, not when the previous one finished, so `OwesRerun` reads
        // "a repaint is still owed" all the way from the dropped ask to the run that answers it.
        Volatile.Write(ref _pending, 0);
        return true;
      }

      Volatile.Write(ref _pending, 1);
      return false;
    }

    /// <summary>
    /// This build is over. RERUN means another census must run (the answer may still be painted as an interim view — it is only refused
    /// as the LAST word); FRESH means this is the answer until something invalidates it. The debt is recorded here and cleared by the run
    /// that pays it, so a reader can always tell whether a repaint is outstanding.
    /// </summary>
    internal Outcome Complete(long stamp)
    {
      Volatile.Write(ref _running, 0);

      if (IsFresh(stamp)) return Volatile.Read(ref _pending) == 1 ? Outcome.Rerun : Outcome.Fresh;

      // Written underneath: remember that, or the refusal would be a secret kept only by the local variable.
      Volatile.Write(ref _pending, 1);
      return Outcome.Rerun;
    }

    /// <summary>
    /// A census completed FRESH, was held for an open editor, and is now refused at release because a write landed while it waited.
    /// Refusing is not silence: the repaint is owed again or the pane converges on the very stale answer it just declined.
    /// </summary>
    internal void OwePaint() => Volatile.Write(ref _pending, 1);

    /// <summary>
    /// Forget this gate's debt and run flag; callers must independently refuse any abandoned callback before completing it.
    /// </summary>
    internal void Abandon()
    {
      Volatile.Write(ref _pending, 0);
      Volatile.Write(ref _running, 0);
    }
  }
}
