namespace EQLogParser
{
  /* FctLifeController is the presentation-side governor: 3.5 s baseline, compressed by predicted lane
   * fill. Pure logic (no WPF types), so it is fully unit-testable here. */
  [TestClass]
  public sealed class FctLifeControllerTest
  {

    [TestInitialize]
    public void ResetAmbient() => FctAmbient.Reset();
    [TestMethod]
    public void NoTrafficGetsTheBaseline()
    {
      var life = new FctLifeController();

      Assert.AreEqual(3500.0, life.NextLifetime(FctLane.DamageDealt, liveCount: 0, nowMs: 1000));
    }

    [TestMethod]
    public void LongGapResetsToTheBaseline()
    {
      var life = new FctLifeController();
      life.NextLifetime(FctLane.DamageDealt, 0, 1000);
      life.NextLifetime(FctLane.DamageDealt, 0, 1100);

      // 20 s of silence: the rate estimate is forgotten
      Assert.AreEqual(3500.0, life.NextLifetime(FctLane.DamageDealt, liveCount: 0, nowMs: 21100));
    }

    /* The crit badge is not a stream. FctLane.Crit exists so colour, halo and folding can pool the big numbers together; a modern raider crits
     * most of what it deals, so a governor keyed to that class would be timing nearly the whole overlay while reporting every real column empty.
     * Crits are timed by the column they came from (FctHitState.TrafficLane) and keep only a small margin over it: see
     * FctIngest.CritsAndOrdinaryNumbersShareOneClock. 0 here means "not governed on this lane", which the caller resolves to the baseline. */
    [TestMethod]
    public void TheCritClassIsNotAStream()
    {
      var life = new FctLifeController();

      Assert.AreEqual(0, FctLifeController.Capacity(FctLane.Crit));
      Assert.AreEqual(0, life.NextLifetime(FctLane.Crit, liveCount: 50, nowMs: 1000));
    }

    [TestMethod]
    public void SteadyHighRateCompressesTowardTheFloor()
    {
      var life = new FctLifeController();
      double t = 1000;

      // 10/s for two seconds on a half-full lane: the EMA converges, and a lane that keeps its predictions right
      // has no business holding numbers for the full baseline
      for (var i = 0; i < 20; i++)
      {
        t += 100;
        life.NextLifetime(FctLane.DamageDealt, liveCount: 2, nowMs: t);
      }

      var value = life.NextLifetime(FctLane.DamageDealt, liveCount: 2, nowMs: t + 100);
      Assert.IsTrue(value is >= FctLifeController.FloorMs and < FctLifeController.BaselineMs,
        $"expected compression below the {FctLifeController.BaselineMs:0} s baseline, got {value}");
    }

    /*
     * The floor is a legibility rule, not a headroom rule, and it is deliberately high. It was 1000 ms, which bought screen space by making numbers
     * unreadable at exactly the moment a raid asks the player to read the most; folding identical hits and evicting the least significant row are the
     * tools for crowding. And a low floor made the one exempt class dominate by comparison — crits fixed at 2800 ms against neighbours cut under a
     * second reads as two overlays running on different clocks, not as emphasis inside one (see FctIngestTest.CritsAndOrdinaryNumbersShareOneClock).
     * FctScale.Time remains the player's knob for a table that wants less time on screen.
     */
    [TestMethod]
    public void AFullLaneGetsTheFloor()
    {
      var life = new FctLifeController();
      double t = 1000;

      // establish a rate first
      for (var i = 0; i < 5; i++)
      {
        t += 200;
        life.NextLifetime(FctLane.DamageDealt, liveCount: 3, nowMs: t);
      }

      var value = life.NextLifetime(FctLane.DamageDealt, liveCount: (int)FctLifeController.Capacity(FctLane.DamageDealt), nowMs: t + 200);
      Assert.AreEqual(FctLifeController.FloorMs, value);
      Assert.IsTrue(value >= 2000, $"the most crowded lane still gets {value:0} ms to be read in");
    }

    [TestMethod]
    public void LifetimeIsSlackDividedByRate()
    {
      var life = new FctLifeController();
      life.NextLifetime(FctLane.HealingDealt, 0, 1000);

      // one interval of 250 ms -> instant rate 4/s, EMA (alpha .25) -> 1/s; slack 5 - 2 = 3 -> 3000 ms. Spaced above the floor
      // on purpose: below it the clamp answers, and this test is about the arithmetic before the clamp.
      var value = life.NextLifetime(FctLane.HealingDealt, liveCount: 2, nowMs: 1250);
      Assert.AreEqual(3000, value, 1);
    }

    [TestMethod]
    public void CapacityDiffersByLane()
    {
      Assert.AreEqual(7, FctLifeController.Capacity(FctLane.DamageDealt));
      Assert.AreEqual(7, FctLifeController.Capacity(FctLane.DamageTaken));
      Assert.AreEqual(5, FctLifeController.Capacity(FctLane.HealingDealt));
      Assert.AreEqual(5, FctLifeController.Capacity(FctLane.HealingReceived));
      Assert.AreEqual(0, FctLifeController.Capacity(FctLane.Crit));
    }
  }
}