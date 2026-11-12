#nullable enable annotations
namespace EQLogParser;

// Pin estimates to killed lives, with complete same-name pool context even under selection.
[TestClass]
public class NpcCombatStatsTest
{
  private const double T0 = FixtureTime.Base;

  private static DamageFactTable BuildFacts(params (string Atk, string Def, long Dmg, double T, byte Label)[] rows)
  {
    var facts = new DamageFactTable(64);
    var seq = 0;
    foreach (var (atk, def, dmg, t, label) in rows)
    {
      var a = facts.InternName(atk);
      var d = facts.InternName(def);
      facts.AddFact(new DamageFact(seq++, (long)(T0 + t), a, d, total: dmg, typeId: label,
        flags: 0, modMask: 0, subIdx: DamageFactTable.NoSubtype));
    }

    return facts;
  }

  private static void Kill(DamageFactTable facts, string npc, double t, int seq)
    => facts.AddDeath(new DeathFact(seq, (long)(T0 + t), facts.InternName(npc), -1));

  private static EntityTimeline Raid(params (string Name, IdentityKind Kind)[] people)
  {
    var timeline = new EntityTimeline();
    foreach (var (name, kind) in people)
    {
      timeline.SetIdentity(name, kind, RuleStrength.Certain, "R1-target");
    }

    timeline.SetIdentity("Raidman", IdentityKind.Player, RuleStrength.Certain, "R1-target");
    return timeline;
  }

  private static List<NpcCombatStatsRow> Stats(DamageFactTable facts, EntityTimeline timeline,
    HealFactTable? heals = null, NpcZoneTimeline? zones = null)
  {
    var index = new FightFactIndex(timeline);
    var rows = FightProjection.Build(facts, timeline, index.OnFact);
    return NpcCombatStats.Build(rows, facts, index, timeline, heals, zones);
  }

  [TestMethod]
  public void AKilledNpcPricesTheDamageItTook()
  {
    var facts = BuildFacts(
      ("Raidman", "a cave bear", 3000, 0, LabelTypes.Melee),
      ("Raidman", "a cave bear", 2000, 1, LabelTypes.Melee),
      ("a cave bear", "Raidman", 400, 1, LabelTypes.Melee));
    Kill(facts, "a cave bear", 1, 3);

    var row = Stats(facts, Raid()).Single();
    Assert.AreEqual(5000, row.EstHp, "the estimate is the damage the row RECEIVED");
    Assert.AreEqual(1, row.Deaths);
    Assert.AreEqual(400, row.MaxMelee);
    Assert.AreEqual(400, row.AvgMelee);
    Assert.AreEqual(NpcZoneTimeline.UnknownZone, row.Zone, "no zone line in the capture says nothing about the zone");
  }

  [TestMethod]
  public void TwoLivesInOneStreamPoolRatherThanSplitByArrivalOrder()
  {
    // Both instances are being hit before the first death line, so the projection files 1000 of the pool's 1100 in the
    // first row and 100 in the second. Reporting either row - or the mean of the two rows as two observations - would be
    // arithmetic on which swing happened to be read first.
    var facts = BuildFacts(
      ("Raidman", "a frenzied lynx", 700, 0, LabelTypes.Melee),
      ("Raidman", "a frenzied lynx", 300, 0, LabelTypes.Melee),
      ("Raidman", "a frenzied lynx", 100, 1, LabelTypes.Melee));
    Kill(facts, "a frenzied lynx", 0, 3);
    Kill(facts, "a frenzied lynx", 1, 4);

    var timeline = Raid();
    var index = new FightFactIndex(timeline);
    var rows = FightProjection.Build(facts, timeline, index.OnFact);
    Assert.AreEqual(2, rows.Count, "the slain line splits the name into two rows");
    Assert.AreEqual(1000, rows[0].DamageToOwner);
    Assert.AreEqual(100, rows[1].DamageToOwner);

    var row = NpcCombatStats.Build(rows, facts, index, timeline, null, null).Single();
    Assert.AreEqual(2, row.Deaths);
    Assert.AreEqual(550, row.EstHp, "the 1100 damage pool is shared by two killed lives");
  }

