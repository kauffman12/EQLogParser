using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * The calculator's label timing (2026-11; the row word it shows is pinned in EQLogParser.Wpf.Test, where NamesTable is reachable):
 * the click's honest outcome is sometimes an identical cell, so
 * "tried" has to be sayable. The law (RecalcOverlay) in three terms — a floor, then the pass that answers the click, capped for the
 * bulk load that parks both derive lanes. What must NOT come back: a wall-clock guess in place of the pass (a label that hides before
 * the answer has painted reads as "the click did nothing", the very report that bought this), and an immediate-hide under the floor
 * (the inverse of DeferredBusyState's law, pinned here so both halves of that sentence stay true).
 */
[TestClass]
public sealed class RecalcOverlayTest
{
  private const long Start = 100_000;

  /*
   * The floor is absolute: under it the label stays up even though the pass landed (a fast build must not erase the trace of the
   * click) and even though nothing has landed at all. This is where "if it ends up with the same result, at least the user knows it
   * tried" comes from — the showing was certain; only the hiding waits.
   */
  [TestMethod]
  public void TheFloorHoldsTheLabelUpWhetherOrNotThePassLanded()
  {
    for (var elapsed = 0L; elapsed < RecalcOverlay.MinVisibleMs; elapsed += 123)
    {
      Assert.IsFalse(RecalcOverlay.ShouldHide(Start, Start + elapsed, passLanded: true),
                     $"at {elapsed} ms the pass already ran, yet the floor still holds: the click must be seen trying");
      Assert.IsFalse(RecalcOverlay.ShouldHide(Start, Start + elapsed, passLanded: false),
                     $"at {elapsed} ms nothing has landed and the label may not hide on a timer alone before the floor");
    }
  }

  /*
   * Past the floor: the pass landing is what takes the label down — the census and the boards both read the derive's snapshot, so
   * "done" means "the pass this click asked for has run", not a guess about it. Without the pass, time alone cannot take it down until
   * the cap.
   */
  [TestMethod]
  public void PastTheFloorThePassTakesItDownAndTimeOnlyAtTheCap()
  {
    var justPast = Start + RecalcOverlay.MinVisibleMs + 1;

    Assert.IsTrue(RecalcOverlay.ShouldHide(Start, justPast, passLanded: true), "floor paid and the pass ran: the label has done its job");
    Assert.IsFalse(RecalcOverlay.ShouldHide(Start, justPast, passLanded: false),
                   "the pass has not run, so the timer alone may not hide it — hiding early is the report this feature was bought to prevent");

    for (var elapsed = RecalcOverlay.MinVisibleMs + 1; elapsed < RecalcOverlay.HardCapMs; elapsed += 377)
      Assert.IsFalse(RecalcOverlay.ShouldHide(Start, Start + elapsed, passLanded: false),
                     $"at {elapsed} ms no pass has landed and the cap has not been paid");

    Assert.IsTrue(RecalcOverlay.ShouldHide(Start, Start + RecalcOverlay.HardCapMs, passLanded: false),
                  "the cap is paid: a pass parked behind a bulk load may not hold the row hostage");
  }

  /*
   * The two clocks must not drift into each other's units: the law is monotonic in elapsed (nothing unhides once hidden) and the cap
   * is measured on the SAME floor, so a pass that lands at exactly the boundary reads as landed. The test is over plain longs on
   * purpose — the pane feeds Environment.TickCount64 into these, and this file is where a unit slip would fail first.
   */
  [TestMethod]
  public void HidingIsMonotonicInElapsed()
  {
    foreach (var passLanded in new bool[] { false, true })
    {
      var hidden = false;
      for (var elapsed = 0L; elapsed <= RecalcOverlay.HardCapMs + 500; elapsed += 1)
      {
        if (passLanded && elapsed < RecalcOverlay.MinVisibleMs) continue;   // the floor window is asserted on its own
        var shows = RecalcOverlay.ShouldHide(Start, Start + elapsed, passLanded);
        Assert.IsFalse(hidden && !shows, $"the label re-appeared at {elapsed} ms: once hidden it stays hidden");
        hidden |= shows;
      }
    }
  }


}
