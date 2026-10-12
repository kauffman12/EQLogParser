using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser.Tests.derive;

/*
 * The fight list's owed scroll (EQLogParser.Core/src/ui/FightListReveal.cs): every capture owes exactly one jump to its newest row, paid when the
 * file finishes READING — the moment an operator watches — and not on a timer, not on growth. The order of the two halves is not fixed: a short log
 * reads out before any derive runs (rows after the read), a big one reaches 100 % while its first capture is still being projected (rows later).
 * Both orders are asserted here because each is a real capture shape, and a rule that only works in one of them either jumps mid-file or never jumps
 * at all. Nothing else conditions the viewport: no pointer test, no visibility test (the pane's only extra step is that a single row has no fold to
 * cross, and it spends the debt even then — see RevealNewestRowIfOwed).
 */
[TestClass]
public class FightListRevealTest
{
  [TestMethod]
  [Description("Read finishes first (the short-log shape): the answer that has rows in it pays the reveal")]
  public void ReadFinishingThenRowsPays()
  {
    var reveal = new FightListReveal();

    reveal.ReadFinished();
    Assert.IsTrue(reveal.TryPay(hasRows: true));
  }

  [TestMethod]
  [Description("Rows land first (the big-capture shape): nothing scrolls mid-file, and the read still gets its reveal")]
  public void RowsArrivingMidReadDoNotPay()
  {
    var reveal = new FightListReveal();

    Assert.IsFalse(reveal.TryPay(hasRows: true), "a jump at a third of the way through the file shows a list that is not finished — it is not the reveal");

    reveal.ReadFinished();
    Assert.IsTrue(reveal.TryPay(hasRows: true), "the debt survived the mid-file pass, which is what makes the jump land at the end");
  }

  [TestMethod]
  [Description("An empty answer keeps the debt — a big capture finishes reading before its rows exist")]
  public void EmptyAnswerKeepsTheDebt()
  {
    var reveal = new FightListReveal();

    reveal.ReadFinished();
    Assert.IsFalse(reveal.TryPay(hasRows: false), "an empty grid is nothing to reveal; the loading band was the message");
    Assert.IsTrue(reveal.TryPay(hasRows: true), "the first real answer gets the jump the operator is owed");
  }

  [TestMethod]
  [Description("One payment per capture: live growth after the reveal gets no second scroll, however many fights arrive")]
  public void GrowthAfterTheRevealGetsNothing()
  {
    var reveal = new FightListReveal();

    reveal.ReadFinished();
    Assert.IsTrue(reveal.TryPay(hasRows: true));
    Assert.IsFalse(reveal.TryPay(hasRows: true), "this is the live-raid case: a new fight at the bottom is not permission to move the viewport");
    Assert.IsFalse(reveal.TryPay(hasRows: false));
  }

  [TestMethod]
  [Description("Arming is what a new capture does (reopen, Clear All): the read must finish again before the new jump")]
  public void ANewCaptureOwesAgain()
  {
    var reveal = new FightListReveal();

    reveal.ReadFinished();
    Assert.IsTrue(reveal.TryPay(hasRows: true));

    reveal.Arm();
    Assert.IsFalse(reveal.TryPay(hasRows: true), "the rows still on screen belong to the read that just finished, not to this one");
    reveal.ReadFinished();
    Assert.IsTrue(reveal.TryPay(hasRows: true));
  }

  [TestMethod]
  [Description("A refusal is still a payment: once there is an answer, the debt does not survive into somebody's pull")]
  public void APaidDebtStaysPaid()
  {
    var reveal = new FightListReveal();

    reveal.ReadFinished();
    Assert.IsTrue(reveal.TryPay(hasRows: true));

    // The pane pays first and then decides whether there is anything to scroll (one row is no fold), so a capture whose reveal has nothing to move
    // still cannot spend it on some later pass.
    Assert.IsFalse(reveal.TryPay(hasRows: true));
  }
}
