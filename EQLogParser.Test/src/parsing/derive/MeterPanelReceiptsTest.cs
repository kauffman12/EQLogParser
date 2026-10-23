namespace EQLogParser;

[TestClass]
public sealed class MeterPanelReceiptsTest
{
  private readonly DamageFactTable _facts = new(16);
  private readonly EntityTimeline _timeline = new();
  private readonly MeterArrivals _arrivals = new();

  [TestInitialize]
  public void Initialize()
  {
    PipelineHarness.EnsureDataStore();
    _timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "manual");
    _timeline.SetIdentity("a rat", IdentityKind.Npc, RuleStrength.Manual, "manual");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void AHiddenLaneCannotBridgeAnUnobservedQuietGap(bool incoming)
  {
    Add(100, incoming);
    foreach (var time in new[] { 102, 104, 106 }) Add(time, !incoming);
    Add(108, incoming);
    var panels = incoming ? MeterPanels.Tanking : MeterPanels.Damage;

    Assert.AreEqual(new MeterArrival(5, 108, 4), _arrivals.Latest(_facts, _timeline, 3, panels));
    Assert.AreEqual(new MeterArrival(5, 108), _arrivals.Latest(_facts, _timeline, 3, MeterPanels.Both),
      "both panels together had no quiet gap");
    Assert.AreEqual(new MeterArrival(5, 108), _arrivals.Latest(_facts, _timeline, 8, panels),
      "a gap equal to the mode still does not reset");
    Assert.AreEqual(new MeterArrival(5, 108), _arrivals.Latest(_facts, _timeline, 0, panels),
      "on-kill has no timed cut");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void MixedBatchesKeepEachLanesOwnEndpointsAndPartialSuffix(bool incoming)
  {
    Add(0, incoming);
    Add(2, !incoming);
    Add(4, !incoming);
    Add(4, incoming);
    Add(4, !incoming);
    var panels = incoming ? MeterPanels.Tanking : MeterPanels.Damage;
    var read = _arrivals.BeginRead(_timeline);
    Add(4, incoming);

    Assert.AreEqual(new MeterArrival(4, 4, 3), read.Complete(_facts, 3, panels),
      "neither end of the mixed batch belongs to the selected lane");
    Assert.AreEqual(new MeterArrival(4, 4), _arrivals.Observed(panels));
    Assert.AreEqual(new MeterArrival(5, 4), _arrivals.Observed(MeterPanels.Both),
      "equal receipt times still keep the latest ordinal across both lanes");
    Assert.AreEqual(new MeterArrival(6, 4, 3), _arrivals.Latest(_facts, _timeline, 3, panels),
      "completing the growing batch preserves the earlier cut");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void PerLaneCutsSurviveCompactionAndReclassification(bool incoming)
  {
    Add(0, incoming);
    Add(2, !incoming);
    Add(4, !incoming);
    for (var time = 6; time <= 3010; time += 2) Add(time, incoming);
    var panels = incoming ? MeterPanels.Tanking : MeterPanels.Damage;
    var expected = new MeterArrival(_facts.FactCount, 3010, 3);

    Assert.AreEqual(expected, _arrivals.Latest(_facts, _timeline, 3, panels));
    _timeline.SetIdentity("Unrelated", IdentityKind.Player, RuleStrength.Manual, "manual");
    var retained = _arrivals.BeginRead(_timeline);
    Assert.HasCount(61, retained.Batches);
    Assert.AreEqual(expected, retained.Complete(_facts, 3, panels));
    Assert.AreEqual(0, _arrivals.Latest(_facts, _timeline, 3, MeterPanels.Both).FirstOrdinal);
  }

  [TestMethod]
  public void AnAbsentLaneDoesNotInheritTheOtherLanesCut()
  {
    Add(0, false);
    Add(10, false);
    Assert.AreEqual(MeterArrival.None, _arrivals.Latest(_facts, _timeline, 3, MeterPanels.Tanking));
    Assert.AreEqual(new MeterArrival(2, 10, 1), _arrivals.Latest(_facts, _timeline, 3, MeterPanels.Damage));
  }

  private void Add(double receipt, bool incoming)
  {
    var player = _facts.InternName("Illuminai");
    var npc = _facts.InternName("a rat");
    _facts.AddFact(new DamageFact(_facts.FactCount, FixtureTime.BaseL, incoming ? npc : player,
      incoming ? player : npc, 600, LabelTypes.Melee, 0, 0, DamageFactTable.NoSubtype));
    _arrivals.Record(_facts.FactCount, receipt);
  }
}
