namespace EQLogParser;

/*
 * The Busy column at the three builder seams, on hand-built fights so every second in an assertion is one the fixture named.
 * What each lane is held to: marks come from the records THAT walk accepted (excluded damage buys nothing, a zero-amount attempt
 * buys its second), they sit on the row the walk already credits (attacker / healer / defender, and for a pet the owner-specific
 * row the amounts land on), an aggregate welds the UNION of its members' marks, and narrowing the question re-measures rather than
 * clipping the previous scalar.
 *
 * And Busy is cut from the very ranges Sec divides by, so it can never exceed Sec: `AGapBetweenTwoSelectedFightsCostsBusyAndSecTheSame`
 * pins both halves — a pause those ranges merge is active time for both columns, a pause they do not merge costs both. A weld on its own
 * would report activity on ground the capture never recorded, which is what the coverage cut exists to stop.
 */
[TestClass]
[DoNotParallelize]
public class BusyTimeTest
{
  private const double T0 = FixtureTime.Base;

  private Func<string, double, IdentityKind>? _savedLiveVerdict;
  private bool _ass, _bane, _ds, _fb, _hs, _su, _aoe, _swarm;

  [TestInitialize]
  public void Setup()
  {
    PipelineHarness.EnsureDataStore();
    DamageLineParser.ResetProcessState();
    RecordsStore.Instance.Clear(false);
    PlayerRegistry.Instance.Clear();
    HealRecordSource.Current = null;

    // Another test may have left a live verdict hook mounted; these boards only ever need the plain name answer.
    _savedLiveVerdict = IdentityLookup.LiveVerdict;
    IdentityLookup.LiveVerdict = null;

    _ass = AppSettings.IsAssassinateDamageEnabled;
    _bane = AppSettings.IsBaneDamageEnabled;
    _ds = AppSettings.IsDamageShieldDamageEnabled;
    _fb = AppSettings.IsFinishingBlowDamageEnabled;
    _hs = AppSettings.IsHeadshotDamageEnabled;
    _su = AppSettings.IsSlayUndeadDamageEnabled;
    _aoe = AppSettings.IsAoEHealingEnabled;
    _swarm = AppSettings.IsHealingSwarmPetsEnabled;
    AppSettings.IsAssassinateDamageEnabled = true;
    AppSettings.IsBaneDamageEnabled = true;
    AppSettings.IsDamageShieldDamageEnabled = true;
    AppSettings.IsFinishingBlowDamageEnabled = true;
    AppSettings.IsHeadshotDamageEnabled = true;
    AppSettings.IsSlayUndeadDamageEnabled = true;
    AppSettings.IsAoEHealingEnabled = true;
    AppSettings.IsHealingSwarmPetsEnabled = true;
  }

  [TestCleanup]
  public void Cleanup()
  {
    IdentityLookup.LiveVerdict = _savedLiveVerdict;
    AppSettings.IsAssassinateDamageEnabled = _ass;
    AppSettings.IsBaneDamageEnabled = _bane;
    AppSettings.IsDamageShieldDamageEnabled = _ds;
    AppSettings.IsFinishingBlowDamageEnabled = _fb;
    AppSettings.IsHeadshotDamageEnabled = _hs;
    AppSettings.IsSlayUndeadDamageEnabled = _su;
    AppSettings.IsAoEHealingEnabled = _aoe;
    AppSettings.IsHealingSwarmPetsEnabled = _swarm;
    DamageLineParser.ResetProcessState();
    RecordsStore.Instance.Clear(false);
    PlayerRegistry.Instance.Clear();
    HealRecordSource.Current = null;
  }

  // ---- damage ----------------------------------------------------------------------

