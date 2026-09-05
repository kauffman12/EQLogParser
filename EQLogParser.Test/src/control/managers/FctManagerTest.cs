namespace EQLogParser
{
  /* FCT feed tests: live/replay gating and lane mapping. Records are injected straight into the
   * manager's handlers, so no log lines are parsed. */
  [DoNotParallelize]
  [TestClass]
  public sealed class FctManagerTest
  {
    private Action<IReadOnlyList<FctHitCommand>> _onHits;
    private List<IReadOnlyList<FctHitCommand>> _batches = [];

    [TestInitialize]
    public void Setup()
    {
      ConfigUtil.PlayerName = "TestPlayer";
      _batches = [];
      _onHits = batch => _batches.Add(batch);
      PlayerRegistry.Instance.Clear();
      FctManager.Instance.Clear(true);
      FctManager.Instance.EventsHitsProcessed += _onHits;
    }

    [TestCleanup]
    public void Teardown()
    {
      FctManager.Instance.EventsHitsProcessed -= _onHits;
      FctManager.Instance.Clear(true);
    }

    [TestMethod]
    public void SuppressesRecordsBeforeTheFirstMonitorLine()
    {
      FireDamage("TestPlayer", 100);

      Assert.AreEqual(0, _batches.Count);
    }

    [TestMethod]
    public void EmitsLiveDamageWithTheRightLanes()
    {
      FctManager.Instance.MarkLiveLine(500);

      FireDamage("TestPlayer", 500);
      FireDamage("TestPlayer", 600, crit: true);
      FireDamage("OtherGuy", 700);

      Assert.AreEqual(3, _batches.Count);
      Assert.AreEqual(FctLane.DamageDealt, _batches[0][0].Lane);
      Assert.AreEqual(FctLane.Crit, _batches[1][0].Lane);
      Assert.AreEqual(FctLane.DamageTaken, _batches[2][0].Lane);
    }

    [TestMethod]
    public void DropsReplayedRecordsOpenedWhileMonitoring()
    {
      FctManager.Instance.MarkLiveLine(500);

      FireDamage("TestPlayer", 100);

      Assert.AreEqual(0, _batches.Count);
    }

    [TestMethod]
    public void ToleratesSubGraceTimestampsAtTheLiveBoundary()
    {
      FctManager.Instance.MarkLiveLine(500);

      FireDamage("TestPlayer", 490); // inside the 50 ms replay grace

      Assert.AreEqual(1, _batches.Count);
      Assert.AreEqual(FctLane.DamageDealt, _batches[0][0].Lane);
    }

    [TestMethod]
    public void HealsByMeMapToHealingAndOtherHealersAreDropped()
    {
      FctManager.Instance.MarkLiveLine(500);

      FireHeal("TestPlayer", 500);
      FireHeal("OtherGuy", 600);

      Assert.AreEqual(1, _batches.Count);
      Assert.AreEqual(FctLane.Healing, _batches[0][0].Lane);
    }

    [TestMethod]
    public void ClearReArmsTheGate()
    {
      FctManager.Instance.MarkLiveLine(500);
      FctManager.Instance.Clear(true);

      FireDamage("TestPlayer", 600);

      Assert.AreEqual(0, _batches.Count);
    }

    private static void FireDamage(string attacker, double beginTime, bool crit = false) =>
      FctManager.Instance.HandleDamage(new DamageProcessedEvent
      {
        Record = new DamageRecord
        {
          Attacker = attacker,
          Defender = "SomeNpc",
          Total = 100,
          SubType = "melee",
          ModifiersMask = crit ? LineModifiersParser.Crit : LineModifiersParser.None,
        },
        BeginTime = beginTime,
      });

    private static void FireHeal(string healer, double beginTime) =>
      FctManager.Instance.HandleHeal(new HealProcessedEvent
      {
        Record = new HealRecord
        {
          Healer = healer,
          Total = 50,
          SubType = "Blessing",
        },
        BeginTime = beginTime,
      });
  }
}
