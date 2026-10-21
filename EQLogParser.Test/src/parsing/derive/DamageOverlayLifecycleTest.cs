#nullable enable annotations
namespace EQLogParser;

[TestClass]
public class DamageOverlayLifecycleTest
{
  private const double T0 = FixtureTime.Base;

  [TestMethod]
  public void KillingTheOnlyMobThenAttackingAnotherStartsFreshBeforeTheDisplayHoldEnds()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    log.Hit("a rat", 4, 500);
    Damage(1000, log.Board(meter, 4));
    log.Die("a rat", 5);
    Damage(1000, log.Board(meter, 5.5)); // completed result is readable, but not accounting for another fight
    log.Hit("a bat", 6, 200);
    var next = log.Board(meter, 6.5);
    Damage(200, next);
    Assert.AreEqual(1d, next!.DamageStats.RaidStats.TotalSeconds);
  }

  [TestMethod]
  public void ADeathRemovesOnlyItsOwnFightAndPreservesTheSurvivorsTimeAndDamage()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a bat", 0, 200);
    log.Hit("a bat", 2, 100);
    log.Hit("a rat", 4, 500);
    Damage(800, log.Board(meter, 4));
    log.Die("a rat", 5);
    var survivor = log.Board(meter, 13);
    Damage(300, survivor);
    Assert.AreEqual(3d, survivor!.DamageStats.RaidStats.TotalSeconds);
    log.Hit("a bat", 14, 50);
    Damage(350, log.Board(meter, 14));
  }

  [TestMethod]
  public void TwelveSecondLullWithoutADeathKeepsBothHitsAndTheOriginalClock()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 12));
    log.Hit("a rat", 12, 200);
    var result = log.Board(meter, 12.5);
    Damage(700, result);
    Assert.AreEqual(13d, result!.DamageStats.RaidStats.TotalSeconds);
  }

  [TestMethod]
  public void ThirtySecondBoundaryIsPerLifeEvenWhenNoOverlayTickSawTheGap()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 30));
    Assert.IsNull(log.Board(meter, 30.5));
    log.Hit("a rat", 31, 200);
    Damage(200, log.Board(meter, 31));
    Assert.AreEqual(2, log.Rows.Count);

    // Also correct on first opening: neither a remembered timestamp nor a previous tick is required.
    Damage(200, log.Board(new DamageOverlayLifecycle(), 31));
  }

  [TestMethod]
  public void SameNamedMobAfterADeathIsANewFightNotAnExtensionOfTheOldTotal()
  {
    var log = new Capture();
    log.Hit("a rat", 0, 500);
    log.Die("a rat", 0);
    log.Hit("a rat", 1, 200);
    Damage(200, log.Board(new DamageOverlayLifecycle(), 1));
    Assert.AreEqual(2, log.Rows.Count);
  }

  [TestMethod]
  public void KillAndNewOpponentInTheSameSecondDoNotShareAccounting()
  {
    var log = new Capture();
    log.Hit("a rat", 0, 500);
    log.Die("a rat", 0);
    log.Hit("a bat", 0, 200);
    Damage(200, log.Board(new DamageOverlayLifecycle(), 0.5));
  }

  [TestMethod]
  public void OneShotBeforeTheFirstDeriveOpensShowsItsTotalThenExpires()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    log.Die("a rat", 0);
    Damage(500, log.Board(meter, 1));
    Assert.IsTrue(log.HasActivity(1, 0));
    var announced = log.Arrival.Through;
    Assert.IsTrue(announced > 0);
    Damage(500, log.Board(meter, 8));
    Assert.AreEqual(announced, log.Arrival.Through, "republishing a death is not new damage");
    Assert.IsNull(log.Board(meter, 8.5));
    Assert.IsFalse(log.HasActivity(30.5, 0));
    Assert.IsNull(log.Board(meter, 10));
  }

  [TestMethod]
  public void TwoArrivalsInOneSecondHaveDifferentOpeningWatermarks()
  {
    var log = new Capture();
    log.Hit("a rat", 0, 500);
    log.Board(new DamageOverlayLifecycle(), 0);
    var first = log.Arrival.Through;
    log.Hit("a rat", 0, 200);
    log.Board(new DamageOverlayLifecycle(), 0);
    Assert.IsTrue(log.Arrival.Through > first);
  }

  [TestMethod]
  public void ARowWatermarkDoesNotConsultThePcClock()
  {
    var log = new Capture();
    log.Hit("a rat", 0, 500);
    log.Board(new DamageOverlayLifecycle(), 0);

    Assert.IsTrue(log.Arrival.Through > 0,
      "captured membership does not depend on whether the PC clock has reached a log timestamp");
  }
  [TestMethod]
  public void FreshCompletedBoardDoesNotShrinkOnTheSecondTickOrARebuiltSnapshot()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    log.Hit("a rat", 20, 500);
    log.Die("a rat", 21);
    Damage(1000, log.Board(meter, 22));
    Damage(1000, log.Board(meter, 23));
    Damage(1000, log.Board(meter, 24));
  }

  [TestMethod]
  public void FirstOpeningOnALongRunningFightKeepsItsWholeLife()
  {
    var log = new Capture();
    log.Hit("a rat", 0, 500);
    log.Hit("a rat", 25, 500);
    log.Hit("a rat", 45, 500);
    Damage(1500, log.Board(new DamageOverlayLifecycle(), 46));
  }

  [TestMethod]
  public void ADeathSelectsTheCompletedResultButDoesNotRenewTheDamageReceipt()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    log.Die("a rat", 1);
    Damage(500, log.Board(meter, 2));
    Assert.AreEqual(T0 + 1, log.Rows.Single().DeathTime);
    Damage(500, log.Board(meter, 8));
    Assert.IsNull(log.Board(meter, 8.5), "only receipt of new damage can renew the deadline");
  }

  [TestMethod]
  public void SeveralCompletedPullsInOneDeriveShowTheLastNotTheirCombinedTotal()
  {
    var log = new Capture();
    log.Hit("a rat", 0, 500);
    log.Die("a rat", 1);
    log.Hit("a bat", 2, 200);
    log.Die("a bat", 3);
    Damage(200, log.Board(new DamageOverlayLifecycle(), 4));
  }

  [TestMethod]
  public void SimultaneousFinalDeathsCanShareTheCompletedDisplay()
  {
    var log = new Capture();
    log.Hit("a rat", 0, 500);
    log.Hit("a bat", 0, 200);
    log.Die("a rat", 1);
    log.Die("a bat", 1);
    Damage(700, log.Board(new DamageOverlayLifecycle(), 2));
  }

  [TestMethod]
  public void ResetStaysEmptyUntilNewDamageAndDoesNotLeakToANewSession()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0));
    meter.Reset(1, log.Count);
    Assert.IsNull(log.Board(meter, 1));
    log.Hit("a rat", 2, 200);
    Damage(200, log.Board(meter, 2));
    log.Die("a rat", 3);
    Damage(200, log.Board(meter, 4));
    Damage(700, log.Board(meter, 4, sessionId: 2));
  }

  [TestMethod]
  public void XReopenIsNotResetButExpiredLivesStillDisappear()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0));
    // No lifecycle call is made while the window is closed.
    log.Hit("a rat", 12, 200);
    Damage(700, log.Board(meter, 12));
    Assert.IsNull(log.Board(meter, 43));
  }

  [TestMethod]
  public void TimedModesKeepDeadFightsUntilTheirConfiguredQuietPeriod()
  {
    foreach (var mode in new[] { 3, 5, 10 })
    {
      var log = new Capture();
      var meter = new DamageOverlayLifecycle();
      log.Hit("a rat", 0, 500);
      Damage(500, log.Board(meter, 0, mode));
      log.Die("a rat", 1);
      log.Hit("a bat", 2, 200);
      Damage(700, log.Board(meter, 2, mode));
      Damage(700, log.Board(meter, 2 + mode, mode));
      Assert.IsNull(log.Board(meter, 2.5 + mode, mode));
      log.Hit("a bat", 3 + mode, 100);
      Damage(100, log.Board(meter, 3 + mode, mode));
    }
  }

  [TestMethod]
  public void ThreeSecondResetDoesNotDiscardACapturedAttackWaitingForProjection()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0, 3));
    log.Hit("a rat", 2, 200);

    // The second attack is captured, but the displayed snapshot still ends at second zero.
    Damage(500, log.Board(meter, 3.1, 3, project: false));
    Damage(500, log.Board(meter, 3.2, 3, project: false));
    var caughtUp = log.Board(meter, 3.3, 3);
    Damage(700, caughtUp);
    Assert.AreEqual(3d, caughtUp?.DamageStats.RaidStats.TotalSeconds);
    Damage(700, log.Board(meter, 5, 3));
    Assert.IsNull(log.Board(meter, 5.1, 3), "three seconds of real silence still resets the meter");
    log.Hit("a rat", 6, 100);
    Damage(100, log.Board(meter, 6, 3));
  }

  [TestMethod]
  public void PendingProjectionDoesNotAddGraceAfterThreeSecondsOfRealSilence()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0, 3));
    log.Hit("a rat", 2, 200);
    Damage(500, log.Board(meter, 3.1, 3, project: false));
    Damage(500, log.Board(meter, 5, 3, project: false));
    Assert.IsNull(log.Board(meter, 5.1, 3, project: false));
    Assert.IsNull(log.Board(meter, 5.2, 3));
    log.Hit("a rat", 6, 100);
    Damage(100, log.Board(meter, 6, 3));
  }

  [TestMethod]
  public void DamageWatermarkDoesNotMoveBackwardsWithOutOfOrderLogTimes()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0, 3));
    log.Hit("a rat", 2, 200);
    log.Hit("a rat", 1, 100);
    Damage(500, log.Board(meter, 4.5, 3, project: false));
    Damage(800, log.Board(meter, 4.6, 3));
  }

  [TestMethod]
  public void PendingDeathsDoNotExtendTheTimedDamageClock()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0, 3));
    log.Die("a rat", 2);
    Assert.IsNull(log.Board(meter, 3.1, 3, project: false));
  }

  [TestMethod]
  public void KnownNpcOnlyDamageDoesNotBorrowTheClockEvenBeforeProjection()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0, 3));
    log.NpcFight(2);
    Assert.IsNull(log.Board(meter, 3.1, 3, project: false), "known NPC-only damage is already excluded while pending");
    Assert.IsFalse(log.HasActivity(3.1, 3));
    Assert.IsNull(log.Board(meter, 3.2, 3), "classified NPC-only damage cannot extend our accumulation");
  }

  [TestMethod]
  public void ContinuingNpcNoiseCannotHoldAStalledThreeSecondMeterOpen()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0, 3));
    for (var second = 2; second <= 20; second += 2)
    {
      log.NpcFight(second);
      Assert.IsNull(log.Board(meter, second + 1.5, 3, project: false));
      Assert.IsFalse(log.HasActivity(second + 1.5, 3));
    }
    log.Hit("a rat", 22, 200);
    Damage(200, log.Board(meter, 22, 3));
  }

  [TestMethod]
  public void GenuinePendingCombatHasNoArbitraryGraceCap()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0, 3));
    for (var second = 2; second <= 20; second += 2)
    {
      log.Hit("a rat", second, 100);
      Damage(500, log.Board(meter, second + 1.5, 3, project: false));
      Assert.IsTrue(log.HasActivity(second + 1.5, 3));
    }
    Damage(1500, log.Board(meter, 21.5, 3));
    Assert.IsNull(log.Board(meter, 23.1, 3));
  }

  [TestMethod]
  public void ANewUnclassifiedOpponentCanContinueTimedAccumulationWhilePending()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    log.Die("a rat", 1);
    Damage(500, log.Board(meter, 1, 3));
    log.HitUnclassified("a new opponent", 2, 200);
    Damage(500, log.Board(meter, 3.1, 3, project: false));
    Assert.IsTrue(log.HasActivity(3.1, 3));
    Damage(700, log.Board(meter, 3.2, 3));
  }

  [TestMethod]
  public void OnKillKeepsTheSameLifeAndEligibilityAcrossAStaleThirtySecondBoundary()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0));
    log.Hit("a rat", 29, 200);
    Damage(500, log.Board(meter, 30.5, project: false));
    Assert.IsTrue(log.HasActivity(30.5, 0));
    Damage(700, log.Board(meter, 30.6));
    Assert.IsNull(log.Board(meter, 59.1));
    Assert.IsFalse(log.HasActivity(59.1, 0));
  }

  [TestMethod]
  public void PendingActivityChainsBridgeButNeverJoinSeparateLives()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0));
    log.Hit("a rat", 58, 100);
    log.Hit("a rat", 29, 100); // late arrival bridges the two pending spans to the existing life
    Damage(500, log.Board(meter, 59, project: false));
    log.Hit("a rat", 89, 200); // more than 30 s later: cannot extend the old life
    Assert.IsNull(log.Board(meter, 89.5, project: false));
    Assert.IsTrue(log.HasActivity(89.5, 0), "keep polling for the new life, without showing the old one's totals");
  }

  // Receipt renews the overlay's visibility even while projection lags; only data can move membership.
  [TestMethod]
  public void PendingDamageRenewsVisibilityWithoutRecountingTheOldRows()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0));
    log.Hit("a bat", 29, 200);
    Damage(500, log.Board(meter, 30.5, project: false));
    Assert.IsTrue(log.HasActivity(30.5, 0));
    Damage(700, log.Board(meter, 30.6));
  }

  /*
   * The reason membership is measured from the capture and not the wall clock: a board holding several lives must not shed rows
   * or move its totals while no line arrives. Only one thing may end it — the newest captured second plus the gap — and it ends
   * all of them in a single step.
   */
  [TestMethod]
  public void ASilentBoardHoldsEveryRowAndTotalUntilItsDeadline()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    log.Hit("a bat", 20, 300);
    Damage(800, log.Board(meter, 20));

    // Thirty polls of nothing arriving: the rat used to leave at second 30 and the total with it.
    for (var second = 21; second <= 49; second++) Damage(800, log.Board(meter, second));

    Assert.IsNull(log.Board(meter, 50.5), "second 20 plus the 30 s gap is the deadline, then the board is gone");
  }

  [TestMethod]
  public void ALifeIdleBeyondTheGapLeavesWhenNewDataMovesTheCaptureForward()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    log.Hit("a bat", 10, 300);
    Damage(800, log.Board(meter, 10));

    // Data moves the newest second to 39 (still one bat life: 29 s after its own last swing), and the rat is 39 seconds behind
    // it — so the rat leaves on the capture's own clock while the bat keeps its whole total.
    log.Hit("a bat", 39, 100);
    Damage(400, log.Board(meter, 39));
  }

  [TestMethod]
  public void PendingHitsDoNotResurrectACompletedLifePastItsDisplayHold()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    log.Die("a rat", 1);
    Damage(500, log.Board(meter, 1));
    log.Hit("a rat", 9, 200);
    Assert.IsNull(log.Board(meter, 9.5, project: false));
    Assert.IsTrue(log.HasActivity(9.5, 0));
    Damage(200, log.Board(meter, 9.6));
  }

  [TestMethod]
  public void ExplicitResetStillCutsPendingDamageBeforeTheClick()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0, 3));
    log.Hit("a rat", 2, 200);
    meter.Reset(1, log.Count);
    Assert.IsNull(log.Board(meter, 3.1, 3, project: false));
    Assert.IsNull(log.Board(meter, 3.2, 3));
    log.Hit("a rat", 4, 100);
    Damage(100, log.Board(meter, 4, 3));
  }

  [TestMethod]
  public void FreshSixtySecondMeterMatchesSelectingFiveFightsAcrossSeveralMinutes()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    DamageOverlayStats? overlay = null;
    for (var fight = 0; fight < 5; fight++)
    {
      var begin = fight * 40;
      var name = $"a training mob {fight}";
      log.Hit(name, begin, (uint)(1000 + fight * 100));
      log.Board(meter, begin, 60);
      log.Hit(name, begin + 5, 200);
      log.Die(name, begin + 6);
      overlay = log.Board(meter, begin + 7, 60);
    }

    // Every gap was shorter than the dial, so all five dead fights still belong to this accumulation.
    Assert.AreEqual(5, log.Rows.Count);
    Assert.IsTrue(log.Rows.All(row => row.Dead));
    Damage(7000, overlay);
    var selected = log.SelectedTotals();
    Assert.IsNotNull(selected);
    Assert.IsNotNull(overlay?.DamageStats);
    Assert.AreEqual(selected.RaidStats.Total, overlay.DamageStats.RaidStats.Total);
    Assert.AreEqual(selected.RaidStats.TotalSeconds, overlay.DamageStats.RaidStats.TotalSeconds);
    Assert.AreEqual(selected.RaidStats.Dps, overlay.DamageStats.RaidStats.Dps);
    Assert.AreEqual(selected.StatsList.Single().Total, overlay.DamageStats.StatsList.Single().Total);
    Assert.AreEqual(selected.StatsList.Single().Dps, overlay.DamageStats.StatsList.Single().Dps);

    // Polling during the remaining grace cannot change any of those numbers either.
    var held = log.Board(meter, 200, 60);
    Damage(7000, held);
    Assert.AreEqual(selected.RaidStats.Dps, held!.DamageStats.RaidStats.Dps);
  }

  [TestMethod]
  public void ChangingTheTimedDurationDoesNotEraseTheRunningTotal()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    for (var second = 0; second <= 12; second += 4)
    {
      log.Hit("a rat", second, 100);
      Damage((second / 4 + 1) * 100, log.Board(meter, second, 5));
    }
    Damage(400, log.Board(meter, 12, 10));
  }

  [TestMethod]
  public void TimedOneShotStillOpensAndShowsItsDamage()
  {
    var log = new Capture();
    log.Hit("a rat", 0, 500);
    log.Die("a rat", 0);
    Damage(500, log.Board(new DamageOverlayLifecycle(), 1, 5));
    Assert.IsTrue(log.HasActivity(1, 5));
    Assert.IsFalse(log.HasActivity(6, 5));
  }

  [TestMethod]
  public void OurDeathDoesNotEndTheOpponentAndItsTankingNumbersStay()
  {
    var log = new Capture();
    log.Hit("a rat", 0, 500);
    log.Incoming("a rat", 1, 100);
    log.Die("Illuminai", 2);
    var board = log.Board(new DamageOverlayLifecycle(), 12);
    Damage(500, board);
    Assert.IsNotNull(board!.TankStats);
    Assert.AreEqual(100L, (long)board.TankStats.RaidStats.Total);
  }

  [TestMethod]
  public void KillingAnOpponentDropsItsTankingAsWellAsItsDamage()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    log.Incoming("a rat", 1, 100);
    log.Hit("a bat", 2, 200);
    log.Incoming("a bat", 3, 50);
    log.Die("a rat", 4);
    var board = log.Board(meter, 5);
    Damage(200, board);
    Assert.IsNotNull(board!.TankStats);
    Assert.AreEqual(50L, (long)board.TankStats.RaidStats.Total);
  }

  [TestMethod]
  public void PendingMembershipStaysFrozenUntilTheWholeBoardExpires()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    log.Hit("a bat", 35, 200);
    Damage(200, log.Board(meter, 35));
    log.Hit("a rat", 29, 100); // delayed data bridges the rat, received after the bat
    var initial = log.Board(meter, 36, project: false);
    Damage(700, initial);
    Assert.IsNotNull(initial);
    for (var second = 37; second <= 65; second++)
    {
      var next = log.Board(meter, second, project: false);
      DamageMeterAssert.Equal(initial, next);
      Assert.IsNotNull(next);
      Assert.AreEqual(initial.DamageRdps.Total, next.DamageRdps.Total);
    }
    Assert.IsNull(log.Board(meter, 65.1, project: false));
  }

  private static void Damage(long expected, DamageOverlayStats? board)
  {
    Assert.IsNotNull(board?.DamageStats);
    Assert.AreEqual(expected, (long)board.DamageStats.RaidStats.Total);
  }

  private sealed class Capture
  {
    private readonly DamageFactTable _facts = new(64);
    private readonly EntityTimeline _timeline = new();
    private FightFactIndex _index = new();
    private OverlayPendingActivity _pending = new(0);
    private readonly DamageMeterCalculation _calculation = new();
    private readonly MeterArrivals _arrivals = new();
    private double _lastReceipt = double.NegativeInfinity;
    internal int Count => _facts.FactCount;
    internal MeterArrival Arrival => _arrivals.Latest(_facts, _timeline);
    internal List<DerivedFight> Rows { get; private set; } = [];

    internal Capture() => _timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "manual");

    internal void Hit(string mob, int second, uint amount) => Add("Illuminai", mob, mob, second, amount);

    internal void Incoming(string mob, int second, uint amount) => Add(mob, "Illuminai", mob, second, amount);

    internal void HitUnclassified(string mob, int second, uint amount)
    {
      _facts.AddFact(new DamageFact(_facts.FactCount, (long)(T0 + second), _facts.InternName("Illuminai"),
        _facts.InternName(mob), amount, LabelTypes.Melee, 0, 0, ushort.MaxValue));
      Receipt(second);
    }

    internal void NpcFight(int second)
    {
      _timeline.SetIdentity("a bat", IdentityKind.Npc, RuleStrength.Manual, "manual");
      Add("a guard", "a bat", "a guard", second, 200);
    }

    internal void Die(string name, int second) => _facts.AddDeath(new DeathFact(second, (long)(T0 + second),
      _facts.InternName(name), -1));

    internal DamageOverlayStats? Board(DamageOverlayLifecycle lifecycle, double second, int mode = 0, int sessionId = 1,
      bool project = true)
    {
      if (project)
      {
        ClassificationRules.Apply(_facts, _timeline);
        _index = new FightFactIndex(_timeline);
        Rows = FightProjection.Build(_facts, _timeline, _index.OnFact);
        _pending = new OverlayPendingActivity(_facts.FactCount);
      }
      _pending.Update(_facts, _timeline);
      DamageOverlayScope scope;
      do
      {
        var candidates = lifecycle.SelectCandidates(sessionId, Rows, mode);
        _calculation.Activity.Update(_index.CaptureMeterInput(candidates.Rows, _facts, candidates.FromTime,
          double.PositiveInfinity, candidates.FirstOrdinal));
      } while (!lifecycle.TrySelect(sessionId, Rows, T0 + second, mode, _arrivals.Latest(_facts, _timeline), Count,
        out scope, _pending, _calculation.Activity.For));
      var expected = DerivedTotals.ForOverlay(scope.Rows, _index, _facts, scope.FromTime,
        double.PositiveInfinity, scope.FirstOrdinal);
      var actual = _calculation.Update(_index.CaptureMeterInput(scope.Rows, _facts, scope.FromTime,
        double.PositiveInfinity, scope.FirstOrdinal));
      DamageMeterAssert.Equal(expected, actual);
      return actual;
    }

    internal bool HasActivity(double second, int mode) =>
      DamageOverlayLifecycle.HasRecentActivity(_arrivals.Latest(_facts, _timeline), T0 + second, mode);

    internal CombinedStats? SelectedTotals() => DerivedTotals.For(Rows, _index, _facts, new HealFactTable(_facts))?.CombinedStats;

    private void Add(string attacker, string defender, string mob, int second, uint amount)
    {
      _timeline.SetIdentity(mob, IdentityKind.Npc, RuleStrength.Manual, "manual");
      _facts.AddFact(new DamageFact(_facts.FactCount, (long)(T0 + second), _facts.InternName(attacker),
        _facts.InternName(defender), amount, LabelTypes.Melee, 0, 0, ushort.MaxValue));
      Receipt(second);
    }

    private void Receipt(double second)
    {
      _lastReceipt = Math.Max(_lastReceipt, T0 + second);
      _arrivals.Record(Count, _lastReceipt);
    }
  }
}
