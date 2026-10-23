namespace EQLogParser;

[TestClass]
public sealed class MeterRoutingTest
{
  private const long T0 = FixtureTime.BaseL;

  [TestInitialize]
  public void Initialize()
  {
    PipelineHarness.EnsureDataStore();
    PlayerRegistry.Instance.Clear();
  }

  [TestCleanup]
  public void Cleanup() => PlayerRegistry.Instance.Clear();

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void ReceiptsMatchProjectionAcrossIdentityCharmAndNameShapes(bool attackerIsSpell)
  {
    foreach (var attacker in new[] { "Attacker", "Attacking foe" })
      foreach (var defender in new[] { "Defender", "Defending foe" })
        foreach (var attackerKind in Enum.GetValues<IdentityKind>())
          foreach (var defenderKind in Enum.GetValues<IdentityKind>())
            for (var charms = 0; charms < 4; charms++)
              AssertReceiptMatchesProjection(attacker, defender, attackerKind, defenderKind, charms, attackerIsSpell);
  }

  [TestMethod]
  [DataRow((int)IdentityKind.Player)]
  [DataRow((int)IdentityKind.Pet)]
  [DataRow((int)IdentityKind.Merc)]
  public void UncreditedCharmCombatEarnsNeitherReceiptsNorPendingContinuity(int attackerKind)
  {
    var log = new Combat();
    log.Timeline.SetIdentity("Charmedraider", (IdentityKind)attackerKind, RuleStrength.Manual, "manual");
    log.Add("Charmedraider", "a rat", 0, 100);
    var rows = log.Project();
    var input = log.Projection.Index.CaptureMeterInput(rows, log.Facts,
      double.NegativeInfinity, double.PositiveInfinity);
    Assert.IsNull(log.Calculation.Update(input), "projection credits this exchange to neither board");

    var pending = new OverlayPendingActivity(0);
    pending.Update(log.Facts, log.Timeline);
    Assert.AreEqual(double.NegativeInfinity, pending.Latest);
    Assert.AreEqual(double.NegativeInfinity, pending.ContinuedThrough("a rat", T0));
    foreach (var panels in new[] { MeterPanels.Damage, MeterPanels.Tanking, MeterPanels.Both })
    {
      Assert.AreEqual(MeterArrival.None, log.Arrivals.Latest(log.Facts, log.Timeline, panels: panels));
      Assert.AreEqual(MeterArrival.None, log.Arrivals.Observed(panels));
      Assert.IsFalse(DamageOverlayLifecycle.HasRecentActivity(log.Arrivals.Observed(panels), 101, 0));
    }
  }

