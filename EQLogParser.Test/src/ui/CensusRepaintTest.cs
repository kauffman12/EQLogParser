using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser.Tests.Ui
{
  /*
   * The identity pane's repaint policy, in the one place it is decided (EQLogParser.Core/src/ui/CensusRepaint.cs).
   *
   * The field report this was written for: from the summary panes' right-click menu a verdict works every time — the cell changes,
   * the summary refreshes, the fight list follows. From the Type dropdown in the Identity Table the SAME write "did nothing": the
   * row kept the kind it had; a second click moved the cell and the summary still did not follow; the calculator fixed everything.
   * The write was never the problem (both doors call `ClassificationCommands.ApplyVerdict`, and identity-overrides.txt says so).
   * What differs is the pane's own repaint: the menu has nothing to repaint, and this pane's census — a full classification pass over
   * the capture — answered "one is already running" by RETURNING, which drops the ask. On a finished log nothing asks again, so the
   * store holds the operator's verdict while the grid shows what used to be true. Repair: click twice. That is a defect, not a workflow.
   *
   * So three laws, each its own test below:
   *   AN ASK IS NEVER LOST     one census at a time (two would paint over each other), but an ask arriving during one is DELAYED into
   *                            exactly one follow-up. Coalescing is the whole trick: it keeps the promise AND bounds the cost.
   *   A STALE ANSWER IS NOT AN ANSWER  a census that started before an operator's write describes a world that no longer exists, so it
   *                            may not be the last word. `EditorHold` needs this too: "keep the newest build" is right about NEWEST
   *                            and silent about WRITTEN-UNDERNEATH, which is how a census released by a closing popup erases the very
   *                            verdict the popup was opened to write.
   *   IT CONVERGES             after the dust, exactly one census carries a generation nobody has invalidated — the pane cannot get
   *                            stuck on an old answer with the road clear.
   *
   * No WPF here on purpose: a policy like this can be RUN headless, and the difference between running it and reasoning about it is
   * the difference between "I believe the dropdown works now" and a name that fails by itself if an ask ever gets dropped again.
   */
  [TestClass]
  public class CensusRepaintTest
  {
    // ---------------------------------------------------------------- an ask is never lost, and N asks are one run

    [TestMethod]
    public void AnAskDuringARunIsDelayedRatherThanDropped()
    {
      var repaint = new CensusRepaint();

      Assert.IsTrue(repaint.TryBegin(out var first), "the first ask may build");
      Assert.IsFalse(repaint.TryBegin(out _), "one census at a time; the second is remembered instead of running");
      Assert.IsTrue(repaint.OwesRerun, "and remembering it IS the fix — dropping it was the bug");

      Assert.AreEqual(CensusRepaint.Outcome.Rerun, repaint.Complete(first),
        "the run finished, but something asked during it: it goes again");
      Assert.IsTrue(repaint.OwesRerun, "the debt outlives the run that dropped it — beginning the next census is what pays it");

      Assert.IsTrue(repaint.TryBegin(out var second));
      Assert.IsFalse(repaint.OwesRerun, "and it is paid by the run that starts, not by an outcome being handed back");
      Assert.AreEqual(CensusRepaint.Outcome.Fresh, repaint.Complete(second),
        "nothing asked meanwhile: this answer stands");
    }

    [TestMethod]
    public void EveryAskBehindARunCoalescesIntoOneRatherThanAThrash()
    {
      var repaint = new CensusRepaint();
      Assert.IsTrue(repaint.TryBegin(out var first));

      // A live raid hands out derive passes ~2/s and each one asks for a census of the whole capture. Ten arriving while one is
      // already building must buy ONE follow-up — that is what makes keeping the promise affordable rather than merely correct.
      for (var n = 0; n < 10; n++) Assert.IsFalse(repaint.TryBegin(out _));

      Assert.AreEqual(CensusRepaint.Outcome.Rerun, repaint.Complete(first));
      Assert.IsTrue(repaint.TryBegin(out var second));
      Assert.AreEqual(CensusRepaint.Outcome.Fresh, repaint.Complete(second));
      Assert.IsFalse(repaint.OwesRerun, "ten asks, one follow-up, then quiet");
    }

    // --------------------------------------------------------------- a stale answer is not an answer

    [TestMethod]
    public void ACensusBuiltBeforeAWriteOwesOneAfterIt()
    {
      var repaint = new CensusRepaint();
      Assert.IsTrue(repaint.TryBegin(out var before));

      // The operator picks "Pet" out of the popup while that census is walking the capture. Its rows say Player, because at the second
      // it read the store that WAS true — painting it would erase the click in front of the person who made it.
      repaint.Invalidate();

      Assert.IsFalse(repaint.IsFresh(before), "the builder asks this before it paints");
      Assert.AreEqual(CensusRepaint.Outcome.Rerun, repaint.Complete(before),
        "a stale answer is not an answer: it owes a census taken after the write");
      Assert.IsTrue(repaint.OwesRerun, "a refusal that leaves nothing recorded is how a pane ends up quiet on a stale answer");
    }

    [TestMethod]
    public void ACensusHeldByAnOpenEditorIsCheckedAgainWhenItIsReleased()
    {
      var repaint = new CensusRepaint();
      Assert.IsTrue(repaint.TryBegin(out var held));

      // The census COMPLETED while a cell editor was open, so EditorHold is holding it for the popup's close — the exact path that
      // paints a pre-write answer over an operator's verdict a moment after they wrote it. The hold therefore asks IsFresh again at
      // release time, not only when the census arrived.
      repaint.Invalidate();
      Assert.IsFalse(repaint.IsFresh(held), "the released hold must be refused");

      // Refusing it is not silence either: the refusal leaves a rerun owed, so the pane still converges.
      Assert.AreEqual(CensusRepaint.Outcome.Rerun, repaint.Complete(held));

      // A census begun AFTER the write is paintable — the guard is staleness, not a ban on painting.
      Assert.IsTrue(repaint.TryBegin(out var after));
      Assert.IsTrue(repaint.IsFresh(after));
      Assert.AreEqual(CensusRepaint.Outcome.Fresh, repaint.Complete(after));
    }

    // -------------------------------------------------------------------------- it converges

    [TestMethod]
    public void AfterTheDustOneCensusCarriesWhatTheOperatorWrote()
    {
      var repaint = new CensusRepaint();
      Assert.IsTrue(repaint.TryBegin(out var stale));
      repaint.Invalidate();               // verdict #1 lands mid-census
      repaint.Invalidate();               // and #2 before that census even finishes

      var runs = 0;
      var stamp = stale;
      while (runs < 10 && repaint.Complete(stamp) == CensusRepaint.Outcome.Rerun)
      {
        Assert.IsTrue(repaint.TryBegin(out stamp));
        runs++;
      }

      Assert.AreEqual(1, runs, "two writes and one dropped ask still buy ONE further census, not a rebuild loop");
      Assert.IsTrue(repaint.IsFresh(stamp), "the last run carries both writes — grid and store agree");
      Assert.IsFalse(repaint.OwesRerun);
    }

    [TestMethod]
    public void ACaptureChangeOwesNothingFromTheLogThatClosed()
    {
      var repaint = new CensusRepaint();
      Assert.IsTrue(repaint.TryBegin(out _));
      repaint.Invalidate();               // an operator write, then the log closes underneath the pane

      repaint.Abandon();                  // ClearRows: no census of last night may paint tonight, and none is owed

      Assert.IsFalse(repaint.OwesRerun, "a follow-up for a closed capture would repaint a grid that belongs to another file");
      Assert.IsTrue(repaint.TryBegin(out var fresh), "the new capture's first census is not blocked by the old one's run flag");
      Assert.AreEqual(CensusRepaint.Outcome.Fresh, repaint.Complete(fresh));
    }
  }
}
