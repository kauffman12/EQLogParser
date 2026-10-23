namespace EQLogParser;

public partial class DamageMeterCalculationTest
{
  [TestMethod]
  [DataRow(false, 0)]
  [DataRow(true, 0)]
  [DataRow(false, 10)]
  [DataRow(true, 10)]
  [DataRow(false, 100)]
  [DataRow(true, 100)]
  public void LoadingTwoDaysOfStaleHistoryCannotOpenTheLiveMeter(bool incoming, int mode)
  {
    var log = new Capture();
    var panels = incoming ? MeterPanels.Tanking : MeterPanels.Damage;
    foreach (var second in new[] { -172800, -172799, -50433, -50400 })
      Add(log, incoming, second, 0, 0);

    log.Check(0, mode, compareFresh: true, panels: panels);
    Assert.IsNull(log.Last);
    Assert.AreEqual(4, log.Facts.FactCount, "source freshness must not discard captured damage");
    Assert.AreEqual(MeterArrival.None, log.Arrivals.Observed(panels, mode));
    Assert.IsFalse(DamageOverlayLifecycle.HasRecentActivity(log.Arrivals.Observed(panels, mode), T0, mode));

    // Catching up to current combat needs no reopen, and cannot credit the stale prefix as its receipt.
    Add(log, incoming, 1, 1, 1);
    log.Check(1, mode, compareFresh: true, panels: panels);
    Assert.IsNotNull(log.Last);
    var stats = incoming ? log.Last.TankStats : log.Last.DamageStats;
    Assert.IsNotNull(stats);
    Assert.AreEqual(600L, stats.RaidStats.Total);
    Assert.AreEqual(5, log.Arrivals.Observed(panels, mode).Through);
  }

  [TestMethod]
  [DataRow(false, 0)]
  [DataRow(true, 0)]
  [DataRow(false, 10)]
  [DataRow(true, 10)]
  public void HistoricalDamageCannotRenewAnExistingLiveReceipt(bool incoming, int mode)
  {
    var log = new Capture();
    var panels = incoming ? MeterPanels.Tanking : MeterPanels.Damage;
    Add(log, incoming, 0, 0, 0);
    log.Check(0, mode, panels: panels);
    var original = log.Arrivals.Observed(panels, mode);
    Assert.IsNotNull(log.Last);

    log.Add(incoming ? "a bat" : "Illuminai", incoming ? "Illuminai" : "a bat", -50400, 600,
      receivedAt: 9, wallReceivedAt: 9);
    log.Check(9, mode, panels: panels);
    Assert.AreEqual(original, log.Arrivals.Observed(panels, mode));
    log.Check(mode == 0 ? 30.1 : 10.1, mode, panels: panels);
    Assert.IsNull(log.Last, "historical ingest must not prolong a live board's deadline");
  }

  [TestMethod]
  [DataRow(0, 30, true)]
  [DataRow(0, 30.01, false)]
  [DataRow(3, 3, true)]
  [DataRow(3, 3.01, false)]
  [DataRow(10, 8, true)]
  [DataRow(10, 10, true)]
  [DataRow(10, 10.01, false)]
  [DataRow(100, 100, true)]
  [DataRow(100, 100.01, false)]
  [DataRow(10, -1, false)]
  public void SourceAgeMustQualifyBeforeReceiptCanStartTheFullTimeout(int mode, double age, bool eligible)
  {
    var log = new Capture();
    Add(log, false, 0, 100, age);
    var arrival = log.Arrivals.Latest(log.Facts, log.Timeline, mode);
    Assert.AreEqual(eligible, DamageOverlayLifecycle.HasRecentActivity(arrival, T0 + 100, mode));
    Assert.AreEqual(arrival, log.Arrivals.Observed(MeterPanels.Both, mode));
    if (!eligible) return;

    log.Check(100, mode, compareFresh: true);
    var original = log.Last;
    Assert.IsNotNull(original);
    var timeout = mode == 0 ? 30 : mode;
    log.Check(100 + timeout - 0.01, mode, compareFresh: true);
    DamageMeterAssert.Equal(original, log.Last);
    log.Check(100 + timeout + 0.01, mode);
    Assert.IsNull(log.Last);
  }

