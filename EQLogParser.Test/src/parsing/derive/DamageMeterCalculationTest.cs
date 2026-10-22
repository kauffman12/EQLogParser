namespace EQLogParser;

[TestClass]
public partial class DamageMeterCalculationTest
{
  private const double T0 = FixtureTime.Base;

  [TestInitialize]
  public void Initialize()
  {
    PlayerRegistry.Instance.Clear();
    RecordsStore.Instance.Clear();
    PipelineHarness.EnsureDataStore();
  }

  [TestMethod]
  public void WideAmountsStayExactAcrossIncrementalAndFreshMeterBuilds()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, TextUtils.MaxAmount);
    log.Add("a rat", "Bithika", 1, uint.MaxValue + 1L);
    log.Check(2, compareFresh: true);
    log.Add("Illuminai", "a rat", 3, uint.MaxValue);
    log.Add("a rat", "Bithika", 4, TextUtils.MaxAmount);
    log.Check(5, compareFresh: true);
    Assert.IsNotNull(log.Last);
    Assert.AreEqual(TextUtils.MaxAmount + uint.MaxValue, log.Last.DamageStats.RaidStats.Total);
    Assert.AreEqual(TextUtils.MaxAmount + uint.MaxValue + 1L, log.Last.TankStats.RaidStats.Total);
  }

  /*
   * SDPS divides by the RAID's clock. Bithika joins with six seconds left on a minute-long capture: her DPS is the rate she actually
   * played at and her SDPS is a tenth of it, and that gap IS the information the column exists to carry. Pinned because CalculateRates
   * takes its denominator as an argument — passing the row's own span there would read as a tidy refactor and quietly turn the meter's
   * SDPS into a second DPS column, with nothing else in the build disagreeing. Illuminai swung the whole capture, so his two rates must
   * be the same number: same denominator, same quotient.
   */
  [TestMethod]
  public void TheRaidTimeRateDividesByTheRaidsSpanNotTheNamesOwn()
  {
    var log = new Capture();
    for (var second = 0; second < 60; second++)
    {
      log.Add("Illuminai", "a rat", second, 100);
    }

    for (var second = 54; second < 60; second++)
    {
      log.Add("Bithika", "a rat", second, 100);
    }

    log.Check(60);

    var damage = log.Last!.DamageStats;
    var late = damage.StatsList.First(row => row.Name == "Bithika");
    var throughout = damage.StatsList.First(row => row.Name == "Illuminai");

    Assert.AreEqual(600, late.Total);
    Assert.AreEqual(100, late.Dps, "DPS divides by the six seconds she was swinging");
    Assert.AreEqual(StatsUtil.RatePerSecond(600, damage.RaidStats.TotalSeconds), late.Sdps,
      "SDPS divides by the raid's elapsed span");
    Assert.IsTrue(late.Sdps < late.Dps, "a name on screen for a tenth of the capture cannot rate the same in both columns");

    Assert.AreEqual(throughout.Dps, throughout.Sdps,
      "the raid's span and this name's own span are the same clock, so the two columns must agree");
  }

  [TestMethod]
  public void SameSecondArrivalsAndUnchangedPollsMatchTheFullCalculation()
  {
    var log = new Capture();
    for (var i = 0; i < 12; i++)
    {
      log.Add("Illuminai", "a rat", 0, 100);
      log.Add("a rat", "Bithika", 0, 10);
      log.Check(0);
    }
    log.Check(0.5);
    log.Check(1);
  }

  [TestMethod]
  public void TimedFarmingRetainsCompletedPullsAndTheirExactRanges()
  {
    var log = new Capture();
    for (var pull = 0; pull < 60; pull++)
    {
      var second = pull * 40;
      log.Add("Illuminai", "a rat", second, 100);
      log.Add("Bithika", "a rat", second + 3, 200);
      log.Add("a rat", "Illuminai", second + 4, 30);
      log.Facts.AddDeath(new DeathFact(pull, (long)(T0 + second + 5), log.Facts.InternName("a rat"), -1));
      log.Check(second + 6, mode: 90);
    }
    log.Check(2399, mode: 90);
  }

  [TestMethod]
  public void AllOutcomeWordsAndZeroTotalsKeepTheirOriginalDpsDenominators()
  {
    var log = new Capture();
    byte[] types = [LabelTypes.Absorb, LabelTypes.Block, LabelTypes.Dodge, LabelTypes.Miss,
      LabelTypes.Parry, LabelTypes.Riposte, LabelTypes.Invulnerable, LabelTypes.Melee, LabelTypes.Dd,
      LabelTypes.Dot, LabelTypes.Ds, LabelTypes.Rs, LabelTypes.Bane, LabelTypes.Proc];
    for (var i = 0; i < types.Length; i++)
    {
      log.Add("Illuminai", "a rat", i, i % 2 == 0 ? 0u : 100u, types[i]);
      log.Add("a rat", "Bithika", i, i % 2 == 0 ? 0u : 20u, types[i]);
      log.Check(i);
    }
  }

  [TestMethod]
  public void DamageFilterChangesRebuildOutgoingButDoNotFilterIncoming()
  {
    var previous = AppSettings.IsBaneDamageEnabled;
    try
    {
      var log = new Capture();
      log.Add("Illuminai", "a rat", 0, 100, LabelTypes.Bane);
      log.Add("a rat", "Illuminai", 1, 50, LabelTypes.Bane);
      AppSettings.IsBaneDamageEnabled = true;
      log.Check(2);
      AppSettings.IsBaneDamageEnabled = false;
      log.Check(2);
      AppSettings.IsBaneDamageEnabled = true;
      log.Check(2);
    }
    finally
    {
      AppSettings.IsBaneDamageEnabled = previous;
    }
  }

  [TestMethod]
  public void EqualAndZeroTotalsKeepTheBoardsTimestampBlockOrdering()
  {
    var log = new Capture();
    for (var i = 0; i < 40; i++)
    {
      var name = "Player" + i;
      log.Timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Manual, "manual");
      log.Add(name, i % 2 == 0 ? "a rat" : "a bat", i / 4, 0, LabelTypes.Miss);
      log.Add("a rat", name, i / 4, 100);
    }
    log.Check(10);
    for (var second = 11; second < 20; second++)
    {
      log.Add("Illuminai", "a rat", second, 100);
      log.Check(second);
    }
  }

  [TestMethod]
  public void FallbackOwnershipChangesRegroupTheSavedAmountsWithoutNewDamage()
  {
    var saved = IdentityLookup.LiveOwner;
    try
    {
      var owner = "Illuminai";
      IdentityLookup.LiveOwner = name => name == "Fluffy" ? owner : null;
      var log = new Capture();
      log.Timeline.SetIdentity("Fluffy", IdentityKind.Pet, RuleStrength.Manual, "manual");
      log.Add("Fluffy", "a rat", 0, 100);
      log.Add("Illuminai", "a rat", 1, 200);
      log.Check(2);
      owner = "Bithika";
      log.Check(2);
    }
    finally
    {
      IdentityLookup.LiveOwner = saved;
    }
  }

  [TestMethod]
  public void SpellAttackersAndPetIncomingDamageKeepTheProjectionRules()
  {
    var log = new Capture();
    log.Timeline.SetIdentity("Slicing Energy", IdentityKind.Spell, RuleStrength.Manual, "manual");
    log.Timeline.SetIdentity("Fluffy", IdentityKind.Pet, RuleStrength.Manual, "manual");
    log.Add("Slicing Energy", "a rat", 0, 100, LabelTypes.Dd, DamageFact.FlagAttackerIsSpell);
    log.Add("a rat", "Fluffy", 1, 500);
    log.Check(2);
    Assert.AreEqual(100L, log.Last!.DamageStats.RaidStats.Total);
    Assert.AreEqual(500L, log.Last.TankStats.RaidStats.Total);
    Assert.AreEqual("Fluffy", log.Last.TankStats.StatsList.Single().Name);
  }

  [TestMethod]
  public void RepeatedBlockResortsKeepPetAndPlainRowRanksEquivalent()
  {
    var log = new Capture();
    log.Timeline.SetIdentity("Illuminai`s pet", IdentityKind.Pet, RuleStrength.Manual, "manual");
    for (var i = 0; i < 12; i++)
    {
      var name = "Player" + i;
      log.Timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Manual, "manual");
      log.Add(name, i % 2 == 0 ? "a rat" : "a bat", i / 3, i % 3 == 0 ? 0u : 100u);
    }
    log.Add("Illuminai`s pet", "a rat", 0, 150, flags: DamageFact.FlagOwnerInLine);
    log.Check(5);
    for (var i = 6; i < 15; i++)
    {
      log.Add("Illuminai", "a bat", i, 200);
      log.Check(i);
    }
  }

  [TestMethod]
  public void PetOwnersKeepTheirOwnDamageAndUnionTheirChildActivity()
  {
    var log = new Capture();
    var pet = "Illuminai`s pet";
    log.Timeline.SetIdentity(pet, IdentityKind.Pet, RuleStrength.Manual, "manual");
    log.Add("Illuminai", "a rat", 0, 100);
    log.Add(pet, "a rat", 1, 200, flags: DamageFact.FlagOwnerInLine);
    log.Add("Bithika", "a bat", 3, 50);
    log.Check(3);
    log.Add(pet, "a bat", 7, 300, flags: DamageFact.FlagOwnerInLine);
    log.Add("Illuminai", "a bat", 9, 400);
    log.Check(10);
  }

  [TestMethod]
  public void OneCharmedAttackerCanContributeToTwoOwners()
  {
    var log = new Capture();
    log.Timeline.AddAffiliation(AffiliationKind.Friendly, "a hound", T0, T0 + 5, 9, "R9-charm", "Illuminai");
    log.Timeline.AddAffiliation(AffiliationKind.Friendly, "a hound", T0 + 6, T0 + 12, 9, "R9-charm", "Bithika");
    log.Add("a hound", "a rat", 1, 100);
    log.Check(2);
    log.Add("a hound", "a rat", 7, 200);
    log.Check(8);
  }

  [TestMethod]
  public void AHistoricalOwnerCorrectionInvalidatesPreviouslyCountedAmounts()
  {
    var log = new Capture();
    log.Timeline.AddAffiliation(AffiliationKind.Friendly, "a hound", T0, T0 + 10, 9, "R9-charm", "Illuminai");
    log.Add("a hound", "a rat", 1, 100);
    log.Check(2);
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "manual");
    timeline.SetIdentity("Bithika", IdentityKind.Player, RuleStrength.Manual, "manual");
    timeline.SetIdentity("a rat", IdentityKind.Npc, RuleStrength.Manual, "manual");
    timeline.AddAffiliation(AffiliationKind.Friendly, "a hound", T0, T0 + 10, 9, "R9-charm", "Bithika");
    log.Timeline = timeline;
    log.Check(2);
  }

  [TestMethod]
  public void AnUnrelatedClassificationChangeCanRebindExactOrdinalPrefixes()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 100);
    log.Check(1);
    log.Timeline.SetIdentity("Unrelated", IdentityKind.Player, RuleStrength.Manual, "manual");
    log.Check(1);
    log.Add("Illuminai", "a rat", 2, 300);
    log.Check(3);
  }

  [TestMethod]
  public void CapturedCountsDoNotReadFactsFromALaterProjection()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 100);
    var rows = log.Project();
    var input = log.Cache.Index.CaptureMeterInput(rows, log.Facts, double.NegativeInfinity, T0 + 5);
    log.Add("Illuminai", "a rat", 0, 900);
    log.Project();
    var result = log.Calculation.Update(input);
    Assert.AreEqual(100L, result.DamageStats.RaidStats.Total);
    log.Check(1);
    Assert.AreEqual(1000L, log.Last!.DamageStats.RaidStats.Total);
  }

  [TestMethod]
  public void AProjectedFutureFactBecomesEligibleWithoutAnotherDerive()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 100);
    log.Add("Illuminai", "a rat", 4, 900);
    log.Check(1);
    log.Check(4);
  }

  [TestMethod]
  public void ABackwardPresentationClockCannotRemoveCapturedDamage()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 100);
    log.Add("Illuminai", "a rat", 4, 900);
    log.Check(4, 90);
    log.Check(2, 90);
    Assert.AreEqual(1000L, log.Last!.DamageStats.RaidStats.Total);
    log.Check(4, 90);
    Assert.AreEqual(1000L, log.Last!.DamageStats.RaidStats.Total);
  }

  [TestMethod]
  public void OutOfOrderTimestampsAndOverlappingRangesMatchMaterializedActivity()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 100);
    log.Add("Illuminai", "a rat", 10, 100);
    log.Add("Illuminai", "a bat", 2, 100);
    log.Check(10);
    log.Add("Illuminai", "a rat", 4, 100);
    log.Check(10);
  }

  [TestMethod]
  public void AllCopyOptionsUseDetachedResultsIncludingSpecials()
  {
    var log = new Capture();
    RecordsStore.Instance.Add(new SpecialRecord { Player = "Illuminai", Code = "A" }, T0);
    RecordsStore.Instance.Add(new DeathRecord { Killed = "Illuminai" }, T0 + 3);
    log.Add("Illuminai", "a rat", 0, 100);
    log.Add("Bithika", "a rat", 1, 250);
    log.Add("a rat", "Illuminai", 2, 30);
    log.Check(3);
    var frozen = log.Last!;
    for (var bits = 0; bits < 256; bits++)
    {
      var options = new SummaryOptions
      {
        ListView = (bits & 1) != 0, ShowPetLabel = (bits & 2) != 0,
        ShowDps = (bits & 4) != 0, ShowTime = (bits & 8) != 0,
        ShowRaidTime = (bits & 16) != 0, ShowTotals = (bits & 32) != 0,
        RankPlayers = (bits & 64) != 0, ShowSpecial = (bits & 128) != 0
      };
      EqualCopy(Labels.DamageParse, log.Expected!.DamageStats, frozen.DamageStats, options);
      EqualCopy(Labels.TankParse, log.Expected.TankStats, frozen.TankStats, options);
    }
    log.Add("Illuminai", "a rat", 0, 900);
    log.Check(3);
    Assert.AreEqual(350L, frozen.DamageStats.RaidStats.Total, "publishing cannot mutate an already copied parse");
  }

  [TestMethod]
  public void APreCancelledUpdateAfterResetLeavesOnlyPostResetDamage()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 100);
    log.Check(1);
    log.Lifecycle.Reset(1, log.Facts.FactCount);
    log.Add("Illuminai", "a rat", 2, 900);
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    var input = log.Cache.Index.CaptureMeterInput(log.Project(), log.Facts, T0 + 1.5, T0 + 2);
    Assert.ThrowsExactly<OperationCanceledException>(() => log.Calculation.Update(input, cancel.Token));
    log.Check(3);
    Assert.AreEqual(900L, log.Last!.DamageStats.RaidStats.Total);
  }

  [TestMethod]
  public void OrdinalPrefixComparisonRefusesEqualLengthDifferentRouting()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 100);
    log.Add("Illuminai", "a rat", 1, 200);
    log.Add("Illuminai", "a bat", 2, 300);
    var oldLife = new DerivedFight { Name = "a rat" };
    var newLife = new DerivedFight { Name = "a rat" };
    var previous = new FightFactIndex(log.Timeline);
    var current = new FightFactIndex(log.Timeline);
    previous.OnFact(log.Facts.At(0), 0, oldLife, FightProjection.FactTarget.AtOwner);
    previous.OnFact(log.Facts.At(1), 1, oldLife, FightProjection.FactTarget.AtOwner);
    current.OnFact(log.Facts.At(0), 0, newLife, FightProjection.FactTarget.AtOwner);
    current.OnFact(log.Facts.At(2), 2, newLife, FightProjection.FactTarget.AtOwner);
    Assert.IsTrue(current.MeterPrefixMatches(previous, oldLife, newLife, false, 1));
    Assert.IsFalse(current.MeterPrefixMatches(previous, oldLife, newLife, false, 2));
  }

  private static void EqualCopy(string type, CombinedStats a, CombinedStats b, SummaryOptions options)
  {
    var expected = StatsFormatter.Build(type, a, a.StatsList, options, null);
    var actual = StatsFormatter.Build(type, b, b.StatsList, options, null);
    Assert.AreEqual(expected.Title, actual.Title);
    Assert.AreEqual(expected.RankedPlayers, actual.RankedPlayers);
  }

  private sealed class Capture
  {
    internal readonly DamageFactTable Facts = new(64);
    internal EntityTimeline Timeline = new();
    internal readonly FightProjection.FightProjectionCache Cache = new();
    internal readonly DamageMeterCalculation Calculation = new();
    internal readonly DamageOverlayLifecycle Lifecycle = new();
    internal readonly MeterArrivals Arrivals = new();
    private double _lastReceipt = double.NegativeInfinity;
    internal DamageOverlayStats? Last;
    internal DamageOverlayStats? Expected;
    // A cold calculation over the SAME input, kept when a test asks for it: what the incremental state must agree with.
    internal DamageOverlayStats? Fresh;

    internal Capture()
    {
      Timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "manual");
      Timeline.SetIdentity("Bithika", IdentityKind.Player, RuleStrength.Manual, "manual");
      Timeline.SetIdentity("a rat", IdentityKind.Npc, RuleStrength.Manual, "manual");
      Timeline.SetIdentity("a bat", IdentityKind.Npc, RuleStrength.Manual, "manual");
    }

    internal void Add(string attacker, string defender, int second, long amount,
      byte type = LabelTypes.Melee, byte flags = 0, double? receivedAt = null)
    {
      Facts.AddFact(new DamageFact(Facts.FactCount,
        (long)(T0 + second), Facts.InternName(attacker), Facts.InternName(defender), amount, type,
        flags, 0, ushort.MaxValue));
      _lastReceipt = Math.Max(_lastReceipt, T0 + (receivedAt ?? second));
      Arrivals.Record(Facts.FactCount, _lastReceipt);
    }

    internal IReadOnlyList<DerivedFight> Project()
    {
      ClassificationRules.Apply(Facts, Timeline);
      var rows = Cache.Project(Facts, Timeline);
      Sectionizer.StampGroupIds(rows);
      return rows;
    }

    internal void Check(double second, int mode = 0, bool compareFresh = false)
    {
      var rows = Project();
      DamageOverlayScope scope;
      do
      {
        var candidates = Lifecycle.SelectCandidates(1, rows, mode);
        Calculation.Activity.Update(Cache.Index.CaptureMeterInput(candidates.Rows, Facts, candidates.FromTime,
          double.PositiveInfinity, candidates.FirstOrdinal));
      } while (!Lifecycle.TrySelect(1, rows, T0 + second, mode, Arrivals.Latest(Facts, Timeline), Facts.FactCount,
        out scope, activity: Calculation.Activity.For));
      Expected = DerivedTotals.ForOverlay(scope.Rows, Cache.Index, Facts, scope.FromTime,
        double.PositiveInfinity, scope.FirstOrdinal);
      var input = Cache.Index.CaptureMeterInput(scope.Rows, Facts, scope.FromTime,
        double.PositiveInfinity, scope.FirstOrdinal);
      Last = Calculation.Update(input);
      DamageMeterAssert.Equal(Expected, Last);
      if (compareFresh)
      {
        Fresh = new DamageMeterCalculation().Update(input);
        DamageMeterAssert.Equal(Expected, Fresh);
      }
    }
  }
}
