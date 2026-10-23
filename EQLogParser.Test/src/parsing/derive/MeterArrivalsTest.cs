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
  public void EnablingNeedsWorkerVerificationBeforeAnUnobservedReceiptCanOpenTheMeter()
  {
    Add(100, LabelTypes.Melee);
    Assert.IsFalse(DamageOverlayLifecycle.HasRecentActivity(_arrivals.Observed(MeterPanels.Both), 105, 10),
      "a disabled meter may have fresh captured damage but no verified opening cache");

    _ = _arrivals.Latest(_facts, _timeline);
    Assert.IsTrue(DamageOverlayLifecycle.HasRecentActivity(_arrivals.Observed(MeterPanels.Both), 105, 10));
    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Observed(MeterPanels.Both), "verification must keep the ingest timestamp");
    Assert.IsFalse(DamageOverlayLifecycle.HasRecentActivity(_arrivals.Observed(MeterPanels.Both), 111, 10),
      "enabling is not a fresh receipt");
  }

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
    var capture = new CombatCapture(_facts, damageArrived: (through, sourceTime) =>
    {
      Assert.AreEqual(_facts.FactCount, through);
      Assert.AreEqual(FixtureTime.Base, sourceTime);
      notifications++;
      _arrivals.Record(through, 100, sourceTime, FixtureTime.Base);
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
    Assert.AreEqual(newest, _arrivals.Observed(MeterPanels.Both));
    Assert.AreEqual(newest, _arrivals.Latest(_facts, _timeline));
  }

  [TestMethod]
  public void CancellationAcknowledgesNothingAndTheSamePrefixCanBeRetried()
  {
    Add(100, LabelTypes.Melee);
    var read = _arrivals.BeginRead(_timeline);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    Assert.ThrowsExactly<OperationCanceledException>(() => read.Complete(_facts, cancellation: cancelled.Token));
    Assert.AreEqual(MeterArrival.None, _arrivals.Observed(MeterPanels.Both));
    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline));
  }

  [TestMethod]
  public void AReadCannotPublishAReceiptAgedOutWhileItWasPaused()
  {
    Add(0, LabelTypes.Melee);
    var read = _arrivals.BeginRead(_timeline);
    Add(200, LabelTypes.Melee);
    Assert.AreEqual(MeterArrival.None, read.Complete(_facts));
    Assert.AreEqual(MeterArrival.None, _arrivals.Observed(MeterPanels.Both));
    Assert.AreEqual(new MeterArrival(2, 200), _arrivals.Latest(_facts, _timeline));
  }

  [TestMethod]
  public void AChangedVerdictInvalidatesAnUnfinishedReadWithoutRestampingItsRetry()
  {
    Add(100, LabelTypes.Melee);
    var read = _arrivals.BeginRead(_timeline);
    _timeline.SetIdentity("Illuminai", IdentityKind.Npc, RuleStrength.Manual, "manual");
    Assert.IsFalse(read.InputsCurrent, "an invalidated scan must be retried before retiring an accumulation");
    Assert.AreEqual(MeterArrival.None, read.Complete(_facts));
    Assert.AreEqual(MeterArrival.None, _arrivals.Observed(MeterPanels.Both));
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
    Assert.IsFalse(read.InputsCurrent, "a filter change is not proof that the checked prefix expired");
    Assert.AreEqual(MeterArrival.None, read.Complete(_facts));
    Assert.AreEqual(MeterArrival.None, _arrivals.Observed(MeterPanels.Both));
    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline));
  }

  [TestMethod]
  public void UnexaminedBatchesSurviveRetentionThenCompactWithoutLosingTheGap()
  {
    Add(0, LabelTypes.Melee);
    for (var second = 10; second <= 3010; second += 2) Add(second, LabelTypes.Melee);
    var read = _arrivals.BeginRead(_timeline);
    Assert.HasCount(1502, read.Batches, "no unchecked receipt can age out, even during a long stall");
    Assert.AreEqual(new MeterArrival(1502, 3010, 1), read.Complete(_facts, mode: 3));

    _timeline.SetIdentity("Unrelated", IdentityKind.Player, RuleStrength.Manual, "manual");
    var retained = _arrivals.BeginRead(_timeline);
    Assert.HasCount(61, retained.Batches, "checked metadata outside the recent window is retired");
    Assert.AreEqual(new MeterArrival(1502, 3010, 1), retained.Complete(_facts, mode: 3),
      "rechecking retained data starts from the compact historical boundary");
    Assert.AreEqual(new MeterArrival(1502, 3010), _arrivals.Latest(_facts, _timeline, mode: 10),
      "each mode retains its own boundary, even when nobody was using that mode during the gap");
  }

  [TestMethod]
  public void AQuietLogAndDuplicateNotificationsAddNoReceiptMetadata()
  {
    Add(100, LabelTypes.Melee);
    _ = _arrivals.Latest(_facts, _timeline);
    for (var second = 101; second < 1000; second++)
    {
      _arrivals.Record(1, second);
      Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline));
    }
    _timeline.SetIdentity("Unrelated", IdentityKind.Player, RuleStrength.Manual, "manual");
    Assert.HasCount(1, _arrivals.BeginRead(_timeline).Batches);
  }

  [TestMethod]
  public void ExclusionsAreRecheckedBeforeDecidingWhetherReceiptsBridgeAGap()
  {
    Add(0, LabelTypes.Melee);
    Add(2, LabelTypes.Bane);
    Add(4, LabelTypes.Melee);
    Assert.AreEqual(new MeterArrival(3, 4, 2), _arrivals.Latest(_facts, _timeline, mode: 3));
    AppSettings.IsBaneDamageEnabled = true;
    Assert.AreEqual(new MeterArrival(3, 4), _arrivals.Latest(_facts, _timeline, mode: 3),
      "an eligible intermediate receipt bridges the gap without changing its timestamp");
    AppSettings.IsBaneDamageEnabled = false;
    Assert.AreEqual(new MeterArrival(3, 4, 2), _arrivals.Latest(_facts, _timeline, mode: 3));
  }

  [TestMethod]
  public void APartialMillisecondBatchKeepsItsNewSuffixAndTheEarlierGap()
  {
    Add(0, LabelTypes.Melee);
    Add(10, LabelTypes.Bane);
    Add(10, LabelTypes.Melee);
    var read = _arrivals.BeginRead(_timeline);
    Add(10, LabelTypes.Melee);
    Assert.AreEqual(new MeterArrival(3, 10, 2), read.Complete(_facts, mode: 3));
    Assert.AreEqual(new MeterArrival(4, 10, 2), _arrivals.Latest(_facts, _timeline, mode: 3));
  }

  [TestMethod]
  public void CancelledLongStallReadsCannotDiscardTheUnexaminedGap()
  {
    Add(0, LabelTypes.Melee);
    Add(200, LabelTypes.Melee);
    var read = _arrivals.BeginRead(_timeline);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    Assert.ThrowsExactly<OperationCanceledException>(() => read.Complete(_facts, 3, MeterPanels.Both, cancelled.Token));
    var retry = _arrivals.BeginRead(_timeline);
    Assert.HasCount(2, retry.Batches);
    Assert.AreEqual(new MeterArrival(2, 200, 1), retry.Complete(_facts, mode: 3));
  }

  /*
   * Which panels the operator displays decides which activity a meter may act on: a Damage-only overlay is deaf to being hit and a
   * Tanking-only one deaf to the raid's own damage, so a pull that only the hidden lane worked in neither opens the window nor keeps
   * it up. Both answers come from ONE verified run over the prefix — the choice is read, never rescanned — which is why widening it
   * shows history at once rather than warming up. docs/DesignNotes.md → "Which panels the meter shows decides what may wake it and
   * what is worth displaying".
   */
  [TestMethod]
  public void ADamageOnlyMeterIsDeafToBeingHit()
  {
    AddIncoming(100, LabelTypes.Melee);

    Assert.AreEqual(MeterArrival.None, _arrivals.Latest(_facts, _timeline, panels: MeterPanels.Damage),
      "a hit landing on us is not the damage panel's activity");
    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline, panels: MeterPanels.Tanking));
    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline, panels: MeterPanels.Both));
    Assert.IsFalse(DamageOverlayLifecycle.HasRecentActivity(_arrivals.Observed(MeterPanels.Damage), 105, 10),
      "so neither the opening question nor the keep-alive tick hears it either");
    Assert.IsTrue(DamageOverlayLifecycle.HasRecentActivity(_arrivals.Observed(MeterPanels.Tanking), 105, 10));
  }

  [TestMethod]
  public void ATankingOnlyMeterIsDeafToTheRaidsOwnDamage()
  {
    Add(100, LabelTypes.Melee);   // Illuminai hits a rat: outgoing only

    Assert.AreEqual(MeterArrival.None, _arrivals.Latest(_facts, _timeline, panels: MeterPanels.Tanking));
    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline, panels: MeterPanels.Damage));
    Assert.IsFalse(DamageOverlayLifecycle.HasRecentActivity(_arrivals.Observed(MeterPanels.Tanking), 105, 10));
  }

  // The exclusions ask what KIND of damage this is and never who hit whom, so the two lanes cannot share one answer.
  [TestMethod]
  public void DamageExclusionsStayOnTheDamageLaneAlone()
  {
    AddIncoming(100, LabelTypes.Bane);   // bane damage is excluded in Initialize

    Assert.AreEqual(MeterArrival.None, _arrivals.Latest(_facts, _timeline, panels: MeterPanels.Damage));
    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline, panels: MeterPanels.Tanking),
      "incoming stays exempt from the damage exclusions, exactly as it was one yes");
  }

  /*
   * Widening the choice is a read: the scan already recorded which panel each receipt earned, so no walk over history is needed
   * to remember the lane that kept arriving while the meter ignored it. A rescan-on-change would have made switching panels cost a
   * prefix walk and left the newly shown board empty until the next derive.
   */
  [TestMethod]
  public void OneVerifiedRunAnswersForBothChoicesWithoutARescan()
  {
    Add(100, LabelTypes.Melee);
    Assert.AreEqual(new MeterArrival(1, 100), _arrivals.Latest(_facts, _timeline, panels: MeterPanels.Damage));

    AddIncoming(104, LabelTypes.Melee);        // arriving while the meter was Damage-only
    Assert.AreEqual(new MeterArrival(2, 104), _arrivals.Latest(_facts, _timeline, panels: MeterPanels.Tanking),
      "the tanking lane answers for receipts it never displayed, from the same checked prefix");
    Assert.AreEqual(new MeterArrival(2, 104), _arrivals.Latest(_facts, _timeline, panels: MeterPanels.Both));
  }

  private void Add(double receipt, byte type)
  {
    _facts.AddFact(new DamageFact(_facts.FactCount, FixtureTime.BaseL, _facts.InternName("Illuminai"),
      _facts.InternName("a rat"), 600, type, 0, 0, DamageFactTable.NoSubtype));
    _arrivals.Record(_facts.FactCount, receipt);
  }

  // The same pair reversed: a rat on Illuminai is incoming, which no damage exclusion can silence.
  private void AddIncoming(double receipt, byte type)
  {
    _facts.AddFact(new DamageFact(_facts.FactCount, FixtureTime.BaseL, _facts.InternName("a rat"),
      _facts.InternName("Illuminai"), 600, type, 0, 0, DamageFactTable.NoSubtype));
    _arrivals.Record(_facts.FactCount, receipt);
  }
}
