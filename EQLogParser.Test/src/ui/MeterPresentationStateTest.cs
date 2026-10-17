namespace EQLogParser;

[TestClass]
public sealed class MeterPresentationStateTest
{
  [TestMethod]
  public void TimerCanExpireAnAlreadyDisplayedBoardWithoutCompletingItsWorker()
  {
    var gate = new MeterRefreshGate();
    var presentation = new MeterPresentationState();
    presentation.Publish(1, 0, 10);
    Assert.IsTrue(gate.Request(1));
    Assert.IsFalse(gate.Request(11), "the counting worker is still occupied");
    Assert.IsFalse(presentation.IsFresh(1, 0, 11), "expiry is independent of worker completion");
  }

  [TestMethod]
  public void ALateWorkerCannotRepaintAnExpiredOrSupersededResult()
  {
    var presentation = new MeterPresentationState();
    presentation.Publish(1, 0, 10);
    Assert.IsTrue(presentation.IsFresh(1, 0, 10));
    Assert.IsFalse(presentation.IsFresh(1, 0, 10.1));
    Assert.IsFalse(presentation.IsFresh(2, 0, 9));
    Assert.IsFalse(presentation.IsFresh(1, 1, 9));
    presentation.Publish(1, 1, 25);
    Assert.IsTrue(presentation.IsFresh(1, 1, 20));
  }

  [TestMethod]
  public void FailedPaintingHidesThePartialPresentationAndCanRecover()
  {
    var presentation = new MeterPresentationState();
    presentation.Publish(1, 0, 10);
    var toolbar = true;
    var body = true;
    Assert.ThrowsExactly<InvalidOperationException>(() => presentation.Paint(() =>
    {
      body = false;
      throw new InvalidOperationException("paint failed after clearing rows");
    }, () =>
    {
      toolbar = false;
      body = false;
    }));
    Assert.IsFalse(toolbar);
    Assert.IsFalse(body);
    Assert.IsFalse(presentation.IsFresh(1, 0, 1));
    presentation.Publish(1, 0, 20);
    presentation.Paint(() =>
    {
      toolbar = true;
      body = true;
    }, () =>
    {
      toolbar = false;
      body = false;
    });
    Assert.IsTrue(toolbar && body);
    Assert.IsTrue(presentation.IsFresh(1, 0, 11));
  }

  [TestMethod]
  public void EmptyActivityAndExplicitInvalidationNeverKeepAViewAlive()
  {
    var presentation = new MeterPresentationState();
    presentation.Publish(1, 0, double.NegativeInfinity);
    Assert.IsFalse(presentation.IsFresh(1, 0, 1));
    presentation.Publish(1, 0, 10);
    presentation.Clear();
    Assert.IsFalse(presentation.IsFresh(1, 0, 1));
  }

  [TestMethod]
  public void ZeroDamagePercentagesCannotPassNaNToTheProgressControl()
  {
    Assert.AreEqual(0d, MeterDisplaySelection.Percentage(0, 0));
    Assert.AreEqual(0d, MeterDisplaySelection.Percentage(100, 0));
    Assert.AreEqual(25d, MeterDisplaySelection.Percentage(100, 400));
  }
}