  [TestMethod]
  public void ADamagersBusyCountsItsOwnAcceptedSeconds()
  {
    var fight = BuildFight(1, "Busyboss",
      (0, Hit("Busydane", "Busyboss")),
      (1, Hit("Busydane", "Busyboss")),
      (2, Hit("Busydane", "Busyboss")),
      (2, Hit("Busydane", "Busyboss")),   // a second visit to a marked second buys nothing
      (4, Hit("Busyother", "Busyboss")));

    var board = DamageBoard(Options(Range(T0, T0 + 4), fight));

    Assert.AreEqual(3d, Row(board, "Busydane").BusySeconds);
    Assert.AreEqual(1d, Row(board, "Busyother").BusySeconds);

    // The columns this feature must leave alone.
    Assert.AreEqual(400L, Row(board, "Busydane").Total);
    Assert.AreEqual(4u, Row(board, "Busydane").Hits);
    Assert.AreEqual(3d, Row(board, "Busydane").TotalSeconds);
    Assert.AreEqual(0d, board.RaidStats.BusySeconds, "the raid row is not an entity, so it earns nothing");
  }

  [TestMethod]
  public void AnAttemptThatDidNoDamageMarksItsSecondButExcludedDamageDoesNot()
  {
    /*
     * The second hand of the miss sits BEYOND the welding gap on purpose: at 0 and 5 both cases answer six seconds and the
     * assertion would prove nothing. Twelve apart, a counted miss answers two runs and an ignored one answers one.
     */
    var miss = Hit("Busydane", "Busyboss", 0);
    miss.Type = Labels.Miss;
    var board = DamageBoard(Options(Range(T0, T0 + 12),
      BuildFight(1, "Busyboss", (0, Hit("Busydane", "Busyboss")), (12, miss))));

    Assert.AreEqual(2d, Row(board, "Busydane").BusySeconds, "a swing that missed was still a swing taken");

    // Bane switched off: the same walk now refuses the bane line, so its second is not part of the answer.
    var baneful = Hit("Busydane", "Busyboss", 500);
    baneful.Type = Labels.Bane;
    AppSettings.IsBaneDamageEnabled = false;
    board = DamageBoard(Options(Range(T0, T0 + 12),
      BuildFight(1, "Busyboss", (0, Hit("Busydane", "Busyboss")), (12, baneful))));

    Assert.AreEqual(1d, Row(board, "Busydane").BusySeconds, "damage the walk refused buys no time");
    Assert.AreEqual(100L, Row(board, "Busydane").Total);
  }

  [TestMethod]
  public void TheOwnerRowAndThePetRowStayStrictWhileTheAggregateJoinsThem()
  {
    var fight = BuildFight(1, "Busyboss",
      (0, Hit("Busyowner", "Busyboss")),
      (1, Hit("Busyowner", "Busyboss")),
      (2, Hit("Busyowner", "Busyboss")),
      (1, Hit("Busypet", "Busyboss", owner: "Busyowner")),
      (2, Hit("Busypet", "Busyboss", owner: "Busyowner")),
      (3, Hit("Busypet", "Busyboss", owner: "Busyowner")));

    var board = DamageBoard(Options(Range(T0, T0 + 3), fight));

    Assert.AreEqual(4d, Row(board, "Busyowner +Pets").BusySeconds, "seconds both of them worked in are counted once");
    Assert.AreEqual(3d, Child(board, "Busyowner +Pets", "Busyowner").BusySeconds);
    Assert.AreEqual(3d, Child(board, "Busyowner +Pets", "Busypet").BusySeconds);
  }

  /*
   * The aggregate is the union of its children's marks cut inside their children's SECONDS: owner at 0 and pet at 10 are one busy second
   * each and two for the pair. Welding alone would have called that eleven — ten seconds of activity on ground neither child's ranges nor
   * Sec contain — which is exactly what the coverage cut forbids. The next test shows a bridge the row's own ranges DO cover.
   */
  [TestMethod]
  public void AnAggregateUnionsItsChildrenWithoutInventingSeconds()
  {
    var fight = BuildFight(1, "Busyboss",
      (0, Hit("Busyidle", "Busyboss")),
      (10, Hit("Busyspider", "Busyboss", owner: "Busyidle")));

    var board = DamageBoard(Options(Range(T0, T0 + 10), fight));
    var aggregate = Row(board, "Busyidle +Pets");

    Assert.AreEqual(1d, Child(board, "Busyidle +Pets", "Busyidle").BusySeconds);
    Assert.AreEqual(1d, Child(board, "Busyidle +Pets", "Busyspider").BusySeconds);
    Assert.AreEqual(2d, aggregate.BusySeconds, "two marked seconds with nothing either child covers between them");
    Assert.AreEqual(aggregate.TotalSeconds, aggregate.BusySeconds, "no idle second inside that coverage, so Busy is Sec");
  }