  [TestMethod]
  public void ANameSeenInTwoZoneVisitsIsOneRowWithBothDeaths()
  {
    var facts = BuildFacts(
      ("Raidman", "a tomb rat", 400, 10, LabelTypes.Melee),
      ("Raidman", "a tomb rat", 600, 510, LabelTypes.Melee));
    Kill(facts, "a tomb rat", 10, 2);
    Kill(facts, "a tomb rat", 510, 3);

    var zones = new NpcZoneTimeline(new[]
    {
      (T0 + 5, "Ruined Relic"),
      (T0 + 200, "Guild Lobby"),
      (T0 + 400, "Ruined Relic"),
    });

    var rows = Stats(facts, Raid(), zones: zones);
    var row = rows.Single();
    Assert.AreEqual("Ruined Relic", row.Zone);
    Assert.AreEqual(2, row.Deaths);
    Assert.AreEqual(600, row.EstHp, "an even split anchors on the higher visit's reading");
  }

  [TestMethod]
  public void KnuckleAcrossThreeVisitsHasOneZoneRowWithThreeDeaths()
  {
    var facts = BuildFacts(
      ("Raidman", "Knuckle", 3_555_782_170, 0, LabelTypes.Melee),
      ("Knuckle", "Raidman", 100, 0, LabelTypes.Melee),
      ("Raidman", "Knuckle", 3_556_074_685, 100, LabelTypes.Melee),
      ("Knuckle", "Raidman", 200, 100, LabelTypes.Melee),
      ("Knuckle", "Raidman", 300, 100, LabelTypes.Melee),
      ("Raidman", "Knuckle", 3_556_288_416, 200, LabelTypes.Melee),
      ("Knuckle", "Raidman", 900, 200, LabelTypes.Melee));
    Kill(facts, "Knuckle", 0, 7);
    Kill(facts, "Knuckle", 100, 8);
    Kill(facts, "Knuckle", 200, 9);
    var zones = new NpcZoneTimeline(new[]
    {
      (T0, "Plane of Guile: Cunning Gambit"),
      (T0 + 50, "Guild Lobby"),
      (T0 + 100, "Plane of Guile: Cunning Gambit"),
      (T0 + 150, "Guild Lobby"),
      (T0 + 200, "Plane of Guile: Cunning Gambit")
    });

    var row = Stats(facts, Raid(), zones: zones).Single();
    Assert.AreEqual("Knuckle", row.Npc);
    Assert.AreEqual("Plane of Guile: Cunning Gambit", row.Zone);
    Assert.AreEqual(3, row.Deaths);
    Assert.AreEqual(3_556_181_550, row.EstHp,
      "the median visit anchors the cluster and the mean of the upper half answers, not the mean of all three");
    Assert.AreEqual(375, row.AvgMelee, "combine hit totals/counts, not rounded visit averages");
    Assert.AreEqual(900, row.MaxMelee);
  }

  [TestMethod]
  public void VisitsKeepSeparatePoolsAndContributeReadingsToTheZonesCluster()
  {
    var facts = BuildFacts(
      ("Raidman", "a rat", 900, 0, LabelTypes.Melee),
      ("Raidman", "a rat", 800, 10, LabelTypes.Melee),
      ("Raidman", "a rat", 800, 11, LabelTypes.Melee),
      ("Raidman", "a rat", 800, 12, LabelTypes.Melee),
      ("Raidman", "a rat", 8000, 20, LabelTypes.Melee),
      ("a rat", "Raidman", 900, 20, LabelTypes.Melee),
      ("Raidman", "a rat", 700, 100, LabelTypes.Melee));
    Kill(facts, "a rat", 0, 7);
    Kill(facts, "a rat", 10, 8);
    Kill(facts, "a rat", 11, 9);
    Kill(facts, "a rat", 12, 10);
    Kill(facts, "a rat", 100, 11);
    var zones = new NpcZoneTimeline(new[]
    {
      (T0, "First Zone"), (T0 + 10, "First Zone"), (T0 + 20, "First Zone"), (T0 + 100, "Other Zone")
    });
    var timeline = Raid();
    var index = new FightFactIndex(timeline);
    var lives = FightProjection.Build(facts, timeline, index.OnFact);
    var rows = NpcCombatStats.Build(lives, facts, index, timeline, null, zones);

    Assert.AreEqual(2, rows.Count, "different zone names remain separate");
    var first = rows.Single(r => r.Zone == "First Zone");
    Assert.AreEqual(4, first.Deaths);
    Assert.AreEqual(825, first.EstHp,
      "the visits price 800 per life (three lives) and 900 (one), and an unfinished later visit cannot veto either");
    Assert.AreEqual(900, first.AvgMelee, "selected unfinished visits still contribute observed melee to the zone row");
    Assert.AreEqual(900, first.MaxMelee);
    Assert.AreEqual(700, rows.Single(r => r.Zone == "Other Zone").EstHp);

    var selected = lives.Where(life => life.BeginTime == T0 || life.BeginTime == T0 + 10).ToList();
    var slice = NpcCombatStats.Build(lives, facts, index, timeline, null, zones, selected).Single();
    Assert.AreEqual(2, slice.Deaths);
    Assert.AreEqual(900, slice.EstHp, "the two selected lives split evenly, so the higher reading anchors the estimate");
    Assert.AreEqual(0, slice.MaxMelee, "unselected lives contribute no melee");

    var pooledSlice = lives.Where(life => life.BeginTime == T0 + 10).ToList();
    Assert.AreEqual(800, NpcCombatStats.Build(lives, facts, index, timeline, null, zones, pooledSlice).Single().EstHp,
      "one selected life still gets the three-life visit's full pool price, not its own row's damage");
  }