  [TestMethod]
  public void ModeChangesUseTheirOwnSourceQualificationWithoutRestampingOrRescanning()
  {
    var log = new Capture();
    Add(log, false, 0, 100, 40);
    Assert.AreEqual(MeterArrival.None, log.Arrivals.Latest(log.Facts, log.Timeline));
    Assert.AreEqual(MeterArrival.None, log.Arrivals.Observed(MeterPanels.Both, 10));
    var longMode = log.Arrivals.Observed(MeterPanels.Both, 60);
    Assert.AreEqual(new MeterArrival(1, T0 + 100), longMode);
    Assert.AreEqual(longMode, log.Arrivals.Latest(log.Facts, log.Timeline, 60));
    Assert.IsFalse(DamageOverlayLifecycle.HasRecentActivity(longMode, T0 + 161, 60));
  }

  [TestMethod]
  public void StaleForOneModeCannotBridgeThatModesQuietGap()
  {
    var log = new Capture();
    Add(log, false, 0, 0, 0);
    Add(log, false, 0, 2, 4); // qualifies for five seconds, not three
    Add(log, false, 0, 4, 0);
    Assert.AreEqual(new MeterArrival(3, T0 + 4, 2), log.Arrivals.Latest(log.Facts, log.Timeline, 3));
    Assert.AreEqual(new MeterArrival(3, T0 + 4), log.Arrivals.Latest(log.Facts, log.Timeline, 5));
  }

  [TestMethod]
  public void MixedSourceAgesInOneMillisecondKeepTheirOwnReceiptEndpoints()
  {
    var log = new Capture();
    Add(log, false, -50400, 100, 0);
    Add(log, false, 0, 100, 0);
    Add(log, false, -50400, 100, 0);
    Add(log, false, 0, 100, 8);
    Assert.AreEqual(new MeterArrival(2, T0 + 100), log.Arrivals.Latest(log.Facts, log.Timeline, 3));
    Assert.AreEqual(new MeterArrival(4, T0 + 100), log.Arrivals.Latest(log.Facts, log.Timeline, 10));
    Assert.AreEqual(4, log.Arrivals.BeginRead(log.Timeline).Through);
  }

  [TestMethod]
  public void WorkerDelayAndReclassificationCannotTurnStaleOrFutureInputIntoLiveActivity()
  {
    var log = new Capture();
    Add(log, false, -50400, 100, 0);
    Add(log, false, 1, 100, 0);
    var read = log.Arrivals.BeginRead(log.Timeline);
    Assert.AreEqual(MeterArrival.None, read.Complete(log.Facts, 10));
    log.Timeline.SetIdentity("Unrelated", IdentityKind.Player, RuleStrength.Manual, "manual");
    log.Check(101, 10);
    Assert.IsNull(log.Last);
    Assert.AreEqual(MeterArrival.None, log.Arrivals.Observed(MeterPanels.Both, 10));
  }

  [TestMethod]
  public void QualifiedSourceAgeSurvivesReceiptHistoryRetirementAndVerdictRechecks()
  {
    var log = new Capture();
    for (var second = 0; second <= 300; second += 2)
      Add(log, false, second, second, second + 4);
    var expected = new MeterArrival(151, T0 + 300);
    Assert.AreEqual(expected, log.Arrivals.Latest(log.Facts, log.Timeline, 5));
    Assert.AreEqual(MeterArrival.None, log.Arrivals.Latest(log.Facts, log.Timeline, 3));
    log.Timeline.SetIdentity("Unrelated", IdentityKind.Player, RuleStrength.Manual, "manual");
    Assert.AreEqual(expected, log.Arrivals.Latest(log.Facts, log.Timeline, 5));
    Assert.AreEqual(MeterArrival.None, log.Arrivals.Observed(MeterPanels.Both, 3));
  }

  private static void Add(Capture log, bool incoming, int sourceSecond, double receivedAt, double wallReceivedAt) =>
    log.Add(incoming ? "a rat" : "Illuminai", incoming ? "Illuminai" : "a rat", sourceSecond, 600,
      receivedAt: receivedAt, wallReceivedAt: wallReceivedAt);
}