  /*
   * Three seconds apart instead of ten makes the pair one four second answer, because at that distance the row's own ranges merge and Sec
   * counts the seconds in between too. An aggregate may still beat the sum of its children — never its own coverage.
   */
  [TestMethod]
  public void AnAggregateBridgesOnlyAsFarAsItsOwnRangesBridge()
  {
    var fight = BuildFight(1, "Busyboss",
      (0, Hit("Busynear", "Busyboss")),
      (3, Hit("Busyspider", "Busyboss", owner: "Busynear")));

    var board = DamageBoard(Options(Range(T0, T0 + 3), fight));
    var aggregate = Row(board, "Busynear +Pets");

    Assert.AreEqual(1d, Child(board, "Busynear +Pets", "Busynear").BusySeconds);
    Assert.AreEqual(1d, Child(board, "Busynear +Pets", "Busyspider").BusySeconds);
    Assert.AreEqual(4d, aggregate.BusySeconds, "one range from 0 to 3 is four busy seconds for the pair, twice the sum of its children");
    Assert.AreEqual(aggregate.TotalSeconds, aggregate.BusySeconds);
  }

  [TestMethod]
  public void MarksFollowTheOwnerTheRecordCarriesNotWhoeverHoldsThePetNow()
  {
    var fight = BuildFight(1, "Busyboss",
      (0, Hit("Busycharmed", "Busyboss", owner: "Busycharama")),
      (1, Hit("Busycharmed", "Busyboss", owner: "Busycharama")),
      (20, Hit("Busycharmed", "Busyboss", owner: "Busycharmb")));

    var board = DamageBoard(Options(Range(T0, T0 + 20), fight));

    Assert.AreEqual(2d, Row(board, "Busycharama +Pets").BusySeconds);
    Assert.AreEqual(1d, Row(board, "Busycharmb +Pets").BusySeconds);
    Assert.AreEqual(200L, Row(board, "Busycharama +Pets").Total, "the marks sit with the row the amounts sit with");
    Assert.AreEqual(100L, Row(board, "Busycharmb +Pets").Total);
    Assert.AreEqual(2d, Child(board, "Busycharama +Pets", "Busycharmed").BusySeconds);
    Assert.AreEqual(1d, Child(board, "Busycharmb +Pets", "Busycharmed").BusySeconds);
  }

  /*
   * Both columns are cut from the same ranges: at six seconds or less a fight gap merges and Busy keeps every second joined (Sec counts
   * those as active too); past that the row has no data for the pause and neither column claims it. A ten second gap sits inside this
   * column's weld and outside TimeRange.Offset — the shape that used to invent seconds out of nothing.
   */
  [TestMethod]
  [DataRow(5, 6d)]
  [DataRow(6, 7d)]
  [DataRow(7, 2d)]
  [DataRow(10, 2d)]
  public void AGapBetweenTwoSelectedFightsCostsBusyAndSecTheSame(int gapSeconds, double expected)
  {
    var first = BuildFight(1, "Busyfirst", (0, Hit("Busydane", "Busyfirst")));
    var second = BuildFight(2, "Busysecond", (gapSeconds, Hit("Busydane", "Busysecond")));

    var row = Row(DamageBoard(FightsOptions(Range(T0, T0 + gapSeconds), first, second)), "Busydane");

    Assert.AreEqual(expected, row.TotalSeconds, $"a {gapSeconds} second gap between two one second fights");
    Assert.AreEqual(expected, row.BusySeconds, "Busy removes idle seconds from the span Sec counts and adds nothing to it");
  }

  // ---- healing ---------------------------------------------------------------------