  [TestMethod]
  public void AnUnpriceableVisitCountsItsDeathsButPricesNothing()
  {
    var facts = BuildFacts(
      ("Raidman", "a rat", 1, 0, LabelTypes.Melee),
      ("Raidman", "a rat", 2, 1, LabelTypes.Melee),
      ("Raidman", "a rat", 1, 100, LabelTypes.Melee),
      ("Raidman", "a rat", 2, 101, LabelTypes.Melee),
      ("Raidman", "a rat", 100, 200, LabelTypes.Melee),
      ("Raidman", "a rat", 8000, 201, LabelTypes.Melee));
    Kill(facts, "a rat", 0, 6);
    Kill(facts, "a rat", 1, 7);
    Kill(facts, "a rat", 100, 8);
    Kill(facts, "a rat", 101, 9);
    Kill(facts, "a rat", 200, 10);
    var zones = new NpcZoneTimeline(new[] { (T0, "Zone"), (T0 + 100, "Zone"), (T0 + 200, "Zone") });

    var row = Stats(facts, Raid(), zones: zones).Single();
    Assert.AreEqual(5, row.Deaths, "the visit that ends mid-pull still died twice before that");
    Assert.AreEqual(1, row.EstHp, "both priceable visits read 1.5 per life; the answer truncates once, at the end");
  }

  [TestMethod]
  public void ANameThatNeverDiedIsNotReported()
  {
    // A night of wipes is a true empty table, not an invented health number.
    var facts = BuildFacts(("Raidman", "the unkillable", 900, 0, LabelTypes.Melee));

    Assert.AreEqual(0, Stats(facts, Raid()).Count);
  }

  [TestMethod]
  public void ALifeThatNeverDiedPricesNothingButDoesNotHideTheKillsOrTheHits()
  {
    // One kill, then a second instance the log ends on. Its 8000 belongs partly to a creature still standing, so the
    // pool is not priceable; the kill count and the NPC's own output are still facts.
    var facts = BuildFacts(
      ("Raidman", "a wraith", 900, 0, LabelTypes.Melee),
      ("a wraith", "Raidman", 500, 0, LabelTypes.Melee),
      ("Raidman", "a wraith", 8000, 2, LabelTypes.Melee),
      ("a wraith", "Raidman", 900, 2, LabelTypes.Melee));
    Kill(facts, "a wraith", 0, 4);

    var row = Stats(facts, Raid()).Single();
    Assert.IsNull(row.EstHp, "an unpriceable pool is unknown, not a zero-HP creature");
    Assert.AreEqual(1, row.Deaths);
    Assert.AreEqual(700, row.AvgMelee, "a reported group keeps melee from its unfinished lives too");
    Assert.AreEqual(900, row.MaxMelee, "what the NPC dealt is measured whether or not it died");
  }

  [TestMethod]
  public void HealsLandingOnTheNameAreSubtractedToEstimateStartingHealth()
  {
    var facts = BuildFacts(
      ("Raidman", "Useless", 1000, 0, LabelTypes.Melee),
      ("Raidman", "Useless", 500, 2, LabelTypes.Melee));
    Kill(facts, "Useless", 2, 2);

    var heals = new HealFactTable(facts);
    var boss = facts.InternName("Useless");
    heals.AddHeal(new HealFact(3, (long)(T0 + 1), boss, boss, 250, 0, LabelTypes.Heal, 0, -1, HealFact.NoSpell));
    // Outside the life: healed before the fight opened, so it is not part of what this kill took.
    heals.AddHeal(new HealFact(4, (long)(T0 - 50), boss, boss, 5000, 0, LabelTypes.Heal, 0, -1, HealFact.NoSpell));

    var row = Stats(facts, Raid(), heals).Single();
    Assert.AreEqual(1250, row.EstHp, "1500 damage includes the 250 HP restored during the life");
  }

