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
  [DataRow(false)]
  [DataRow(true)]
  public void PrunedReceiptsStillRetireTheExpiredTimedAccumulation(bool incoming)
  {
    var log = new Capture();
    log.Add(incoming ? "a rat" : "Illuminai", incoming ? "Illuminai" : "a rat", 0, 600);
    log.Check(0, 10);
    var checkpoint = log.Lifecycle.Copy();
    log.Add("a rat", "a bat", 200, 600);
    Assert.AreEqual(MeterArrival.None, log.Arrivals.Latest(log.Facts, log.Timeline));
    log.Check(200, 10);
    Assert.IsNull(log.Last);
    Assert.AreEqual(2, log.Lifecycle.SelectCandidates(1, log.Project(), 10).FirstOrdinal,
      "a verified prefix with no retained eligible receipt cannot preserve an expired accumulation");
    Assert.IsTrue(checkpoint.TrySelect(1, log.Project(), T0 + 200, 10, MeterArrival.None, log.Facts.FactCount,
      out var copiedScope, activity: log.Calculation.Activity.For));
    Assert.AreEqual(2, copiedScope.FirstOrdinal, "the lifecycle checkpoint must carry the last verified deadline too");

    log.Add(incoming ? "a rat" : "Illuminai", incoming ? "Illuminai" : "a rat", 201, 600);
    log.Check(201, 10, compareFresh: true);
    Assert.IsNotNull(log.Last);
    var stats = incoming ? log.Last.TankStats : log.Last.DamageStats;
    Assert.IsNotNull(stats);
    Assert.AreEqual(600L, stats.RaidStats.Total, "expired damage must not return with the next receipt");
    Assert.AreEqual(1d, stats.RaidStats.TotalSeconds);
  }

  [TestMethod]
  public void AFilterHidingEveryReceiptDoesNotPretendTheTimedBoardExpired()
  {
    var previous = AppSettings.IsBaneDamageEnabled;
    try
    {
      AppSettings.IsBaneDamageEnabled = true;
      var log = new Capture();
      log.Add("Illuminai", "a rat", 0, 600, LabelTypes.Bane);
      log.Check(0, 10);
      AppSettings.IsBaneDamageEnabled = false;
      log.Check(1, 10);
      Assert.IsNull(log.Last);
      Assert.AreEqual(0, log.Lifecycle.SelectCandidates(1, log.Project(), 10).FirstOrdinal,
        "an ineligible receipt is not necessarily an expired receipt");
      AppSettings.IsBaneDamageEnabled = true;
      log.Check(2, 10, compareFresh: true);
      Assert.IsNotNull(log.Last);
      Assert.AreEqual(600L, log.Last.DamageStats.RaidStats.Total);
      AppSettings.IsBaneDamageEnabled = false;
      log.Check(11, 10);
      Assert.IsNull(log.Last);
      Assert.AreEqual(1, log.Lifecycle.SelectCandidates(1, log.Project(), 10).FirstOrdinal);
      AppSettings.IsBaneDamageEnabled = true;
      log.Check(12, 10);
      Assert.IsNull(log.Last, "reconsidering a filter cannot reopen an expired receipt");
    }
    finally
    {
      AppSettings.IsBaneDamageEnabled = previous;
    }
  }

  [TestMethod]
  public void PruningDuringAReceiptReadCannotRetireTheNewerReceipt()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 600);
    log.Check(0, 5);
    var rows = log.Project();
    var receipts = log.Arrivals.BeginRead(log.Timeline);
    log.Add("Illuminai", "a rat", 200, 300);
    var arrival = receipts.Complete(log.Facts);
    Assert.AreEqual(MeterArrival.None, arrival);
    Assert.IsTrue(log.Lifecycle.TrySelect(1, rows, T0 + 200, 5, arrival, receipts.Through,
      out var expired, activity: log.Calculation.Activity.For));
    Assert.AreEqual(0, expired.Rows.Count);
    Assert.AreEqual(1, expired.FirstOrdinal, "pruning may retire only the checked prefix");
    log.Check(200, 5, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(300L, log.Last.DamageStats.RaidStats.Total);
  }

  [TestMethod]
  [DataRow(false, 0, false)]
  [DataRow(true, 0, false)]
  [DataRow(false, 90, false)]
  [DataRow(true, 90, false)]
  [DataRow(false, 90, true)]
  [DataRow(true, 90, true)]
  public void OrdinalCutAnnotationRangesUseOnlyTheRetainedRecords(bool incoming, int mode, bool expired)
  {
    var log = new Capture();
    RecordsStore.Instance.Add(new SpecialRecord { Player = "Illuminai", Code = "A" }, T0 + 50);
    RecordsStore.Instance.Add(new DeathRecord { Killed = "Illuminai" }, T0 + 50);
    for (var second = 0; second <= 100; second += 20)
    {
      log.Add(incoming ? "a rat" : "Illuminai", incoming ? "Illuminai" : "a rat", second, 600);
      log.Check(second, mode);
    }
    if (expired)
    {
      log.Check(191, mode);
    }
    else
    {
      log.Lifecycle.Reset(1, log.Facts.FactCount);
      log.Check(100, mode);
    }
    Assert.IsNull(log.Last);
    RecordsStore.Instance.Add(new SpecialRecord { Player = "Illuminai", Code = "B" }, T0 + 101);
    RecordsStore.Instance.Add(new DeathRecord { Killed = "Illuminai" }, T0 + 102);
    foreach (var second in new[] { 101, 105 })
    {
      var receipt = expired ? second + 100 : second;
      log.Add(incoming ? "a rat" : "Illuminai", incoming ? "Illuminai" : "a rat", second, 600, receivedAt: receipt);
      log.Check(receipt, mode, compareFresh: true);
      foreach (var result in new[] { log.Last, log.Expected, log.Fresh })
      {
        Assert.IsNotNull(result);
        var stats = incoming ? result.TankStats : result.DamageStats;
        Assert.IsNotNull(stats);
        Assert.AreEqual(T0 + 101, stats.RaidStats.AllRanges.TimeSegments[0].BeginTime);
        Assert.AreEqual(T0 + second, stats.RaidStats.AllRanges.TimeSegments[^1].EndTime);
        Assert.HasCount(1, stats.RaidStats.Deaths, "an ordinal cut must exclude the death from the original life");
        Assert.AreEqual(T0 + 102, stats.RaidStats.Deaths[0].BeginTime);
        Assert.AreEqual("BX", stats.StatsList.Single().Special,
          "the annotation lead-in and death tail still apply to the retained span, not the original lifetime");
      }
    }

    var whole = DerivedTotals.ForOverlay(log.Project(), log.Cache.Index, log.Facts,
      double.NegativeInfinity, double.PositiveInfinity);
    Assert.IsNotNull(whole);
    var wholeStats = incoming ? whole.TankStats : whole.DamageStats;
    Assert.IsNotNull(wholeStats);
    Assert.HasCount(2, wholeStats.RaidStats.Deaths, "an ordinal cut must not change the ordinary whole-life query");
    Assert.AreEqual(T0, wholeStats.RaidStats.AllRanges.TimeSegments[0].BeginTime);
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