  [TestMethod]
  public void HealingMarksTheHealerAndNotTheNameThatGotHealed()
  {
    var board = HealingBoard(Options(Range(T0, T0 + 1), heals: Heals(
      (0, Heal("Busyhealer", "Busytarget")),
      (1, Heal("Busyhealer", "Busytarget")),
      (1, Heal("Busyhealer", "Busyother")))));

    var healer = Row(board, "Busyhealer");
    Assert.AreEqual(2d, healer.BusySeconds);
    Assert.IsFalse(board.StatsList.Any(r => r.Name == "Busytarget"), "being healed opens no row, still less an answer");
    foreach (var healed in healer.SubStats2)
    {
      Assert.AreEqual(0d, healed.BusySeconds, $"the who-healed-me row ({healed.Name}) is detail, not an entity");
    }
  }

  [TestMethod]
  public void ReceivedHealingDetailIsNotStamped()
  {
    var builder = new HealingStatsBuilder();
    builder.BuildTotalStats(Options(Range(T0, T0 + 4), heals: Heals(
      (0, Heal("Busyhealer", "Busytarget")),
      (4, Heal("Busytarget", "Busyhealer")))));

    var board = LastBoard(builder);
    Assert.AreEqual(1d, Row(board, "Busyhealer").BusySeconds);
    Assert.AreEqual(1d, Row(board, "Busytarget").BusySeconds);

    builder.PopulateHealing(board);

    var target = Row(board, "Busytarget");
    Assert.IsNotNull(target.MoreStats, "the received-healing pass should have hung detail on the healed name");
    Assert.AreEqual(0d, target.MoreStats!.BusySeconds, "PopulateHealing is not this lane's activity collector");
    foreach (var detail in target.MoreStats!.SubStats.Concat(target.MoreStats.SubStats2))
    {
      Assert.AreEqual(0d, detail.BusySeconds, detail.Name);
    }
  }

  [TestMethod]
  public void AHealThatLandedNothingStillMarksItsSecondAndADroppedHealDoesNot()
  {
    var overhealed = Heal("Busyhealer", "Busytarget");
    overhealed.Total = 0;
    overhealed.OverTotal = 900;

    var board = HealingBoard(Options(Range(T0, T0 + 3), heals: Heals((3, overhealed))));
    Assert.AreEqual(1d, Row(board, "Busyhealer").BusySeconds, "the ask is a logged action even when the target was full");

    AppSettings.IsHealingSwarmPetsEnabled = false;
    board = HealingBoard(Options(Range(T0, T0 + 1), heals: Heals(
      (0, Heal("Busycleric", "Busyowner`s pet")),
      (1, Heal("Busycleric", "Busytarget")))));

    Assert.AreEqual(300L, Row(board, "Busycleric").Total, "the pet heal is out of this build by the existing filter");
    Assert.AreEqual(1d, Row(board, "Busycleric").BusySeconds, "and it buys no time either");
  }

  // ---- tanking ---------------------------------------------------------------------

  [TestMethod]
  public void IncomingAttemptsMarkTheDefenderWhoFacedThem()
  {
    var miss = Hit("Busyboss", "Busytank", 0);
    miss.Type = Labels.Miss;
    var fight = BuildTankFight(1, "Busyboss",
      (0, Hit("Busyboss", "Busytank")),
      (12, miss),
      (12, Hit("Busyboss", "Busyoffhand")));

    var board = TankBoard(Options(Range(T0, T0 + 12), fight));

    Assert.AreEqual(2d, Row(board, "Busytank").BusySeconds, "being swung at and missing is still being attacked");
    Assert.AreEqual(1d, Row(board, "Busyoffhand").BusySeconds);
  }