  [TestMethod]
  public void MeleeColumnsCountWhatOurSideReceivedAndNothingElse()
  {
    var facts = BuildFacts(
      ("Gorb", "Raidman", 500, 0, LabelTypes.Melee),
      ("Gorb", "Botman", 700, 1, LabelTypes.Melee),          // mercenary
      ("Gorb", "Raidman", 900, 2, LabelTypes.Dd));           // a spell, not a melee hit
    Kill(facts, "Gorb", 2, 4);

    var row = Stats(facts, Raid(("Botman", IdentityKind.Merc))).Single();
    Assert.AreEqual(1, row.Deaths);
    Assert.AreEqual(600, row.AvgMelee, "(500 + 700) over the two landed melee facts, spell damage excluded");
    Assert.AreEqual(700, row.MaxMelee);
  }

  [TestMethod]
  public void MeleeAveragesDoNotOverflowWhenTheSumExceedsLong()
  {
    const int hits = 32_769;
    var facts = new DamageFactTable(hits);
    var npc = facts.InternName("Gorb");
    var player = facts.InternName("Raidman");
    var timeline = Raid();
    var index = new FightFactIndex(timeline);
    var life = new DerivedFight
    {
      Name = "Gorb", BeginTime = T0, LastTime = T0, DeathTime = T0, Dead = true,
      EndReason = DerivedFightEnd.Slain, DamageToOwner = 1
    };
    for (var i = 0; i < hits; i++)
    {
      var fact = new DamageFact(i, (long)T0, npc, player, TextUtils.MaxAmount, LabelTypes.Melee,
        0, 0, DamageFactTable.NoSubtype);
      facts.AddFact(fact);
      index.OnFact(fact, i, life, FightProjection.FactTarget.RaidSide);
    }

    var row = NpcCombatStats.Build([life], facts, index, timeline, null, null).Single();
    Assert.AreEqual(TextUtils.MaxAmount, row.AvgMelee);
    Assert.AreEqual(TextUtils.MaxAmount, row.MaxMelee);
  }

  [TestMethod]
  public void EstimationRequiresTheTimelineThatExcludesRaidPeople()
  {
    var facts = BuildFacts(("Gorb", "Raidman", 500, 0, LabelTypes.Melee));
    var timeline = Raid();
    var index = new FightFactIndex(timeline);
    var rows = FightProjection.Build(facts, timeline, index.OnFact);
    Assert.Throws<ArgumentNullException>(() => NpcCombatStats.Capture(rows, facts, index, null, null, null));
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void HealingWindowsPreserveBoundariesAndQuietGapsInsidePools(bool unordered)
  {
    var facts = BuildFacts(
      ("Raidman", "a rat", 1000, 0, LabelTypes.Melee),
      ("Raidman", "a rat", 1000, 2, LabelTypes.Melee),
      ("Raidman", "a rat", 1000, 100, LabelTypes.Melee),
      ("Raidman", "a rat", 1000, 102, LabelTypes.Melee));
    Kill(facts, "a rat", 0, 4);
    Kill(facts, "a rat", 2, 5);
    Kill(facts, "a rat", 100, 6);
    Kill(facts, "a rat", 102, 7);
    var heals = new HealFactTable(facts);
    var rat = facts.InternName("a rat");
    var amounts = new (int Time, int Amount)[]
    {
      (-1, 9000), (0, 100), (1, 200), (2, 300), (3, 9000), (50, 9000),
      (100, 50), (101, 100), (102, 150), (103, 9000)
    };
    if (unordered) Array.Reverse(amounts);
    foreach (var (time, amount) in amounts)
    {
      heals.AddHeal(new HealFact(heals.HealCount + 8, (long)(T0 + time), rat, rat, amount,
        0, LabelTypes.Heal, 0, -1, HealFact.NoSpell));
    }
    Assert.AreEqual(!unordered, heals.AscendingTime);
    var timeline = Raid();
    var index = new FightFactIndex(timeline);
    var rows = FightProjection.Build(facts, timeline, index.OnFact);

    Assert.AreEqual(850, NpcCombatStats.Build(rows, facts, index, timeline, heals, null).Single().EstHp,
      "the pools price 700 and 850 per life; the upper median keeps the higher reading");
    var first = rows.Where(r => r.BeginTime < T0 + 100).ToList();
    Assert.AreEqual(700, NpcCombatStats.Build(rows, facts, index, timeline, heals, null, first).Single().EstHp,
      "heals at both pool endpoints and between pooled lives count, heals between pools do not");
    var second = rows.Where(r => r.BeginTime >= T0 + 100).ToList();
    Assert.AreEqual(850, NpcCombatStats.Build(rows, facts, index, timeline, heals, null, second).Single().EstHp,
      "narrowing the heal walk may not change a selected pool's estimate");
  }

  [TestMethod]
  public void ACharmedRaiderIsAPersonAndGetsNoRow()
  {
    var facts = BuildFacts(
      ("Illuminai", "Raidman", 900, 0, LabelTypes.Melee),
      ("Raidman", "Illuminai", 100, 1, LabelTypes.Melee));
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Raidman", IdentityKind.Player, RuleStrength.Certain, "R1-target");
    timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Certain, "R1-target");
    timeline.AddAffiliation(AffiliationKind.Friendly, "Illuminai", T0, T0 + 100, RuleStrength.Certain, "R9-charm", "Raidman");

    Assert.AreEqual(0, Stats(facts, timeline).Count, "the fight list keeps her row; this table counts NPCs");
  }

