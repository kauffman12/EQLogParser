namespace EQLogParser;

[TestClass]
public sealed class MeterActivityIndexTest
{
  private const double T0 = FixtureTime.Base;
  private bool _bane;

  [TestInitialize]
  public void Initialize()
  {
    PipelineHarness.EnsureDataStore();
    PlayerRegistry.Instance.Clear();
    _bane = AppSettings.IsBaneDamageEnabled;
    AppSettings.IsBaneDamageEnabled = false;
  }

  [TestCleanup]
  public void Cleanup()
  {
    AppSettings.IsBaneDamageEnabled = _bane;
    PlayerRegistry.Instance.Clear();
  }

  [TestMethod]
  public void ExcludedOutgoingDamageCannotExtendATenSecondMeter()
  {
    var log = new Combat();
    var lifecycle = new DamageOverlayLifecycle();
    log.Add(0, 100);
    Assert.AreEqual(T0 + 10, log.Select(lifecycle, 0, 10).ExpiresAt);
    log.Add(9, 100, LabelTypes.Bane);
    Assert.AreEqual(0, log.Select(lifecycle, 11, 10).Rows.Count);
  }

  [TestMethod]
  public void IncomingDamageIsStillActivityEvenWhenThatOutgoingTypeIsExcluded()
  {
    var log = new Combat();
    log.Add(0, 100);
    log.Add(9, 100, LabelTypes.Bane, incoming: true);
    var scope = log.Select(new DamageOverlayLifecycle(), 11, 10);
    Assert.AreEqual(1, scope.Rows.Count);
    Assert.AreEqual(T0 + 19, scope.ExpiresAt);
  }

  [TestMethod]
  public void HealingCastingTauntsAndDeathsCannotRenewATimedMeter()
  {
    var log = new Combat();
    var lifecycle = new DamageOverlayLifecycle();
    log.Add(0, 100);
    Assert.AreEqual(1, log.Select(lifecycle, 0, 10).Rows.Count);
    var player = log.Facts.InternName("Illuminai");
    var mob = log.Facts.InternName("a rat");
    var heals = new HealFactTable(log.Facts);
    heals.AddHeal(new HealFact(1, (long)(T0 + 9), player, player, 100, 0,
      LabelTypes.Heal, 0, LineModifiersParser.None, HealFact.NoSpell));
    log.Facts.AddEvidence(new EvidenceFact(2, (long)(T0 + 9), player, EvidenceFact.EvCast,
      log.Facts.InternAux("Cure")));
    log.Facts.AddTaunt(new TauntFact(3, (long)(T0 + 9), mob, player, TauntFact.TauntSuccess));
    log.Facts.AddDeath(new DeathFact(4, (long)(T0 + 9), mob, -1));
    Assert.AreEqual(0, log.Select(lifecycle, 11, 10).Rows.Count);
  }

  [TestMethod]
  public void OnlyExcludedDamageSuppliesNoDisplayOrOnKillGrace()
  {
    var log = new Combat();
    log.Add(0, 100, LabelTypes.Bane);
    Assert.AreEqual(0, log.Select(new DamageOverlayLifecycle(), 1, 0).Rows.Count);
    Assert.AreEqual(0, log.Select(new DamageOverlayLifecycle(), 1, 10).Rows.Count);
  }

  [TestMethod]
  public void AvoidanceStillContinuesAnEncounterWithRealDamage()
  {
    var log = new Combat();
    var lifecycle = new DamageOverlayLifecycle();
    log.Add(0, 100);
    Assert.AreEqual(1, log.Select(lifecycle, 0, 10).Rows.Count);
    log.Add(9, 0, LabelTypes.Miss);
    var scope = log.Select(lifecycle, 11, 10);
    Assert.AreEqual(1, scope.Rows.Count);
    Assert.AreEqual(T0 + 19, scope.ExpiresAt);
  }

  [TestMethod]
  public void FilterChangesReevaluateTheSameProjectedAndPendingPrefixes()
  {
    var log = new Combat();
    log.Add(0, 100, LabelTypes.Bane);
    Assert.AreEqual(0, log.Select(new DamageOverlayLifecycle(), 1, 10).Rows.Count);
    var pending = new OverlayPendingActivity(0);
    pending.Update(log.Facts, log.Timeline);
    Assert.IsFalse(double.IsFinite(pending.Latest));
    AppSettings.IsBaneDamageEnabled = true;
    Assert.AreEqual(1, log.Select(new DamageOverlayLifecycle(), 1, 10).Rows.Count);
    pending.Update(log.Facts, log.Timeline);
    Assert.AreEqual(T0, pending.Latest);
  }

