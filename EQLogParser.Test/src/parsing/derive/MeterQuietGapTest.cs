namespace EQLogParser;

public partial class DamageMeterCalculationTest
{
  [TestMethod]
  [DataRow(false, false)]
  [DataRow(true, false)]
  [DataRow(false, true)]
  [DataRow(true, true)]
  public void AnUnobservedQuietGapKeepsOnlyTheEntireNewRun(bool incoming, bool firstOpening)
  {
    var log = new Capture();
    var attacker = incoming ? "a rat" : "Illuminai";
    var defender = incoming ? "Illuminai" : "a rat";
    log.Add(attacker, defender, 0, 600, receivedAt: 0);
    if (!firstOpening) log.Check(0, 3);
    // The new run lasts longer than the mode, but no tick saw its beginning or the preceding gap.
    // Equal log timestamps prove that the cut is an ordinal, not a log-time slice.
    foreach (var receipt in new[] { 10, 12, 14 })
      log.Add(attacker, defender, 0, 200, receivedAt: receipt);
    log.Check(14, 3, compareFresh: true);
    Assert.IsNotNull(log.Last);
    var stats = incoming ? log.Last.TankStats : log.Last.DamageStats;
    Assert.IsNotNull(stats);
    Assert.AreEqual(600L, stats.RaidStats.Total);
    Assert.AreEqual(1, log.Lifecycle.SelectCandidates(1, log.Project(), 3).FirstOrdinal);
    log.Check(16.9, 3, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(600L, (incoming ? log.Last.TankStats : log.Last.DamageStats).RaidStats.Total);
  }

  [TestMethod]
  public void ANewRunLongerThanTheModeIsNotReslicedFromItsLatestLogTime()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 600);
    log.Check(0, 3);
    foreach (var second in new[] { 10, 12, 14 }) log.Add("Illuminai", "a rat", second, 200);
    log.Check(14, 3, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(600L, log.Last.DamageStats.RaidStats.Total);
    Assert.AreEqual(5d, log.Last.DamageStats.RaidStats.TotalSeconds);
  }

  [TestMethod]
  [DataRow(3)]
  [DataRow(100)]
  public void AStallLongerThanReceiptRetentionKeepsContinuousCombatButNotAPriorRun(int mode)
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 600, receivedAt: 0);
    log.Check(0, mode);
    var first = mode + 1;
    for (var receipt = first; receipt <= first + 260; receipt += 2)
      log.Add("Illuminai", "a rat", 0, 100, receivedAt: receipt);
    log.Check(first + 260, mode, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(13100L, log.Last.DamageStats.RaidStats.Total);
    Assert.AreEqual(1, log.Lifecycle.SelectCandidates(1, log.Project(), mode).FirstOrdinal);

    // Compaction and a verdict-stamp change must not erase the old gap boundary or invent a new one.
    log.Timeline.SetIdentity("Unrelated", IdentityKind.Player, RuleStrength.Manual, "manual");
    log.Add("Illuminai", "a rat", 0, 100, receivedAt: first + 262);
    log.Check(first + 262, mode, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(13200L, log.Last.DamageStats.RaidStats.Total);
  }

  [TestMethod]
  public void ALongUnexaminedContinuousRunDoesNotBecomeAFalseReset()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 600, receivedAt: 0);
    log.Check(0, 3);
    for (var receipt = 2; receipt <= 260; receipt += 2)
      log.Add("Illuminai", "a rat", 0, 100, receivedAt: receipt);
    log.Check(260, 3, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(13600L, log.Last.DamageStats.RaidStats.Total);
    Assert.AreEqual(0, log.Lifecycle.SelectCandidates(1, log.Project(), 3).FirstOrdinal);
  }

  [TestMethod]
  public void ExactlyTheConfiguredQuietPeriodDoesNotSplitTheAccumulation()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 600);
    log.Check(0, 3);
    log.Add("Illuminai", "a rat", 3, 200);
    log.Check(3, 3, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(800L, log.Last.DamageStats.RaidStats.Total);
    log.Add("Illuminai", "a rat", 7, 100);
    log.Check(7, 3, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(100L, log.Last.DamageStats.RaidStats.Total);
  }

  [TestMethod]
  public void NpcNoiseCannotBridgeAQuietGapAndPendingDamageSurvivesItsCut()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 600);
    log.Check(0, 3);
    var rows = log.Project();
    foreach (var second in new[] { 2, 4, 6 }) log.Add("a rat", "a bat", second, 100);
    log.Add("Illuminai", "a rat", 8, 200);
    var read = log.Arrivals.BeginRead(log.Timeline);
    var arrival = read.Complete(log.Facts, mode: 3);
    Assert.IsFalse(log.Lifecycle.TrySelect(1, rows, T0 + 8, 3, arrival, read.Through,
      out _, activity: log.Calculation.Activity.For));
    Assert.AreEqual(4, log.Lifecycle.SelectCandidates(1, rows, 3).FirstOrdinal,
      "the unprojected new receipt is kept, not retired with the checked prefix");
    log.Check(8, 3, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(200L, log.Last.DamageStats.RaidStats.Total);
  }
}