  [TestMethod]
  public void TheClusterAnchorFollowsKilledLivesNotPools()
  {
    var facts = BuildFacts(
      ("Raidman", "a rat", 900, 0, LabelTypes.Melee),
      ("Raidman", "a rat", 1000, 100, LabelTypes.Melee),
      ("Raidman", "a rat", 1000, 101, LabelTypes.Melee),
      ("Raidman", "a rat", 1000, 102, LabelTypes.Melee));
    Kill(facts, "a rat", 0, 4);
    Kill(facts, "a rat", 100, 5);
    Kill(facts, "a rat", 101, 6);
    Kill(facts, "a rat", 102, 7);

    var row = Stats(facts, Raid()).Single();
    Assert.AreEqual(4, row.Deaths);
    Assert.AreEqual(1000, row.EstHp,
      "three killed lives sharing 3000 read 1000 each and outvote one life at 900; averaging pools would answer 950");
  }

  [TestMethod]
  public void PoolFractionsSurviveUntilTheFinalAverage()
  {
    var facts = BuildFacts(
      ("Raidman", "a rat", 1000, 0, LabelTypes.Melee),
      ("Raidman", "a rat", 1000, 1, LabelTypes.Melee),
      ("Raidman", "a rat", 1002, 2, LabelTypes.Melee),
      ("Raidman", "a rat", 1000, 100, LabelTypes.Melee),
      ("Raidman", "a rat", 1004, 101, LabelTypes.Melee));
    Kill(facts, "a rat", 0, 5);
    Kill(facts, "a rat", 1, 6);
    Kill(facts, "a rat", 2, 7);
    Kill(facts, "a rat", 100, 8);
    Kill(facts, "a rat", 101, 9);
    var zones = new NpcZoneTimeline(new[] { (T0, "Zone"), (T0 + 100, "Zone") });

    Assert.AreEqual(1001, Stats(facts, Raid(), zones: zones).Single().EstHp,
      "3002 shared by three lives and 2004 by two average to 1001.2; truncating a pool first would answer 1000");
  }

  [TestMethod]
  [DataRow(1, false)]
  [DataRow(1, true)]
  [DataRow(2, false)]
  [DataRow(2, true)]
  public void AnEvenSplitDoesNotRejectTheHigherReadingAsOverkill(int livesPerPool, bool lowFirst)
  {
    var damage = new List<(string Atk, string Def, long Dmg, double T, byte Label)>();
    for (var pool = 0; pool < 2; pool++)
    {
      var amount = (pool == 0) == lowFirst ? 6_000_000 : 22_000_000;
      for (var i = 0; i < livesPerPool; i++)
      {
        damage.Add(("Raidman", "a rat", amount, pool * 100 + i, LabelTypes.Melee));
      }
    }

    var facts = BuildFacts(damage.ToArray());
    var seq = damage.Count;
    foreach (var row in damage) Kill(facts, "a rat", row.T, seq++);
    var timeline = Raid();
    var index = new FightFactIndex(timeline);
    var lives = FightProjection.Build(facts, timeline, index.OnFact);
    var higher = lives.Where(life => life.DamageToOwner == 22_000_000).ToList();

    Assert.AreEqual(22_000_000,
      NpcCombatStats.Build(lives, facts, index, timeline, null, null, higher).Single().EstHp);
    var all = NpcCombatStats.Build(lives, facts, index, timeline, null, null).Single();
    Assert.AreEqual(livesPerPool * 2, all.Deaths);
    Assert.AreEqual(22_000_000, all.EstHp,
      "including equally many low readings must not replace 22M with 6M by calling the higher half overkill");
  }