  [TestMethod]
  public void TheDamageTypeDialChangesTheMarksBecauseItChangesTheRecords()
  {
    var spell = Hit("Busyboss", "Busytank");
    spell.Type = Labels.Dd;
    spell.SubType = "Fireball";
    var fight = BuildTankFight(1, "Busyboss", (0, Hit("Busyboss", "Busytank")), (12, spell));
    var range = Range(T0, T0 + 12);

    Assert.AreEqual(2d, Row(TankBoard(Options(range, fight)), "Busytank").BusySeconds, "all damage");
    Assert.AreEqual(1d, Row(TankBoard(Options(range, fight, damageType: 1)), "Busytank").BusySeconds, "melee only");
    Assert.AreEqual(1d, Row(TankBoard(Options(range, fight, damageType: 2)), "Busytank").BusySeconds, "non-melee only");
  }

  // ---- lanes, windows and row kinds -------------------------------------------------

  [TestMethod]
  public void TheSameNameAnswersDifferentlyInEachLane()
  {
    var damage = DamageBoard(Options(Range(T0, T0 + 1),
      BuildFight(1, "Busyboss", (0, Hit("Busyall", "Busyboss")), (1, Hit("Busyall", "Busyboss")))));
    var tank = TankBoard(Options(Range(T0, T0 + 7),
      BuildTankFight(1, "Busyboss", (7, Hit("Busyboss", "Busyall")))));
    var healing = HealingBoard(Options(Range(T0, T0 + 4), heals: Heals(
      (0, Heal("Busyall", "Busytarget")),
      (4, Heal("Busyall", "Busytarget")))));

    Assert.AreEqual(2d, Row(damage, "Busyall").BusySeconds);
    Assert.AreEqual(1d, Row(tank, "Busyall").BusySeconds);
    Assert.AreEqual(5d, Row(healing, "Busyall").BusySeconds, "a four second cadence is one continuous run");
  }

  [TestMethod]
  public void NarrowingTheQuestionReMeasuresRatherThanClippingTheOldAnswer()
  {
    var range = Range(T0, T0 + 7);
    var hits = new[] { 0d, 3d, 7d };
    var damageFight = BuildFight(1, "Busyboss", hits.Select(s => (s, Hit("Busydane", "Busyboss"))).ToArray());
    var tankFight = BuildTankFight(1, "Busyboss", hits.Select(s => (s, Hit("Busyboss", "Busytank"))).ToArray());
    var heals = Heals(hits.Select(s => (s, Heal("Busyhealer", "Busytarget"))).ToArray());

    /*
     * Four seconds cut off the front leaves only the mark at seven. Keeping the last five keeps nought and three, which still weld
     * into one four second run — a narrowed question is re-measured from its own surviving marks, not clipped from the old scalar.
     */
    Assert.AreEqual(1d, Row(DamageBoard(Options(range, damageFight, minSeconds: 4)), "Busydane").BusySeconds);
    Assert.AreEqual(4d, Row(DamageBoard(Options(range, damageFight, maxSeconds: 5)), "Busydane").BusySeconds);
    Assert.AreEqual(1d, Row(TankBoard(Options(range, tankFight, minSeconds: 4)), "Busytank").BusySeconds);
    Assert.AreEqual(4d, Row(TankBoard(Options(range, tankFight, maxSeconds: 5)), "Busytank").BusySeconds);
    Assert.AreEqual(1d, Row(HealingBoard(Options(range, heals: heals, minSeconds: 4)), "Busyhealer").BusySeconds);
    Assert.AreEqual(4d, Row(HealingBoard(Options(range, heals: heals, maxSeconds: 5)), "Busyhealer").BusySeconds);

    // Widening rebuilds the whole answer instead of keeping the bridging credit of a mark the narrow question discarded.
    Assert.AreEqual(8d, Row(DamageBoard(Options(range, damageFight)), "Busydane").BusySeconds);
    Assert.AreEqual(8d, Row(TankBoard(Options(range, tankFight)), "Busytank").BusySeconds);
    Assert.AreEqual(8d, Row(HealingBoard(Options(range, heals: heals)), "Busyhealer").BusySeconds);
  }

