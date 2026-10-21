namespace EQLogParser;

public partial class DamageMeterCalculationTest
{
  [TestMethod]
  public void CapturedFutureTimestampsDoNotWaitForOrAgeWithThePresentationClock()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 600, receivedAt: 0);
    log.Add("Illuminai", "a rat", 10, 600, receivedAt: 0);
    log.Check(0, compareFresh: true);
    var original = log.Last;
    Assert.IsNotNull(original);
    Assert.AreEqual(1200L, original.DamageStats.RaidStats.Total);
    Assert.AreEqual(100L, DamageRdps(log, "Illuminai"));
    for (var second = 1; second < 30; second++)
    {
      log.Check(second, compareFresh: true);
      DamageMeterAssert.Equal(original, log.Last);
      Assert.AreEqual(100L, DamageRdps(log, "Illuminai"));
      AssertRatesAgreeWithAColdBuild(log);
    }
    log.Check(30.1);
    Assert.IsNull(log.Last);
  }

  [TestMethod]
  public void TimedOpeningAtDifferentInstantsCountsTheSameCapturedDamage()
  {
    var early = MakeCapture();
    var late = MakeCapture();
    early.Check(4, 5);
    late.Check(8, 5);
    Assert.IsNotNull(early.Last);
    Assert.AreEqual(1000L, early.Last.DamageStats.RaidStats.Total);
    DamageMeterAssert.Equal(early.Last, late.Last);
    Assert.IsNotNull(late.Last);
    Assert.AreEqual(early.Last.DamageRdps.Total, late.Last.DamageRdps.Total);

    static Capture MakeCapture()
    {
      var log = new Capture();
      log.Add("Illuminai", "a rat", 0, 500);
      log.Add("Illuminai", "a rat", 4, 500);
      return log;
    }
  }

  [TestMethod]
  public void DelayedDamageGetsTheFullTimeoutFromReceiptNotFromItsLogTimestamp()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 600, receivedAt: 100);
    log.Check(100, 10);
    var original = log.Last;
    Assert.IsNotNull(original);
    Assert.AreEqual(100L, DamageRdps(log, "Illuminai"));
    log.Check(109.9, 10, compareFresh: true);
    DamageMeterAssert.Equal(original, log.Last);
    AssertRatesAgreeWithAColdBuild(log);
    log.Check(110.1, 10);
    Assert.IsNull(log.Last);

    // A new ordinal with an old/same-second stamp is still newly received damage, not a replay of the old board.
    log.Add("Illuminai", "a rat", 0, 300, receivedAt: 120);
    log.Check(120, 10, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(300L, log.Last.DamageStats.RaidStats.Total);
    Assert.AreEqual(50L, DamageRdps(log, "Illuminai"));
  }

  [TestMethod]
  [DataRow(false, 0)]
  [DataRow(true, 0)]
  [DataRow(false, 1)]
  [DataRow(true, 1)]
  [DataRow(false, 2)]
  [DataRow(true, 2)]
  public void TimedInitialBoundIsAppliedBeforeTheFirstPublication(bool incoming, int boundary)
  {
    var log = new Capture();
    if (boundary != 0)
    {
      log.Add("Illuminai", "a rat", -10, 600, receivedAt: 80);
      log.Check(80, 5);
      if (boundary == 1) log.Lifecycle.Reset(1, log.Facts.FactCount);
      else log.Check(86, 5);
    }
    log.Add(incoming ? "a rat" : "Illuminai", incoming ? "Illuminai" : "a rat", 0, 600, receivedAt: 100);
    log.Add(incoming ? "a bat" : "Bithika", incoming ? "Bithika" : "a bat", 6, 600, receivedAt: 100);
    log.Add(incoming ? "a rat" : "Illuminai", incoming ? "Illuminai" : "a rat", 10, 0,
      LabelTypes.Miss, receivedAt: 100);

    log.Check(100, 5, compareFresh: true);
    var first = log.Last;
    Assert.IsNotNull(first);
    var stats = incoming ? first.TankStats : first.DamageStats;
    Assert.IsNotNull(stats);
    Assert.AreEqual(600L, stats.RaidStats.Total, "only the damage inside the initial bound belongs to the first board");
    Assert.AreEqual(1d, stats.RaidStats.TotalSeconds);
    Assert.AreEqual(600L, stats.RaidStats.Dps);
    Assert.AreEqual("A bat", stats.TargetTitle, "the rat has no positive damage inside the initial bound");
    AssertRatesAgreeWithAColdBuild(log);
    foreach (var now in new[] { 101d, 104.9 })
    {
      log.Check(now, 5, compareFresh: true);
      DamageMeterAssert.Equal(first, log.Last);
      AssertRatesAgreeWithAColdBuild(log);
    }
  }

  [TestMethod]
  public void ExpiryCannotDiscardAReceiptThatArrivedWhileThePreviousPrefixWasBeingChecked()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 600, receivedAt: 100);
    log.Check(100, 5);
    var rows = log.Project();
    var receipts = log.Arrivals.BeginRead(log.Timeline);
    log.Add("Illuminai", "a rat", 1, 300, receivedAt: 140);
    var oldArrival = receipts.Complete(log.Facts);
    Assert.IsTrue(log.Lifecycle.TrySelect(1, rows, T0 + 140, 5, oldArrival, receipts.Through,
      out var expired, activity: log.Calculation.Activity.For));
    Assert.AreEqual(0, expired.Rows.Count);
    Assert.AreEqual(1, expired.FirstOrdinal, "only the checked receipt prefix can be retired");
    log.Check(140, 5, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(300L, log.Last.DamageStats.RaidStats.Total);
  }

  [TestMethod]
  public void ResetCutsAtTheCapturedOrdinalIncludingWithinOneLogSecond()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 500, 600, receivedAt: 10);
    log.Check(10);
    log.Lifecycle.Reset(1, log.Facts.FactCount);
    log.Check(11);
    Assert.IsNull(log.Last);
    log.Add("Illuminai", "a rat", 500, 300, receivedAt: 12);
    log.Check(12, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(300L, log.Last.DamageStats.RaidStats.Total);
    Assert.AreEqual(50L, DamageRdps(log, "Illuminai"));
    AssertRatesAgreeWithAColdBuild(log);
  }
}