  [TestMethod]
  public void PendingExcludedDamageDoesNotBorrowTheClockButIncomingDoes()
  {
    var log = new Combat();
    log.Add(0, 100);
    var pending = new OverlayPendingActivity(log.Facts.FactCount);
    log.Add(9, 100, LabelTypes.Bane);
    pending.Update(log.Facts, log.Timeline);
    Assert.IsFalse(double.IsFinite(pending.Latest));
    log.Add(10, 100, LabelTypes.Bane, incoming: true);
    pending.Update(log.Facts, log.Timeline);
    Assert.AreEqual(T0 + 10, pending.Latest);
  }

  [TestMethod]
  public void OnKillUsesThirtySecondsSinceAcceptedActivityNotFilteredNoise()
  {
    var log = new Combat();
    var lifecycle = new DamageOverlayLifecycle();
    log.Add(0, 100);
    Assert.AreEqual(T0 + 30, log.Select(lifecycle, 0, 0).ExpiresAt);
    log.Add(20, 100, LabelTypes.Bane);
    log.Add(35, 100, LabelTypes.Bane);
    Assert.AreEqual(0, log.Select(lifecycle, 35, 0).Rows.Count);
  }

  [TestMethod]
  public void ResetCutsTheCapturedPrefixAndPresentationTimeCannotChangeItsActivity()
  {
    var log = new Combat();
    log.Add(0, 100);
    log.Add(9, 100);
    var lifecycle = new DamageOverlayLifecycle();
    Assert.AreEqual(T0 + 19, log.Select(lifecycle, 0, 10).ExpiresAt);
    Assert.AreEqual(T0 + 19, log.Select(lifecycle, 11, 10).ExpiresAt);
    Assert.AreEqual(T0 + 19, log.Select(lifecycle, 1, 10).ExpiresAt);
    lifecycle.Reset(1, log.Facts.FactCount);
    Assert.AreEqual(0, log.Select(lifecycle, 11, 10).Rows.Count);
  }

  [TestMethod]
  public void EqualCountsOnAReplacementIndexAreNotProofOfEqualActivity()
  {
    var facts = new DamageFactTable(16);
    var player = facts.InternName("Illuminai");
    var mob = facts.InternName("a rat");
    facts.AddFact(new DamageFact(0, (long)T0, player, mob, 100, LabelTypes.Melee, 0, -1, ushort.MaxValue));
    facts.AddFact(new DamageFact(1, (long)(T0 + 9), player, mob, 100, LabelTypes.Bane, 0, -1, ushort.MaxValue));
    facts.AddFact(new DamageFact(2, (long)(T0 + 8), player, mob, 100, LabelTypes.Melee, 0, -1, ushort.MaxValue));
    var oldLife = new DerivedFight { Name = "a rat", BeginTime = T0, LastTime = T0 + 9 };
    var newLife = new DerivedFight { Name = "a rat", BeginTime = T0, LastTime = T0 + 9 };
    var oldIndex = new FightFactIndex();
    oldIndex.OnFact(facts.At(0), 0, oldLife, FightProjection.FactTarget.AtOwner);
    oldIndex.OnFact(facts.At(1), 1, oldLife, FightProjection.FactTarget.AtOwner);
    var activity = new MeterActivityIndex();
    activity.Update(oldIndex.CaptureMeterInput([oldLife], facts, double.NegativeInfinity, T0 + 10));
    Assert.AreEqual(T0, activity.For(oldLife).Last);
    var newIndex = new FightFactIndex();
    newIndex.OnFact(facts.At(0), 0, newLife, FightProjection.FactTarget.AtOwner);
    newIndex.OnFact(facts.At(2), 2, newLife, FightProjection.FactTarget.AtOwner);
    activity.Update(newIndex.CaptureMeterInput([newLife], facts, double.NegativeInfinity, T0 + 10));
    Assert.AreEqual(T0 + 8, activity.For(newLife).Last);
    Assert.IsFalse(activity.For(oldLife).HasDamage);
  }