  [TestMethod]
  public void OnlyEntityRowsCarryAnAnswerAndNoPublishedRowKeepsItsScratch()
  {
    var fight = BuildFight(1, "Busyboss",
      (0, Hit("Busydane", "Busyboss")),
      (2, SpellHit("Busydane", "Busyboss")),
      (4, Hit("Busypet", "Busyboss", owner: "Busydane")));

    var board = DamageBoard(Options(Range(T0, T0 + 4), fight));
    Assert.AreEqual(5d, Row(board, "Busydane +Pets").BusySeconds);
    Assert.AreEqual(1d, Child(board, "Busydane +Pets", "Busypet").BusySeconds);

    var published = board.StatsList.Concat(board.ExpandedStatsList).Concat(board.Children.Values.SelectMany(children => children));
    foreach (var row in published)
    {
      Assert.IsTrue(row.ActivityMarks is null, $"{row.Name} was published with its scratch attached");
      Assert.IsTrue(row.BusySeconds <= row.TotalSeconds, $"{row.Name}: Busy cannot cut {row.BusySeconds} from a Sec of {row.TotalSeconds}");
      foreach (var sub in row.SubStats.Concat(row.SubStats2))
      {
        Assert.AreEqual(0d, sub.BusySeconds, $"a breakdown row ({sub.Name}/{sub.Type}) has no role that measures busy");
      }
    }

    // The expanded children are rows too, and they answer for themselves: the owner's own two seconds weld into three.
    Assert.AreEqual(3d, board.ExpandedStatsList.First(r => r.Name == "Busydane").BusySeconds);
  }

  [TestMethod]
  public void AnEmptySelectionStillAnswersWithNoData()
  {
    var damage = new DamageStatsBuilder();
    damage.BuildTotalStats(Options(new TimeRange()));
    Assert.IsNull(damage.GetLastStats(), "no NPC selected is the existing empty path, not a board of blank rows");

    var healing = new HealingStatsBuilder();
    healing.BuildTotalStats(Options(new TimeRange(), heals: []));
    Assert.IsNull(healing.GetLastStats());
  }

  // ---- fixtures --------------------------------------------------------------------

  private static DamageRecord Hit(string attacker, string defender, long total = 100, string? owner = null) =>
    new() { Attacker = attacker, AttackerOwner = owner, Defender = defender, Total = total, Type = Labels.Melee, SubType = Labels.Melee };

  private static DamageRecord SpellHit(string attacker, string defender) =>
    new() { Attacker = attacker, Defender = defender, Total = 250, Type = Labels.Dd, SubType = "Flame Lick" };

  private static HealRecord Heal(string healer, string healed) =>
    new() { Healer = healer, Healed = healed, Total = 300, OverTotal = 300, Type = Labels.Heal, SubType = "Greater Renewal" };

  /// <summary>A fight whose damage blocks — and the per-name spans the Sec column reads — sit on the seconds named.</summary>
  private static Fight BuildFight(int id, string npc, params (double Sec, DamageRecord Record)[] hits)
  {
    var fight = new Fight
    {
      Id = id,
      Name = npc,
      BeginDamageTime = T0 + hits.Min(h => h.Sec),
      LastDamageTime = T0 + hits.Max(h => h.Sec),
    };

    foreach (var (sec, record) in hits)
    {
      AddBlock(fight.DamageBlocks, T0 + sec, record);
      AddSegment(fight.DamageSegments, record.Attacker, T0 + sec);
    }

    return fight;
  }

  private static Fight BuildTankFight(int id, string npc, params (double Sec, DamageRecord Record)[] hits)
  {
    var fight = new Fight
    {
      Id = id,
      Name = npc,
      BeginTankingTime = T0 + hits.Min(h => h.Sec),
      LastTankingTime = T0 + hits.Max(h => h.Sec),
    };

    foreach (var (sec, record) in hits)
    {
      AddBlock(fight.TankingBlocks, T0 + sec, record);
      AddSegment(fight.TankSegments, record.Defender, T0 + sec);
    }

    return fight;
  }

  private static void AddBlock(List<ActionGroup> blocks, double sec, DamageRecord record)
  {
    var block = blocks.FirstOrDefault(b => b.BeginTime == sec);
    if (block is null)
    {
      block = new ActionGroup { BeginTime = sec };
      blocks.Add(block);
    }

    block.Actions.Add(record);
  }

