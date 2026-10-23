namespace EQLogParser;

[TestClass]
public sealed class MeterUnplacedReceiptsTest
{
  private readonly DamageFactTable _facts = new(16);
  private readonly EntityTimeline _timeline = new();
  private readonly MeterArrivals _arrivals = new();
  private bool _bane;

  [TestInitialize]
  public void Initialize()
  {
    PipelineHarness.EnsureDataStore();
    _bane = AppSettings.IsBaneDamageEnabled;
    AppSettings.IsBaneDamageEnabled = false;
  }

  [TestCleanup]
  public void Cleanup() => AppSettings.IsBaneDamageEnabled = _bane;

  [TestMethod]
  [DataRow("Unplaced foe", "Newtank", true)]
  [DataRow("Newtank", "Unplaced foe", false)]
  [DataRow("Unplaced foe", "Another foe", false)]
  [DataRow("Newattacker", "Newdefender", false)]
  public void UnplacedReceiptsFollowTheProjectedBoard(string attacker, string defender, bool incoming)
  {
    Add(attacker, defender, 100);
    // Cheap projections use the last full timeline: new names must stay Unknown for this regression.
    var cache = new FightProjection.FightProjectionCache();
    var rows = cache.Project(_facts, _timeline);
    var input = cache.Index.CaptureMeterInput(rows, _facts, double.NegativeInfinity, double.PositiveInfinity, 0);
    var stats = new DamageMeterCalculation().Update(input);
    Assert.IsNotNull(stats);
    var board = incoming ? stats.TankStats : stats.DamageStats;
    Assert.IsNotNull(board);
    Assert.AreEqual(600L, board.RaidStats.Total);
    Assert.IsNull(incoming ? stats.DamageStats : stats.TankStats);

    var panels = incoming ? MeterPanels.Tanking : MeterPanels.Damage;
    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline, panels: panels));
    Assert.AreEqual(MeterArrival.None,
      _arrivals.Latest(_facts, _timeline, panels: incoming ? MeterPanels.Damage : MeterPanels.Tanking));
    Assert.IsTrue(DamageOverlayLifecycle.HasRecentActivity(_arrivals.Observed(panels), 102, 3));
    Assert.IsFalse(DamageOverlayLifecycle.HasRecentActivity(_arrivals.Observed(panels), 104, 3));
    Assert.AreEqual(IdentityKind.Unknown, _timeline.IdentityAt(attacker, FixtureTime.Base));
    Assert.AreEqual(IdentityKind.Unknown, _timeline.IdentityAt(defender, FixtureTime.Base));
  }

  [TestMethod]
  public void UnplacedIncomingIsExemptFromOutgoingExclusions()
  {
    Add("Unplaced foe", "Newtank", 100, LabelTypes.Bane);
    Add("Newtank", "Unplaced foe", 102, LabelTypes.Bane);

    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline, panels: MeterPanels.Tanking));
    Assert.AreEqual(MeterArrival.None, _arrivals.Latest(_facts, _timeline, panels: MeterPanels.Damage));
    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline, panels: MeterPanels.Both));
  }

  [TestMethod]
  public void UnplacedIncomingDoesNotBridgeTheDamageLanesQuietGap()
  {
    Add("Newtank", "Unplaced foe", 100);
    foreach (var receipt in new[] { 102, 104, 106 }) Add("Unplaced foe", "Newtank", receipt);
    Add("Newtank", "Unplaced foe", 108);

    Assert.AreEqual(new MeterArrival(5, 108, 4), _arrivals.Latest(_facts, _timeline, 3, MeterPanels.Damage));
    Assert.AreEqual(new MeterArrival(5, 108), _arrivals.Latest(_facts, _timeline, 3, MeterPanels.Both));
  }

  private void Add(string attacker, string defender, double receipt, byte type = LabelTypes.Melee)
  {
    _facts.AddFact(new DamageFact(_facts.FactCount, FixtureTime.BaseL, _facts.InternName(attacker),
      _facts.InternName(defender), 600, type, 0, 0, DamageFactTable.NoSubtype));
    _arrivals.Record(_facts.FactCount, receipt);
  }
}
