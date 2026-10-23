namespace EQLogParser;

public partial class DamageOverlayLifecycleTest
{
  [TestMethod]
  [DataRow(false, false, 0)]
  [DataRow(false, true, 0)]
  [DataRow(true, false, 0)]
  [DataRow(true, true, 0)]
  [DataRow(false, false, 1)]
  [DataRow(false, true, 1)]
  [DataRow(true, false, 1)]
  [DataRow(true, true, 1)]
  public void APostDeathAttemptCannotRenewACompletionWithATiedOrEarlierLogSecond(bool incoming, bool delayed,
    int attemptSecond)
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 600);
    log.Die("a rat", 1);
    Damage(600, log.Board(meter, 1));
    if (delayed) Assert.IsNull(log.Board(meter, 40));

    var receipt = delayed ? 100 : 2;
    log.Avoid("a bat", attemptSecond, incoming, receivedAt: receipt);
    Assert.IsNull(log.Board(meter, receipt, project: false));
    Assert.IsNull(log.Board(meter, receipt));
    Assert.IsNull(log.Board(meter.Copy(), receipt));
    Assert.IsNull(log.Board(new DamageOverlayLifecycle(), receipt));
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void ASameSecondAttemptBeforeDeathDoesNotSuppressTheFinalResult(bool incoming)
  {
    var log = new Capture();
    log.Hit("a rat", 0, 600);
    log.Avoid("a bat", 1, incoming);
    log.Die("a rat", 1);
    Damage(600, log.Board(new DamageOverlayLifecycle(), 2));
  }

  [TestMethod]
  public void ASameSecondKillingBlowAfterTheSlainLineStillBelongsToTheCompletion()
  {
    var log = new Capture();
    log.Hit("a rat", 0, 600);
    log.Die("a rat", 1);
    log.Hit("a rat", 1, 100);
    Damage(700, log.Board(new DamageOverlayLifecycle(), 2));
  }

  [TestMethod]
  public void AnAttemptBetweenSimultaneousDeathsDoesNotDeleteTheirFinalResult()
  {
    var log = new Capture();
    log.Hit("a rat", 0, 600);
    log.Hit("a bat", 0, 200);
    log.Die("a rat", 1);
    log.Avoid("a snake", 1, false);
    log.Die("a bat", 1);
    Damage(800, log.Board(new DamageOverlayLifecycle(), 2));
  }

  [TestMethod]
  public void ANewPositivePullInTheDeathSecondDoesNotFlashTheCompletedAmountsWhilePending()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 600);
    log.Die("a rat", 1);
    Damage(600, log.Board(meter, 1));
    log.Hit("a bat", 1, 200);
    Assert.IsNull(log.Board(meter, 1, project: false));
    Damage(200, log.Board(meter, 1));
  }

  [TestMethod]
  public void ExcludedPostDeathOutputCannotSuppressOrRenewTheCompletedResult()
  {
    var saved = AppSettings.IsBaneDamageEnabled;
    try
    {
      AppSettings.IsBaneDamageEnabled = false;
      var log = new Capture();
      var meter = new DamageOverlayLifecycle();
      log.Hit("a rat", 0, 600);
      log.Die("a rat", 1);
      Damage(600, log.Board(meter, 1));
      log.Hit("a bat", 1, 100, LabelTypes.Bane);
      Damage(600, log.Board(meter, 2, project: false));
      Damage(600, log.Board(meter, 2));
      Assert.IsNull(log.Board(meter, 9));
    }
    finally
    {
      AppSettings.IsBaneDamageEnabled = saved;
    }
  }

  [TestMethod]
  public void AnInferredCharmCompletionDoesNotInventADeathSequence()
  {
    var log = new Capture();
    log.Hit("a rat", 0, 600);
    log.Avoid("a bat", 0, false);
    log.Charm("a rat", 1);
    Damage(600, log.Board(new DamageOverlayLifecycle(), 1));
    Assert.AreEqual(DerivedFightEnd.Charmed, log.Rows.Single(row => row.Name == "A rat").EndReason);
  }

  [TestMethod]
  [DataRow(false, false)]
  [DataRow(false, true)]
  [DataRow(true, false)]
  [DataRow(true, true)]
  public void UnrelatedAvoidanceCannotReopenAnExpiredOnKillFight(bool killed, bool incoming)
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    if (incoming) log.Incoming("a rat", 0, 600);
    else log.Hit("a rat", 0, 600);
    if (killed) log.Die("a rat", 1);
    Assert.IsNotNull(log.Board(meter, 1));
    Assert.IsNull(log.Board(meter, 40));

    log.Avoid("a bat", 100, incoming);
    Assert.IsTrue(log.HasActivity(100, 0), "the new attempt earns a receipt, not the previous fight's amounts");
    Assert.IsNull(log.Board(meter, 100, project: false), "pending activity must not revive the rat");
    Assert.IsNull(log.Board(meter, 100), "projecting the same attempt must not bring the rat back");
    Assert.IsNull(log.Board(meter.Copy(), 100), "reopening must preserve the empty selection");
    Assert.IsNull(log.Board(new DamageOverlayLifecycle(), 100), "a cold opening must make the same selection");

    if (incoming) log.Incoming("a bat", 101, 200);
    else log.Hit("a bat", 101, 200);
    var board = log.Board(meter, 101);
    Assert.IsNotNull(board);
    var stats = incoming ? board.TankStats : board.DamageStats;
    Assert.IsNotNull(stats);
    Assert.AreEqual("A bat", stats.TargetTitle);
    Assert.AreEqual(200L, stats.RaidStats.Total, "the next positive hit opens only the new encounter");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void NewAvoidanceEndsTheCompletedDisplayBeforeItsHoldExpires(bool incoming)
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 600);
    log.Die("a rat", 1);
    Damage(600, log.Board(meter, 1));

    log.Avoid("a bat", 2, incoming);
    Assert.IsNull(log.Board(meter, 2, project: false));
    Assert.IsNull(log.Board(meter, 2), "the pending-to-projected handoff cannot restore a completed pull");
    Assert.IsNull(log.Board(meter.Copy(), 3));
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void AvoidanceOnTheSameLifeStillContinuesItsDamage(bool incoming)
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 600);
    Damage(600, log.Board(meter, 0));
    log.Avoid("a rat", 29, incoming);
    Damage(600, log.Board(meter, 29, project: false));
    Damage(600, log.Board(meter, 29));
    Damage(600, log.Board(meter, 59));
    Assert.IsNull(log.Board(meter, 59.1));
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void UnrelatedAvoidanceRespectsTheOpenLifesThirtySecondBoundary(bool incoming)
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 600);
    Damage(600, log.Board(meter, 0));
    log.Avoid("a bat", 30, incoming);
    Damage(600, log.Board(meter, 30, project: false));
    Damage(600, log.Board(meter, 30));
    log.Avoid("a bat", 31, incoming);
    Assert.IsNull(log.Board(meter, 31, project: false));
    Assert.IsNull(log.Board(meter, 31), "zero-amount activity must move the projected edge just as it moved the pending edge");
  }
}
