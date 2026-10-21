namespace EQLogParser;

[TestClass]
public sealed class MeterArrivalsTest
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
    _timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "manual");
    _timeline.SetIdentity("a rat", IdentityKind.Npc, RuleStrength.Manual, "manual");
  }

  [TestCleanup]
  public void Cleanup() => AppSettings.IsBaneDamageEnabled = _bane;

  [TestMethod]
  public void ReadingRebuildingAndChangingFiltersNeverRestampAReceipt()
  {
    Add(100, LabelTypes.Melee);
    var first = _arrivals.Latest(_facts, _timeline);
    Assert.AreEqual(new MeterArrival(1, 100), first);
    _arrivals.Record(1, 105); // a duplicate notification is not receipt of another fact
    Assert.AreEqual(first, _arrivals.Latest(_facts, _timeline));
    _timeline.SetIdentity("Unrelated", IdentityKind.Player, RuleStrength.Manual, "manual");
    AppSettings.IsBaneDamageEnabled = true;
    Assert.AreEqual(first, _arrivals.Latest(_facts, _timeline));
    Assert.IsFalse(DamageOverlayLifecycle.HasRecentActivity(first, 110.1, 10));
  }

  [TestMethod]
  public void ExcludedHitsCannotRenewAnAcceptedReceiptEvenInTheSameBatch()
  {
    Add(100, LabelTypes.Melee);
    Add(100, LabelTypes.Bane);
    Add(109, LabelTypes.Bane);
    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline));
    AppSettings.IsBaneDamageEnabled = true;
    Assert.AreEqual(new MeterArrival(3, 109), _arrivals.Latest(_facts, _timeline),
      "reconsideration uses the original receipt, never the time the setting changed");
  }

  [TestMethod]
  public void OldReceiptsCannotBeRenewedByRecentNpcOnlyNoise()
  {
    Add(0, LabelTypes.Melee);
    _timeline.SetIdentity("a bat", IdentityKind.Npc, RuleStrength.Manual, "manual");
    _facts.AddFact(new DamageFact(1, FixtureTime.BaseL, _facts.InternName("a rat"),
      _facts.InternName("a bat"), 100, LabelTypes.Melee, 0, 0, DamageFactTable.NoSubtype));
    _arrivals.Record(2, 200);
    Assert.AreEqual(MeterArrival.None, _arrivals.Latest(_facts, _timeline));
  }

  [TestMethod]
  public void CaptureNotifiesAfterTheFactIsPublishedAndNotWhenItIsRead()
  {
    DamageLineParser.ResetProcessState();
    RecordsStore.Instance.Clear(false);
    PlayerRegistry.Instance.Clear();
    var notifications = 0;
    var capture = new CombatCapture(_facts, damageArrived: through =>
    {
      Assert.AreEqual(_facts.FactCount, through);
      notifications++;
      _arrivals.Record(through, 100);
    });
    capture.Start();
    try
    {
      const string action = "Illuminai hits a rat for 600 points of damage.";
      Assert.IsTrue(DamageLineParser.Process(new LineData
      { Action = action, BeginTime = FixtureTime.Base, Split = action.Split(' ') }));
      Assert.AreEqual(1, notifications);
      Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline));
      _ = _facts.At(0);
      Assert.AreEqual(1, notifications);
    }
    finally
    {
      capture.Stop();
      DamageLineParser.ResetProcessState();
      RecordsStore.Instance.Clear(false);
      PlayerRegistry.Instance.Clear();
    }
  }

  [TestMethod]
  public void APausedReceiptReadDoesNotOwnIngestOrChaseItsGrowingBatch()
  {
    Add(100, LabelTypes.Melee);
    var read = _arrivals.BeginRead(_timeline);
    // Ingest can extend the same millisecond batch and start another while this worker question is paused.
    Add(100, LabelTypes.Melee);
    Add(101, LabelTypes.Melee);
    Assert.AreEqual(1, read.Through);
    Assert.AreEqual(new MeterArrival(1, 100), read.Complete(_facts));
    Assert.AreEqual(new MeterArrival(3, 101), _arrivals.Latest(_facts, _timeline),
      "completing an old prefix must not acknowledge the damage received during its scan");
  }

  [TestMethod]
  public void AnOlderReadCannotOverwriteANewerVerifiedReceipt()
  {
    Add(100, LabelTypes.Melee);
    var old = _arrivals.BeginRead(_timeline);
    Add(101, LabelTypes.Melee);
    var newest = _arrivals.Latest(_facts, _timeline);
    old.Complete(_facts);
    Assert.AreEqual(newest, _arrivals.Observed);
    Assert.AreEqual(newest, _arrivals.Latest(_facts, _timeline));
  }

  [TestMethod]
  public void CancellationAcknowledgesNothingAndTheSamePrefixCanBeRetried()
  {
    Add(100, LabelTypes.Melee);
    var read = _arrivals.BeginRead(_timeline);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    Assert.ThrowsExactly<OperationCanceledException>(() => read.Complete(_facts, cancelled.Token));
    Assert.AreEqual(MeterArrival.None, _arrivals.Observed);
    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline));
  }

  [TestMethod]
  public void AReadCannotPublishReceiptsPrunedWhileItWasPaused()
  {
    Add(0, LabelTypes.Melee);
    var read = _arrivals.BeginRead(_timeline);
    Add(200, LabelTypes.Melee);
    Assert.AreEqual(MeterArrival.None, read.Complete(_facts));
    Assert.AreEqual(MeterArrival.None, _arrivals.Observed);
    Assert.AreEqual(new MeterArrival(2, 200), _arrivals.Latest(_facts, _timeline));
  }

  [TestMethod]
  public void AChangedVerdictInvalidatesAnUnfinishedReadWithoutRestampingItsRetry()
  {
    Add(100, LabelTypes.Melee);
    var read = _arrivals.BeginRead(_timeline);
    _timeline.SetIdentity("Illuminai", IdentityKind.Npc, RuleStrength.Manual, "manual");
    Assert.AreEqual(MeterArrival.None, read.Complete(_facts));
    Assert.AreEqual(MeterArrival.None, _arrivals.Observed);
    Assert.AreEqual(MeterArrival.None, _arrivals.Latest(_facts, _timeline));
    _timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "correction");
    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline));
  }

  [TestMethod]
  public void AChangedFilterInvalidatesAnUnfinishedReadWithoutRestampingItsRetry()
  {
    Add(100, LabelTypes.Bane);
    var read = _arrivals.BeginRead(_timeline);
    AppSettings.IsBaneDamageEnabled = true;
    Assert.AreEqual(MeterArrival.None, read.Complete(_facts));
    Assert.AreEqual(MeterArrival.None, _arrivals.Observed);
    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline));
  }

  private void Add(double receipt, byte type)
  {
    _facts.AddFact(new DamageFact(_facts.FactCount, FixtureTime.BaseL, _facts.InternName("Illuminai"),
      _facts.InternName("a rat"), 600, type, 0, 0, DamageFactTable.NoSubtype));
    _arrivals.Record(_facts.FactCount, receipt);
  }
}
