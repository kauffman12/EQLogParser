namespace EQLogParser
{
  /* FctLifeController is the presentation-side governor: 7 s baseline, compressed by predicted lane
   * fill. Pure logic (no WPF types), so it is fully unit-testable here. */
  [TestClass]
  public sealed class FctLifeControllerTest
  {
    [TestMethod]
    public void NoTrafficGetsTheBaseline()
    {
      var life = new FctLifeController();

      Assert.AreEqual(7000.0, life.NextLifetime(FctSimLane.DamageDealt, liveCount: 0, nowMs: 1000));
    }

    [TestMethod]
    public void LongGapResetsToTheBaseline()
    {
      var life = new FctLifeController();
      life.NextLifetime(FctSimLane.DamageDealt, 0, 1000);
      life.NextLifetime(FctSimLane.DamageDealt, 0, 1100);

      // 20 s of silence: the rate estimate is forgotten
      Assert.AreEqual(7000.0, life.NextLifetime(FctSimLane.DamageDealt, liveCount: 0, nowMs: 21100));
    }

    [TestMethod]
    public void CritsDoNotAdapt()
    {
      var life = new FctLifeController();

      Assert.AreEqual(0, life.NextLifetime(FctSimLane.Crit, liveCount: 50, nowMs: 1000));
    }

    [TestMethod]
    public void SteadyHighRateCompressesTowardTheFloor()
    {
      var life = new FctLifeController();
      double t = 1000;

      // 10/s for a couple of seconds: the EMA converges and slack (7 - live) at low fill is small
      for (var i = 0; i < 20; i++)
      {
        t += 100;
      }

      var value = life.NextLifetime(FctSimLane.DamageDealt, liveCount: 0, nowMs: t);
      Assert.IsTrue(value >= 1000 && value <= 3000, $"expected compressed lifetime, got {value}");
    }

    [TestMethod]
    public void AFullLaneGetsTheFloor()
    {
      var life = new FctLifeController();
      double t = 1000;

      // establish a rate first
      for (var i = 0; i < 5; i++)
      {
        t += 200;
        life.NextLifetime(FctSimLane.DamageDealt, liveCount: 3, nowMs: t);
      }

      var value = life.NextLifetime(FctSimLane.DamageDealt, liveCount: (int)FctLifeController.Capacity(FctSimLane.DamageDealt), nowMs: t + 200);
      Assert.AreEqual(1000.0, value);
    }

    [TestMethod]
    public void LifetimeIsSlackDividedByRate()
    {
      var life = new FctLifeController();
      life.NextLifetime(FctSimLane.HealingDealt, 0, 1000);

      // one interval of 200 ms -> instant rate 5/s, EMA (alpha .25) -> 1.25/s; slack 5 - 3 = 2 -> 1600 ms
      var value = life.NextLifetime(FctSimLane.HealingDealt, liveCount: 3, nowMs: 1200);
      Assert.AreEqual(1600, value, 1);
    }

    [TestMethod]
    public void CapacityDiffersByLane()
    {
      Assert.AreEqual(7, FctLifeController.Capacity(FctSimLane.DamageDealt));
      Assert.AreEqual(7, FctLifeController.Capacity(FctSimLane.DamageTaken));
      Assert.AreEqual(5, FctLifeController.Capacity(FctSimLane.HealingDealt));
      Assert.AreEqual(5, FctLifeController.Capacity(FctSimLane.HealingReceived));
      Assert.AreEqual(0, FctLifeController.Capacity(FctSimLane.Crit));
    }
  }
}
