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
    Assert.IsTrue(DamageOverlayLifecycle.HasRecentActivity(log.Rows, T0 + 1, 0));
    var announced = DamageOverlayLifecycle.LatestActivityOrdinal(log.Rows);
    Assert.IsTrue(announced > 0);
    Damage(500, log.Board(meter, 8));
    Assert.AreEqual(announced, DamageOverlayLifecycle.LatestActivityOrdinal(log.Rows), "republishing a death is not new damage");
    Assert.IsNull(log.Board(meter, 8.5));
    Assert.IsFalse(DamageOverlayLifecycle.HasRecentActivity(log.Rows, T0 + 8.5, 0));
    Assert.IsNull(log.Board(meter, 10));
  }

  [TestMethod]
  public void TwoArrivalsInOneSecondHaveDifferentOpeningWatermarks()
  {
    var log = new Capture();
    log.Hit("a rat", 0, 500);
    log.Board(new DamageOverlayLifecycle(), 0);
    var first = DamageOverlayLifecycle.LatestActivityOrdinal(log.Rows);
    log.Hit("a rat", 0, 200);
    log.Board(new DamageOverlayLifecycle(), 0);
    Assert.IsTrue(DamageOverlayLifecycle.LatestActivityOrdinal(log.Rows) > first);
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
  public void CompletedDisplayUsesTheObservedDeathNotTheLastExchange()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    log.Die("a rat", 10);
    Damage(500, log.Board(meter, 11));
    Assert.AreEqual(T0 + 10, log.Rows.Single().DeathTime);
    Damage(500, log.Board(meter, 18));
    Assert.IsNull(log.Board(meter, 18.5));
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
    meter.Reset(1, T0 + 0.5);
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
  public void FreshProjectionStopsBorrowingTheRawClockForUnrelatedDamage()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0, 3));
    log.NpcFight(2);
    Damage(500, log.Board(meter, 3.1, 3, project: false));
    Assert.IsNull(log.Board(meter, 3.2, 3), "classified NPC-only damage cannot extend our accumulation");
  }

  [TestMethod]
  public void ExplicitResetStillCutsPendingDamageBeforeTheClick()
  {
    var log = new Capture();
    var meter = new DamageOverlayLifecycle();
    log.Hit("a rat", 0, 500);
    Damage(500, log.Board(meter, 0, 3));
    log.Hit("a rat", 2, 200);
    meter.Reset(1, T0 + 2.5);
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
    Assert.IsTrue(DamageOverlayLifecycle.HasRecentActivity(log.Rows, T0 + 1, 5));
    Assert.IsFalse(DamageOverlayLifecycle.HasRecentActivity(log.Rows, T0 + 6, 5));
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
    private int _projectedDamageCount;
    internal List<DerivedFight> Rows { get; private set; } = [];

    internal Capture() => _timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "manual");

    internal void Hit(string mob, int second, uint amount) => Add("Illuminai", mob, mob, second, amount);

    internal void Incoming(string mob, int second, uint amount) => Add(mob, "Illuminai", mob, second, amount);

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
        _projectedDamageCount = _facts.FactCount;
      }
      var pendingDamageTime = _facts.FactCount > _projectedDamageCount ? _facts.LatestDamageTime : double.NaN;
      var scope = lifecycle.Select(sessionId, Rows, T0 + second, mode, pendingDamageTime);
      return DerivedTotals.ForOverlay(scope.Rows, _index, _facts, new HealFactTable(_facts), scope.FromTime, T0 + second);
    }

    internal CombinedStats? SelectedTotals() => DerivedTotals.For(Rows, _index, _facts, new HealFactTable(_facts))?.CombinedStats;

    private void Add(string attacker, string defender, string mob, int second, uint amount)
    {
      _timeline.SetIdentity(mob, IdentityKind.Npc, RuleStrength.Manual, "manual");
      _facts.AddFact(new DamageFact(_facts.FactCount, (long)(T0 + second), _facts.InternName(attacker),
        _facts.InternName(defender), amount, LabelTypes.Melee, 0, 0, ushort.MaxValue));
    }
  }
}