  private static void AddSegment(Dictionary<string, TimeSegment> segments, string name, double sec)
  {
    if (segments.TryGetValue(name, out var segment))
    {
      segment.BeginTime = Math.Min(segment.BeginTime, sec);
      segment.EndTime = Math.Max(segment.EndTime, sec);
    }
    else
    {
      segments[name] = new TimeSegment(sec, sec);
    }
  }

  private static List<(double, HealRecord)> Heals(params (double Sec, HealRecord Record)[] heals) =>
    [.. heals.Select(h => (T0 + h.Sec, h.Record))];

  private static TimeRange Range(double from, double to)
  {
    var range = new TimeRange();
    range.Add(new TimeSegment(from, to));
    return range;
  }

  /* Every call is a FRESH options object: the panes hand each build its own, and a shared one would let one question's dial leak into the next. */
  private static GenerateStatsOptions Options(TimeRange range, Fight? fight = null, List<(double, HealRecord)>? heals = null,
    long minSeconds = -1, long maxSeconds = -1, int damageType = 0)
  {
    var options = new GenerateStatsOptions
    {
      AllRanges = range,
      Heals = heals,
      Source = "busy time test",
      MinSeconds = minSeconds,
      MaxSeconds = maxSeconds,
      DamageType = damageType,
    };

    if (fight is not null)
    {
      options.Npcs.Add(fight);
    }

    return options;
  }

  /* The multi-fight question — Busy welding across two selected fights — needs more than the one-slot Options above. */
  private static GenerateStatsOptions FightsOptions(TimeRange range, params Fight[] fights)
  {
    var options = Options(range);
    options.Npcs.AddRange(fights);
    return options;
  }

  private static CombinedStats DamageBoard(GenerateStatsOptions options)
  {
    var builder = new DamageStatsBuilder();
    builder.BuildTotalStats(options);

    var stats = builder.GetLastStats()?.CombinedStats;
    Assert.IsNotNull(stats, "the damage builder produced no board");
    return stats!;
  }

  private static CombinedStats HealingBoard(GenerateStatsOptions options)
  {
    var builder = new HealingStatsBuilder();
    builder.BuildTotalStats(options);

    var stats = builder.GetLastStats()?.CombinedStats;
    Assert.IsNotNull(stats, "the healing builder produced no board");
    return stats!;
  }

  /* Tanking has no GetLastStats: its answer arrives on the completion event, and the handler leaves with the build. */
  private static CombinedStats TankBoard(GenerateStatsOptions options)
  {
    CombinedStats? result = null;
    var builder = new TankingStatsBuilder();
    void OnStatus(StatsGenerationEvent e) => result = e.CombinedStats ?? result;

    builder.EventsGenerationStatus += OnStatus;
    try
    {
      builder.BuildTotalStats(options);
    }
    finally
    {
      builder.EventsGenerationStatus -= OnStatus;
    }

    Assert.IsNotNull(result, "the tanking builder produced no board");
    return result!;
  }

  private static CombinedStats LastBoard(HealingStatsBuilder builder)
  {
    var stats = builder.GetLastStats()?.CombinedStats;
    Assert.IsNotNull(stats, "the healing builder produced no board");
    return stats!;
  }

  private static PlayerStats Row(CombinedStats stats, string name)
  {
    var row = stats.StatsList.FirstOrDefault(r => r.Name == name);
    Assert.IsNotNull(row, $"expected a {name} row; the board held [{string.Join(", ", stats.StatsList.Select(r => r.Name))}]");
    return row!;
  }

  private static PlayerStats Child(CombinedStats stats, string parent, string name)
  {
    Assert.IsTrue(stats.Children.TryGetValue(parent, out var children), $"expected children under {parent}");
    var row = children!.FirstOrDefault(r => r.Name == name);
    Assert.IsNotNull(row, $"expected a {name} child of {parent}; it held [{string.Join(", ", children.Select(r => r.Name))}]");
    return row!;
  }
}