  [TestMethod]
  [DataRow(false, 0)]
  [DataRow(true, 0)]
  [DataRow(false, 10)]
  [DataRow(true, 10)]
  public void UncreditedCharmCombatCannotExtendOrReopenAnOldBoard(bool incoming, int mode)
  {
    var lane = incoming ? MeterPanels.Tanking : MeterPanels.Damage;
    foreach (var panels in new[] { lane, MeterPanels.Both })
    {
      var log = new Combat();
      log.Add(incoming ? "a boss" : "Raider", incoming ? "Raider" : "a boss", 0, 100);
      var lifecycle = new DamageOverlayLifecycle();
      var original = log.Select(lifecycle, 101, mode, panels);
      var deadline = 100 + (mode == 0 ? FightProjection.EngagementGapS : mode);
      Assert.HasCount(1, original.Rows);
      Assert.AreEqual(deadline, original.ExpiresAt);
      var stats = log.Calculation.Update(log.Projection.Index.CaptureMeterInput(original.Rows, log.Facts,
        original.FromTime, double.PositiveInfinity, original.FirstOrdinal));
      Assert.IsNotNull(stats);
      Assert.AreEqual(600L, (incoming ? stats.TankStats : stats.DamageStats)?.RaidStats.Total);

      log.Add("Charmedraider", "a rat", 5, 105);
      var retained = log.Select(lifecycle, 106, mode, panels);
      Assert.HasCount(1, retained.Rows);
      Assert.AreEqual(deadline, retained.ExpiresAt, "uncounted combat cannot renew the visible board's lease");

      log.Add("Charmedraider", "a rat", 35, 135);
      var expired = log.Select(lifecycle, 136, mode, panels);
      Assert.HasCount(0, expired.Rows);
      Assert.AreEqual(deadline, expired.ExpiresAt);
      Assert.IsFalse(DamageOverlayLifecycle.HasRecentActivity(log.Arrivals.Observed(panels), 136, mode),
        "the auto-open path must not hear a new receipt either");
      Assert.HasCount(0, log.Select(lifecycle.Copy(), 136, mode, panels).Rows);
      Assert.HasCount(0, log.Select(new DamageOverlayLifecycle(), 136, mode, panels).Rows,
        "reopening cannot revive the old amounts");

      log.Add(incoming ? "a boss" : "Raider", incoming ? "Raider" : "a boss", 39, 139);
      var resumed = log.Select(lifecycle, 140, mode, panels);
      Assert.HasCount(1, resumed.Rows, "real enabled-lane combat must still reopen the meter");
      var resumedStats = log.Calculation.Update(log.Projection.Index.CaptureMeterInput(resumed.Rows, log.Facts,
        resumed.FromTime, double.PositiveInfinity, resumed.FirstOrdinal));
      Assert.IsNotNull(resumedStats);
      Assert.AreEqual(600L, (incoming ? resumedStats.TankStats : resumedStats.DamageStats)?.RaidStats.Total);
    }
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void UncreditedCombatCannotBridgeATimedQuietGap(bool incoming)
  {
    var log = new Combat();
    var attacker = incoming ? "a boss" : "Raider";
    var defender = incoming ? "Raider" : "a boss";
    var lifecycle = new DamageOverlayLifecycle();
    log.Add(attacker, defender, 0, 100);
    Assert.HasCount(1, log.Select(lifecycle, 101, 3, MeterPanels.Both).Rows);
    foreach (var second in new[] { 2, 4, 6 }) log.Add("Charmedraider", "a rat", second, 100 + second);
    log.Add(attacker, defender, 8, 108);

    var scope = log.Select(lifecycle, 108, 3, MeterPanels.Both);
    Assert.AreEqual(4, scope.FirstOrdinal, "uncounted hits cannot bridge the original receipt's quiet gap");
    var stats = log.Calculation.Update(log.Projection.Index.CaptureMeterInput(scope.Rows, log.Facts,
      scope.FromTime, double.PositiveInfinity, scope.FirstOrdinal));
    Assert.IsNotNull(stats);
    Assert.AreEqual(600L, (incoming ? stats.TankStats : stats.DamageStats)?.RaidStats.Total);
  }

  private static void AssertReceiptMatchesProjection(string attacker, string defender, IdentityKind attackerKind,
    IdentityKind defenderKind, int charms, bool attackerIsSpell)
  {
    var facts = new DamageFactTable(16);
    var timeline = new EntityTimeline();
    // Explicit snapshot inputs, including Unknown, exercise the cheap lane without running another classifier.
    if (attackerKind != IdentityKind.Unknown)
      timeline.SetIdentity(attacker, attackerKind, RuleStrength.Manual, "manual");
    if (defenderKind != IdentityKind.Unknown)
      timeline.SetIdentity(defender, defenderKind, RuleStrength.Manual, "manual");
    if ((charms & 1) != 0) Charm(timeline, attacker);
    if ((charms & 2) != 0) Charm(timeline, defender);
    var flags = attackerIsSpell ? DamageFact.FlagAttackerIsSpell : (byte)0;
    var fact = new DamageFact(0, T0, facts.InternName(attacker), facts.InternName(defender), 600,
      LabelTypes.Melee, flags, LineModifiersParser.None, DamageFactTable.NoSubtype);
    facts.AddFact(fact);
    var projection = new FightProjection.FightProjectionCache();
    var rows = projection.Project(facts, timeline);
    var input = projection.Index.CaptureMeterInput(rows, facts, double.NegativeInfinity, double.PositiveInfinity);
    var stats = new DamageMeterCalculation().Update(input);
    var expected = (stats?.DamageStats?.StatsList.Count > 0 ? MeterPanels.Damage : MeterPanels.None)
      | (stats?.TankStats?.StatsList.Count > 0 ? MeterPanels.Tanking : MeterPanels.None);

    var actual = MeterActivityIndex.AcceptsPending(fact, timeline, attacker, defender, MeterDamageFilter.Current);
    Assert.AreEqual(expected, actual,
      $"{attacker} ({attackerKind}) -> {defender} ({defenderKind}), charms={charms}, spell={attackerIsSpell}");
  }

  private static void Charm(EntityTimeline timeline, string name) =>
    timeline.AddAffiliation(AffiliationKind.Friendly, name, T0 - 1, T0 + 100,
      RuleStrength.Manual, "R9-charm");

  private sealed class Combat
  {
    internal readonly DamageFactTable Facts = new(16);
    internal readonly EntityTimeline Timeline = new();
    internal readonly MeterArrivals Arrivals = new();
    internal readonly FightProjection.FightProjectionCache Projection = new();
    internal readonly DamageMeterCalculation Calculation = new();

    internal Combat()
    {
      Timeline.SetIdentity("Raider", IdentityKind.Player, RuleStrength.Manual, "manual");
      Timeline.SetIdentity("Charmedraider", IdentityKind.Player, RuleStrength.Manual, "manual");
      Timeline.SetIdentity("a boss", IdentityKind.Npc, RuleStrength.Manual, "manual");
      Timeline.SetIdentity("a rat", IdentityKind.Npc, RuleStrength.Manual, "manual");
      Charm(Timeline, "Charmedraider");
    }

    internal void Add(string attacker, string defender, int second, double receipt)
    {
      Facts.AddFact(new DamageFact(Facts.FactCount, T0 + second, Facts.InternName(attacker),
        Facts.InternName(defender), 600, LabelTypes.Melee, 0, LineModifiersParser.None, DamageFactTable.NoSubtype));
      Arrivals.Record(Facts.FactCount, receipt);
    }

    internal IReadOnlyList<DerivedFight> Project() => Projection.Project(Facts, Timeline);

    internal DamageOverlayScope Select(DamageOverlayLifecycle lifecycle, double now, int mode, MeterPanels panels)
    {
      var rows = Project();
      var arrival = Arrivals.Latest(Facts, Timeline, mode, panels);
      DamageOverlayScope scope;
      do
      {
        var candidates = lifecycle.SelectCandidates(1, rows, mode);
        Calculation.Activity.Update(Projection.Index.CaptureMeterInput(candidates.Rows, Facts,
          candidates.FromTime, double.PositiveInfinity, candidates.FirstOrdinal));
      } while (!lifecycle.TrySelect(1, rows, now, mode, arrival, Facts.FactCount,
        out scope, activity: Calculation.Activity.For, panels: panels));
      return scope;
    }
  }
}
