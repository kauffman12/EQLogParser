namespace EQLogParser
{
  /* FCT feed tests: replay/live gating and lane mapping. Records are injected straight into the
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
      FctManager.Instance.EventsHitsProcessed += _onHits;
    }

    [TestCleanup]
    public void Teardown() => FctManager.Instance.EventsHitsProcessed -= _onHits;

    [TestMethod]
    public void DropsReplayRecords()
    {
      FireDamage("TestPlayer", 100, isMonitor: false);
      FireHeal("TestPlayer", 100, isMonitor: false);

      Assert.AreEqual(0, _batches.Count);
    }

    [TestMethod]
    public void EmitsLiveDamageWithTheRightLanes()
    {
      FireDamage("TestPlayer", 500);
      FireDamage("TestPlayer", 600, crit: true);
      FireDamage("OtherGuy", 700);

      Assert.AreEqual(3, _batches.Count);
      Assert.AreEqual(FctLane.DamageDealt, _batches[0][0].Lane);
      Assert.AreEqual(FctLane.Crit, _batches[1][0].Lane);
      Assert.AreEqual(FctLane.DamageTaken, _batches[2][0].Lane);
    }

    [TestMethod]
    public void HealsMapByDirectionAndOtherHealsAreDropped()
    {
      FireHeal("TestPlayer", 500);                       // I heal someone -> HealingDealt
      FireHeal("OtherGuy", 600, healed: "TestPlayer");   // someone heals me -> HealingReceived
      FireHeal("OtherGuy", 700, healed: "SomeOther");    // neither side is me -> dropped

      Assert.AreEqual(2, _batches.Count);
      Assert.AreEqual(FctLane.HealingDealt, _batches[0][0].Lane);
      Assert.AreEqual(FctLane.HealingReceived, _batches[1][0].Lane);
    }

    private static void FireDamage(string attacker, double beginTime, bool crit = false, bool isMonitor = true) =>
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
        IsMonitor = isMonitor,
      });

    private static void FireHeal(string healer, double beginTime, string healed = "SomeNpc", bool isMonitor = true) =>
      FctManager.Instance.HandleHeal(new HealProcessedEvent
      {
        Record = new HealRecord
        {
          Healer = healer,
          Healed = healed,
          Total = 50,
          SubType = "Blessing",
        },
        BeginTime = beginTime,
        IsMonitor = isMonitor,
      });
  }
}