  [TestMethod]
  public void LowReadingsDoNotDragTheEstimateDown()
  {
    // Rounded per-life readings from eight priceable pools of the Incogitable capture's jade criminal.
    // Low damage alone does not prove a wounded pull; keep those readings without letting them set the estimate.
    var visits = new (int Lives, long PerLife)[]
    {
      (6, 19_749_310), (1, 22_275_910), (2, 21_810_961), (3, 18_656_029),
      (6, 20_982_076), (6, 21_617_870), (1, 8_333_982), (1, 6_365_428),
    };

    var facts = new DamageFactTable(64);
    var seq = 0;
    var zoneStarts = new List<(double Time, string Zone)>();
    for (var v = 0; v < visits.Length; v++)
    {
      var (lives, perLife) = visits[v];
      var begin = v * 100;                     // seconds from T0; Kill() adds T0 itself
      zoneStarts.Add((T0 + begin, "Lair"));
      for (var i = 0; i < lives; i++)
      {
        facts.AddFact(new DamageFact(seq, (long)(T0 + begin + i), facts.InternName("Raidman"),
          facts.InternName("a jade criminal"), perLife, LabelTypes.Melee, 0, 0, DamageFactTable.NoSubtype));
        seq++;
        Kill(facts, "a jade criminal", begin + i, seq);
        seq++;
      }
    }

    var row = Stats(facts, Raid(), zones: new NpcZoneTimeline(zoneStarts)).Single();
    Assert.AreEqual(26, row.Deaths);
    Assert.AreEqual(21_433_167, row.EstHp,
      "the upper readings answer; the 6.4M and 8.3M readings are excluded without guessing their cause");
  }

  [TestMethod]
  public void AMajoritySupportedCapExcludesAnUnusuallyHighReading()
  {
    var facts = BuildFacts(
      ("Raidman", "a rat", 1000, 0, LabelTypes.Melee),
      ("Raidman", "a rat", 1000, 1, LabelTypes.Melee),
      ("Raidman", "a rat", 5000, 100, LabelTypes.Melee));
    Kill(facts, "a rat", 0, 3);
    Kill(facts, "a rat", 1, 4);
    Kill(facts, "a rat", 100, 5);
    var zones = new NpcZoneTimeline(new[] { (T0, "Zone"), (T0 + 100, "Zone") });

    Assert.AreEqual(1000, Stats(facts, Raid(), zones: zones).Single().EstHp,
      "5000 is above 1.5x the majority-supported median and is excluded without identifying the cause");
  }

  [TestMethod]
  public void ASelectedLifeKeepsItsWholePoolsEstimateButOnlyItsOwnCountAndMelee()
  {
    var facts = BuildFacts(
      ("Raidman", "a rat", 1000, 0, LabelTypes.Melee),
      ("a rat", "Raidman", 700, 0, LabelTypes.Melee),
      ("Raidman", "a rat", 100, 1, LabelTypes.Melee),
      ("a rat", "Raidman", 300, 1, LabelTypes.Melee));
    Kill(facts, "a rat", 0, 4);
    Kill(facts, "a rat", 1, 5);
    var timeline = Raid();
    var index = new FightFactIndex(timeline);
    var lives = FightProjection.Build(facts, timeline, index.OnFact);
    Assert.AreEqual(2, lives.Count);

    for (var i = 0; i < lives.Count; i++)
    {
      var row = NpcCombatStats.Build(lives, facts, index, timeline, null, null, [lives[i]]).Single();
      Assert.AreEqual(550, row.EstHp, "neither selected slice is an independently identifiable creature");
      Assert.AreEqual(1, row.Deaths);
      Assert.AreEqual(i == 0 ? 700 : 300, row.AvgMelee);
      Assert.AreEqual(row.AvgMelee, row.MaxMelee);
    }
    Assert.AreEqual(0, NpcCombatStats.Build(lives, facts, index, timeline, null, null, []).Count);
  }

  [TestMethod]
  public void ASelectedUnfinishedLifeDoesNotBorrowAnUnselectedKill()
  {
    var facts = BuildFacts(
      ("Raidman", "a rat", 900, 0, LabelTypes.Melee),
      ("Raidman", "a rat", 8000, 1, LabelTypes.Melee),
      ("a rat", "Raidman", 500, 1, LabelTypes.Melee));
    Kill(facts, "a rat", 0, 3);
    var timeline = Raid();
    var index = new FightFactIndex(timeline);
    var lives = FightProjection.Build(facts, timeline, index.OnFact);
    var unfinished = lives.Single(life => !life.Dead);

    var rows = NpcCombatStats.Build(lives, facts, index, timeline, null, null, [unfinished]);
    Assert.AreEqual(0, rows.Count, "a group needs a selected kill before any of its melee belongs in the table");
  }