  [TestMethod]
  public void OnKillPublishesACompletedDeadlineFromTheLastDamageReceipt()
  {
    var log = new Combat();
    log.Add(0, 100);
    log.Facts.AddDeath(new DeathFact(1, (long)(T0 + 1), log.Facts.InternName("a rat"), -1));
    var lifecycle = new DamageOverlayLifecycle();
    Assert.AreEqual(T0 + 8, log.Select(lifecycle, 1, 0).ExpiresAt);
    Assert.AreEqual(T0 + 8, log.Select(lifecycle, 5, 0).ExpiresAt);
    Assert.AreEqual(0, log.Select(lifecycle, 9.1, 0).Rows.Count);
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void ExcludedActiveOrNewerCompletedLivesCannotHideTheLastAcceptedKill(bool killExcluded)
  {
    var log = new Combat();
    log.Timeline.SetIdentity("a bat", IdentityKind.Npc, RuleStrength.Manual, "R10-manual");
    log.Add(0, 100);
    log.Facts.AddDeath(new DeathFact(1, (long)(T0 + 1), log.Facts.InternName("a rat"), -1));
    log.Add(2, 100, LabelTypes.Bane, opponent: "a bat");
    if (killExcluded)
      log.Facts.AddDeath(new DeathFact(3, (long)(T0 + 3), log.Facts.InternName("a bat"), -1));
    var lifecycle = new DamageOverlayLifecycle();
    var scope = log.Select(lifecycle, 3, 0);
    Assert.HasCount(1, scope.Rows);
    Assert.AreEqual("A rat", scope.Rows[0].Name);
    Assert.AreEqual(T0 + 8, scope.ExpiresAt);
    Assert.HasCount(1, log.Select(lifecycle, 8, 0).Rows, "polling must retain the eligible completion");
    Assert.HasCount(0, log.Select(lifecycle, 9.1, 0).Rows);
  }

  [TestMethod]
  public void AcceptedActiveDamageStillTakesPrecedenceOverTheCompletedDisplay()
  {
    var log = new Combat();
    log.Timeline.SetIdentity("a bat", IdentityKind.Npc, RuleStrength.Manual, "R10-manual");
    log.Add(0, 100);
    log.Facts.AddDeath(new DeathFact(1, (long)(T0 + 1), log.Facts.InternName("a rat"), -1));
    log.Add(2, 100, opponent: "a bat");
    var scope = log.Select(new DamageOverlayLifecycle(), 3, 0);
    Assert.HasCount(1, scope.Rows);
    Assert.AreEqual("A bat", scope.Rows[0].Name);
    Assert.AreEqual(T0 + 32, scope.ExpiresAt);
  }

  [TestMethod]
  public void AlreadyCapturedActivityDoesNotWaitForThePresentationClock()
  {
    var log = new Combat();
    log.Add(0, 100);
    log.Add(9, 100);
    Assert.AreEqual(T0 + 19, log.Select(new DamageOverlayLifecycle(), 1, 10).ExpiresAt);
    Assert.AreEqual(T0 + 19, log.Select(new DamageOverlayLifecycle(), 9, 10).ExpiresAt);
  }

  private sealed class Combat
  {
    internal readonly DamageFactTable Facts = new(16);
    internal readonly EntityTimeline Timeline = new();
    private readonly FightProjection.FightProjectionCache _projection = new();
    private readonly MeterActivityIndex _activity = new();
    private readonly MeterArrivals _arrivals = new();
    private double _lastReceipt = double.NegativeInfinity;

    internal Combat()
    {
      Timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "R10-manual");
      Timeline.SetIdentity("a rat", IdentityKind.Npc, RuleStrength.Manual, "R10-manual");
    }

    internal void Add(int second, uint total, byte type = LabelTypes.Melee, bool incoming = false,
      string opponent = "a rat")
    {
      var attacker = Facts.InternName(incoming ? opponent : "Illuminai");
      var defender = Facts.InternName(incoming ? "Illuminai" : opponent);
      Facts.AddFact(new DamageFact(Facts.FactCount, (long)(T0 + second), attacker, defender,
        total, type, 0, LineModifiersParser.None, DamageFactTable.NoSubtype));
      _lastReceipt = Math.Max(_lastReceipt, T0 + second);
      _arrivals.Record(Facts.FactCount, _lastReceipt);
    }

    internal DamageOverlayScope Select(DamageOverlayLifecycle lifecycle, double second, int mode)
    {
      ClassificationRules.Apply(Facts, Timeline);
      var rows = _projection.Project(Facts, Timeline);
      var now = T0 + second;
      var candidates = lifecycle.SelectCandidates(1, rows, mode);
      _activity.Update(_projection.Index.CaptureMeterInput(candidates.Rows, Facts, candidates.FromTime,
        double.PositiveInfinity, candidates.FirstOrdinal));
      return lifecycle.Select(1, rows, now, mode, _arrivals.Latest(Facts, Timeline), Facts.FactCount,
        activity: _activity.For);
    }
  }
}