  [TestMethod]
  public void AnUnselectedUnfinishedLifeStillMakesThePoolUnpriceable()
  {
    var facts = BuildFacts(
      ("Raidman", "a rat", 900, 0, LabelTypes.Melee),
      ("Raidman", "a rat", 8000, 1, LabelTypes.Melee));
    Kill(facts, "a rat", 0, 2);
    var timeline = Raid();
    var index = new FightFactIndex(timeline);
    var lives = FightProjection.Build(facts, timeline, index.OnFact);

    var row = NpcCombatStats.Build(lives, facts, index, timeline, null, null, [lives[0]]).Single();
    Assert.AreEqual(1, row.Deaths);
    Assert.IsNull(row.EstHp);
  }

  [TestMethod]
  public void UnpriceableRunsDoNotDiluteOtherKilledLives()
  {
    var facts = BuildFacts(
      ("Raidman", "a rat", 500, 0, LabelTypes.Melee),
      ("Raidman", "a rat", 900, 100, LabelTypes.Melee),
      ("Raidman", "a rat", 8000, 101, LabelTypes.Melee));
    Kill(facts, "a rat", 0, 3);
    Kill(facts, "a rat", 100, 4);

    var row = Stats(facts, Raid()).Single();
    Assert.AreEqual(2, row.Deaths);
    Assert.AreEqual(500, row.EstHp, "the second pool has no estimate, not a zero to average into the first");
  }

  [TestMethod]
  public void HealingAtLeastAsLargeAsCapturedDamageMakesHpUnknown()
  {
    var facts = BuildFacts(("Raidman", "Useless", 100, 0, LabelTypes.Melee));
    Kill(facts, "Useless", 0, 1);
    var heals = new HealFactTable(facts);
    var boss = facts.InternName("Useless");
    heals.AddHeal(new HealFact(2, (long)T0, boss, boss, 200, 0, LabelTypes.Heal, 0, -1, HealFact.NoSpell));

    Assert.IsNull(Stats(facts, Raid(), heals).Single().EstHp, "do not print a negative or fake zero starting HP");
  }

  [TestMethod]
  public void AnUnnamedZoneFromTheRealParserKeepsItsBoundaryButNotItsSentenceAsAZoneName()
  {
    RecordsStore.Instance.Clear();
    try
    {
      const string action = "You have entered an area not on the map.";
      CastLineParser.Process(new LineData { Action = action, BeginTime = T0, Split = action.Split(' ') });
      var records = RecordsStore.Instance.GetAllZoning().ToArray();
      Assert.AreEqual(1, records.Length);
      var zones = new NpcZoneTimeline(records.Select(z => (z.Item1, z.Item2.Zone)));
      Assert.AreEqual(NpcZoneTimeline.UnknownVisit, zones.VisitAt(T0 - 1));
      Assert.AreNotEqual(NpcZoneTimeline.UnknownVisit, zones.VisitAt(T0));
      Assert.AreEqual(NpcZoneTimeline.UnknownZone, zones.ZoneAt(T0 + 1));
    }
    finally
    {
      RecordsStore.Instance.Clear();
    }
  }

  [TestMethod]
  public void ADetachedQuestionCannotAcquireADeathFromTheNextPassWithoutItsNewPoolMember()
  {
    var facts = BuildFacts(("Raidman", "a rat", 1000, 0, LabelTypes.Melee));
    var timeline = Raid();
    var cache = new FightProjection.FightProjectionCache();
    var first = cache.Project(facts, timeline);
    var input = NpcCombatStats.Capture(first, facts, cache.Index, timeline, null, null);

    Kill(facts, "a rat", 0, 1);
    facts.AddFact(new DamageFact(2, (long)(T0 + 1), facts.InternName("Raidman"), facts.InternName("a rat"),
      100, LabelTypes.Melee, 0, 0, DamageFactTable.NoSubtype));
    Kill(facts, "a rat", 1, 3);
    var next = cache.Project(facts, timeline);

    Assert.AreSame(first[0], next[0], "a carried projection mutates the original life, not its detached metadata");
    Assert.AreEqual(2, next.Count);
    Assert.IsTrue(first[0].Dead);
    Assert.HasCount(0, NpcCombatStats.Build(input), "the old question saw no killed life");
    Assert.AreEqual(550, NpcCombatStats.Build(next, facts, cache.Index, timeline, null, null).Single().EstHp);
  }

  [TestMethod]
  public void ADetachedUnfinishedPoolMemberStillVetoesPricingAfterItDies()
  {
    var facts = BuildFacts(
      ("Raidman", "a rat", 900, 0, LabelTypes.Melee),
      ("Raidman", "a rat", 100, 1, LabelTypes.Melee));
    Kill(facts, "a rat", 0, 2);
    var timeline = Raid();
    var cache = new FightProjection.FightProjectionCache();
    var first = cache.Project(facts, timeline);
    var selected = first.ToList();
    var input = NpcCombatStats.Capture(first, facts, cache.Index, timeline, null, null, selected);
    selected.Clear();

    Kill(facts, "a rat", 1, 3);
    cache.Project(facts, timeline);

    var row = NpcCombatStats.Build(input).Single();
    Assert.AreEqual(1, row.Deaths, "selection and deaths belong to the frozen question");
    Assert.IsNull(row.EstHp, "the later death cannot turn the old unfinished pool into a priceable one");
  }

  [TestMethod]
  public void ADetachedQuestionKeepsItsMeleeAndHealingPrefixes()
  {
    var facts = BuildFacts(
      ("Raidman", "a rat", 1000, 0, LabelTypes.Melee),
      ("a rat", "Raidman", 400, 0, LabelTypes.Melee),
      ("Raidman", "a rat", 100, 1, LabelTypes.Melee),
      ("a rat", "Raidman", 600, 1, LabelTypes.Melee));
    Kill(facts, "a rat", 0, 4);
    var heals = new HealFactTable(facts);
    var rat = facts.InternName("a rat");
    heals.AddHeal(new HealFact(5, (long)T0, rat, rat, 100, 0, LabelTypes.Heal, 0, -1, HealFact.NoSpell));
    var timeline = Raid();
    var cache = new FightProjection.FightProjectionCache();
    var first = cache.Project(facts, timeline);
    var input = NpcCombatStats.Capture(first, facts, cache.Index, timeline, heals, null);

    facts.AddFact(new DamageFact(6, (long)(T0 + 2), rat, facts.InternName("Raidman"),
      900, LabelTypes.Melee, 0, 0, DamageFactTable.NoSubtype));
    cache.Project(facts, timeline);
    var row = NpcCombatStats.Build(input).Single();
    Assert.AreEqual(500, row.AvgMelee, "later appended ordinals do not enter the frozen prefix");
    Assert.AreEqual(600, row.MaxMelee);

    // A late-ingested heal inside an already frozen life's span must not change its estimate either.
    var killed = first.Where(life => life.Dead).ToList();
    var priced = NpcCombatStats.Capture(killed, facts, cache.Index, timeline, heals, null);
    heals.AddHeal(new HealFact(7, (long)T0, rat, rat, 200, 0, LabelTypes.Heal, 0, -1, HealFact.NoSpell));
    Assert.AreEqual(900, NpcCombatStats.Build(priced).Single().EstHp);
  }

  [TestMethod]
  public void SameSecondZoneChangesKeepFileOrder()
  {
    var zones = new NpcZoneTimeline(new[] { (T0, "First"), (T0, "Second"), (T0 - 1, "Earlier") });
    Assert.AreEqual("Second", zones.ZoneAt(T0));
  }

  [TestMethod]
  public void TheZoneTimelineNamesAnIntervalAndNotACapture()
  {
    var zones = new NpcZoneTimeline(new (double, string?)[]
    {
      (T0 + 100, "The Bazaar"),
      (T0 + 200, null),                // "You have entered an area not on the map."
      (T0 + 300, "The Plane of Fear"),
    });

    Assert.AreEqual(NpcZoneTimeline.UnknownVisit, zones.VisitAt(T0), "before the first zone line we are nowhere named");
    Assert.AreEqual("The Bazaar", zones.ZoneAt(T0 + 150));
    Assert.AreEqual(NpcZoneTimeline.UnknownZone, zones.ZoneAt(T0 + 250), "an unnamed area is still a boundary");
    Assert.AreNotEqual(zones.VisitAt(T0 + 150), zones.VisitAt(T0 + 250));
    Assert.AreEqual("The Plane of Fear", zones.ZoneAt(T0 + 350));
    Assert.AreEqual("The Plane of Fear", zones.ZoneAt(T0 + 9999), "the last line in force stays in force");
    Assert.AreEqual(NpcZoneTimeline.UnknownZone, new NpcZoneTimeline(null).ZoneAt(T0));
  }
}
