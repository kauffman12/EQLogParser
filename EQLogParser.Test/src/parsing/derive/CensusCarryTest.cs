namespace EQLogParser;

/*
 * The carried Names census (CensusCarry) may only ever be an FASTER way to get the answer the whole-capture walk got. Every test here
 * therefore builds the same capture twice over — once through a carry that has seen the earlier phases, once through a census with no
 * carry at all, which is the walk as it always was — and demands the rows agree field by field. Equality is the whole contract: an
 * optimization that reports a different number is not a fast census, it is a wrong one.
 *
 * The phases are the point. A carry that matched only on a finished capture would pass while losing whatever arrived between two asks,
 * which is exactly the window the Identity Table lives in: it asks every derive pass, all night, while the log is still growing.
 */
[TestClass]
public class CensusCarryTest
{
  private const ushort Zone = ushort.MaxValue;

  [TestMethod]
  public void ACarriedCensusMatchesAFreshOneAsFactsArrive()
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    var timeline = ClaimNames(out var raid, out var mob);

    var carry = new CensusCarry();
    for (var phase = 1; phase <= 5; phase++)
    {
      // Each phase writes facts and heals, including one brand-new name: ids are permanent, and a carry must fold the tail it has not eaten.
      for (var i = 0; i < 40; i++)
      {
        var atk = i % 2 == 0 ? raid[0] : raid[i % raid.Count];
        facts.AddFact(new DamageFact(phase * 1000 + i, FixtureTime.BaseL + phase * 60 + i,
          facts.InternName(atk), facts.InternName(i % 7 == 0 ? mob : raid[1]), 100 + i, 0, 0, 0, Zone));
      }

      heals.AddHeal(new HealFact(phase * 1000, FixtureTime.BaseL + phase * 60, facts.InternName(raid[1]),
        facts.InternName(raid[0]), 500, 0, 0, 0, 0, 0));
      facts.AddFact(new DamageFact(phase * 1000 + 900, FixtureTime.BaseL + phase * 60 + 5,
        facts.InternName($"Latecomer{phase}"), facts.InternName(mob), 11, 0, 0, 0, Zone));

      AssertCarriedEqualsFresh(carry, timeline, facts, heals, $"phase {phase}");
    }
  }

  /*
   * The dangerous state, and the reason the carry stores attacker/defender PAIRS instead of four side buckets: a defender's verdict moves
   * mid-capture (R6 naming a mob, an operator write), so a previously folded fact belongs in a different sentence. A carry that froze the
   * buckets at fold time would keep the old number forever; this re-reads the side counts for facts it stopped reading long ago.
   */
  [TestMethod]
  public void AVerdictThatMovesMidCaptureMovesTheCarriedSideCounts()
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    var timeline = new EntityTimeline();

    const string hitter = "Zynkar";
    const string victim = "Cinderal";

    // Nobody has placed the victim yet, so the hitter earns no "Damaged Players" and no "Damaged Mobs" either.
    for (var i = 0; i < 12; i++)
    {
      facts.AddFact(new DamageFact(i + 1, FixtureTime.BaseL + i, facts.InternName(hitter),
        facts.InternName(victim), 50, 0, 0, 0, Zone));
    }

    var carry = new CensusCarry();
    AssertCarriedEqualsFresh(carry, timeline, facts, heals, "before anybody is placed");

    SetKind(timeline, victim, IdentityKind.Npc);
    AssertCarriedEqualsFresh(carry, timeline, facts, heals, "victim named a mob");
    AssertRow(Carried(carry, timeline, facts, heals), hitter, hitsOnMobs: 12, hitsOnRaid: 0);

    // And back again — the operator overrode it, which is a take-back at Manual strength, not a new sighting.
    SetKind(timeline, victim, IdentityKind.Player);
    AssertCarriedEqualsFresh(carry, timeline, facts, heals, "victim ruled a player");
    AssertRow(Carried(carry, timeline, facts, heals), hitter, hitsOnMobs: 0, hitsOnRaid: 12);

    // A Merc is raid-side for WHO a spell belonged to, and a Pet is deliberately not.
    SetKind(timeline, victim, IdentityKind.Merc);
    AssertRow(Carried(carry, timeline, facts, heals), hitter, hitsOnMobs: 0, hitsOnRaid: 12);
    SetKind(timeline, victim, IdentityKind.Pet);
    AssertRow(Carried(carry, timeline, facts, heals), hitter, hitsOnMobs: 0, hitsOnRaid: 0, hitsOnPets: 12);
    AssertCarriedEqualsFresh(carry, timeline, facts, heals, "victim called a pet");
  }

  /*
   * A reflect is not "damaging players": the fact whose attacker and defender are the same name is its own sentence. Self-hits ride outside
   * the pair map on purpose, so this pins that the carry still separates them after several folds.
   */
  [TestMethod]
  public void ACarriedCensusKeepsSelfHitsOutOfEverySideCount()
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    var timeline = ClaimNames(out var raid, out _);

    for (var i = 0; i < 9; i++)
    {
      facts.AddFact(new DamageFact(i + 1, FixtureTime.BaseL + i, facts.InternName(raid[0]),
        facts.InternName(raid[0]), 7, 0, 0, 0, Zone));
    }

    var carry = new CensusCarry();
    AssertCarriedEqualsFresh(carry, timeline, facts, heals, "reflects only");

    // Facts at other names arriving later may not disturb the self-hit row that was already folded.
    for (var i = 0; i < 5; i++)
    {
      facts.AddFact(new DamageFact(100 + i, FixtureTime.BaseL + 30 + i, facts.InternName(raid[0]),
        facts.InternName(raid[1]), 8, 0, 0, 0, Zone));
    }

    AssertCarriedEqualsFresh(carry, timeline, facts, heals, "reflects plus real hits");
    AssertRow(Carried(carry, timeline, facts, heals), raid[0], selfHits: 9, hitsOnRaid: 5);
  }

  /*
   * A carry belongs to ONE capture. Clear All hands the engine a brand-new pair of tables over the same file, and a carry that compared
   * nothing but row counts would add yesterday's totals to today's rows.
   */
  [TestMethod]
  public void ACarryHandedAnotherCapturesTablesStartsOver()
  {
    var timeline = ClaimNames(out var raid, out var mob);

    var first = new DamageFactTable();
    var firstHeals = new HealFactTable(first);
    for (var i = 0; i < 20; i++)
    {
      first.AddFact(new DamageFact(i + 1, FixtureTime.BaseL + i, first.InternName(raid[0]),
        first.InternName(mob), 30, 0, 0, 0, Zone));
      firstHeals.AddHeal(new HealFact(i + 1, FixtureTime.BaseL + i, first.InternName(raid[1]),
        first.InternName(raid[0]), 90, 0, 0, 0, 0, 0));
    }

    var carry = new CensusCarry();
    AssertCarriedEqualsFresh(carry, timeline, first, firstHeals, "first capture");

    // The re-open: same file, new tables, ids start from nought again.
    var second = new DamageFactTable();
    var secondHeals = new HealFactTable(second);
    for (var i = 0; i < 6; i++)
    {
      second.AddFact(new DamageFact(i + 1, FixtureTime.BaseL + i, second.InternName(raid[1]),
        second.InternName(mob), 5, 0, 0, 0, Zone));
    }

    AssertCarriedEqualsFresh(carry, timeline, second, secondHeals, "after the re-open");

    /*
     * The leak this pins: a carry that kept counting would still print the FIRST capture's numbers here. The name that only the first
     * capture knew has no row at all — rows are the new pool, not the union of everything ever folded — and the name the new capture does
     * know starts at nought with only its six new hits behind it.
     */
    var rows = Carried(carry, timeline, second, secondHeals);
    Assert.IsFalse(rows.Any(r => r.Name == raid[0]), $"{raid[0]} belongs to the capture that ended, not this one.");
    AssertRow(rows, raid[1], damage: 30, healing: 0, events: 6, hitsOnMobs: 6);
  }

  /*
   * The one carried table whose entries depend on a VERDICT AT AN OLD TIME: how many distinct raid-side casters tended a name. Healer two
   * was nobody when the early heal lines were folded, and becomes one of ours later; the crowd must say two, not the one it happened to see
   * first. This is why that table carries the timeline's full state stamp and drops itself when the stamp moves — the only place in the carry
   * where a stamp gates anything, and the one place a watermark alone would have been wrong.
   */
  [TestMethod]
  public void AHealerPlacedLateRestatesTheCarriedHealerCrowd()
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    var timeline = new EntityTimeline();

    const string first = "Kalsarya";
    const string second = "Brukfist";
    const string target = "Tomleg";

    timeline.SetIdentity(first, IdentityKind.Player, RuleStrength.Strong, "R3-presence");

    for (var i = 0; i < 3; i++)
    {
      heals.AddHeal(new HealFact(i + 1, FixtureTime.BaseL + i, facts.InternName(first),
        facts.InternName(target), 100, 0, 0, 0, 0, 0));
      heals.AddHeal(new HealFact(i + 40, FixtureTime.BaseL + i + 3, facts.InternName(second),
        facts.InternName(target), 100, 0, 0, 0, 0, 0));
    }

    var carry = new CensusCarry();
    Fold(carry, timeline, facts, heals);
    Assert.AreEqual(1, carry.CasterBreadth("R15-healed", target), "Only the placed healer counts while the other is nobody.");

    // The rules name the second healer. The heal lines it already healed do not change, but what they MEAN does.
    timeline.SetIdentity(second, IdentityKind.Player, RuleStrength.Strong, "R3-presence");
    Fold(carry, timeline, facts, heals);
    Assert.AreEqual(2, carry.CasterBreadth("R15-healed", target), "A healer placed after its heals were folded still counts.");

    // ...and a healer arriving later, on heal rows the carry has not seen at all.
    const string third = "Vessine";
    timeline.SetIdentity(third, IdentityKind.Player, RuleStrength.Strong, "R3-presence");
    heals.AddHeal(new HealFact(900, FixtureTime.BaseL + 90, facts.InternName(third),
      facts.InternName(target), 120, 0, 0, 0, 0, 0));
    Fold(carry, timeline, facts, heals);
    Assert.AreEqual(3, carry.CasterBreadth("R15-healed", target), "New heal lines fold onto the crowd without losing the old ones.");

    // A name that was never a healer at all has no crowd, and asking it must not have side effects on the table.
    Assert.AreEqual(0, carry.CasterBreadth("R15-healed", first));
    Assert.AreEqual(0, carry.CasterBreadth(null, target), "Only the heal-based verdict asks, and an absent word asks nothing.");
  }

  [TestMethod]
  [DataRow(RuleStrength.Medium, RuleStrength.Strong, 0, 1)]
  [DataRow(RuleStrength.Strong, RuleStrength.Medium, 1, 0)]
  public void AStrengthOnlyChangeRefreshesTheHistoricalHealerCrowd(int beforeStrength, int afterStrength,
                                                                 int beforeCount, int afterCount)
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    var healer = facts.InternName("Healer");
    var target = facts.InternName("Target");
    heals.AddHeal(new HealFact(1, FixtureTime.BaseL, healer, target, 100, 0, 0, 0, 0, 0));
    var before = HealerTimeline(beforeStrength);
    var after = HealerTimeline(afterStrength);

    // Boards route the same Player either way, but the crowd reads strength as well as kind.
    Assert.AreEqual(before.AnswerStamp(), after.AnswerStamp());
    Assert.AreNotEqual(before.StateStamp(), after.StateStamp());
    var carry = new CensusCarry();
    var first = ClassificationReport.Build(before, facts, heals, null, null, null, carry);
    Assert.AreEqual(beforeCount, first.Find("Target")?.HealedByCasters);
    var changed = ClassificationReport.Build(after, facts, heals, null, null, null, carry);
    Assert.AreEqual(afterCount, changed.Find("Target")?.HealedByCasters);
    AssertCarriedEqualsFresh(carry, after, facts, heals, "same kind, different historical strength");
    Assert.AreEqual(beforeCount, first.Find("Target")?.HealedByCasters, "Published rows must remain unchanged.");
  }

  [TestMethod]
  public void AStrengthPromotionOnTheSameTimelineRefreshesTheHealerCrowd()
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    heals.AddHeal(new HealFact(1, FixtureTime.BaseL, facts.InternName("Healer"),
      facts.InternName("Target"), 100, 0, 0, 0, 0, 0));
    var timeline = HealerTimeline(RuleStrength.Medium);
    var carry = new CensusCarry();
    ClassificationReport.Build(timeline, facts, heals, null, null, null, carry);
    var stamp = timeline.AnswerStamp();
    timeline.SetIdentity("Healer", IdentityKind.Player, RuleStrength.Strong, "test-promotion");
    Assert.AreEqual(stamp, timeline.AnswerStamp());
    AssertCarriedEqualsFresh(carry, timeline, facts, heals, "in-place strength promotion");
    Assert.AreEqual(1, Carried(carry, timeline, facts, heals).Single(row => row.Name == "Target").HealedByCasters);
  }

  [TestMethod]
  public void AHealerCrowdWithoutATimelineDoesNotKeepThePreviousCount()
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    heals.AddHeal(new HealFact(1, FixtureTime.BaseL, facts.InternName("Healer"),
      facts.InternName("Target"), 100, 0, 0, 0, 0, 0));
    var timeline = HealerTimeline(RuleStrength.Strong);
    var carry = new CensusCarry();
    Fold(carry, timeline, facts, heals);
    Assert.AreEqual(1, carry.CasterBreadth("R15-healed", "Target"));
    carry.Fold(facts, heals, null, facts.InternedNames, facts.InternedNames.Count, facts.FactCount, heals.HealCount);
    Assert.AreEqual(0, carry.CasterBreadth("R15-healed", "Target"));
    Fold(carry, timeline, facts, heals);
    Assert.AreEqual(1, carry.CasterBreadth("R15-healed", "Target"));
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void AReplacementTimelinesTieOrderRefreshesTheHealerCrowd(bool playerWinsFirst)
  {
    var facts = new DamageFactTable();
    var heals = new HealFactTable(facts);
    heals.AddHeal(new HealFact(1, FixtureTime.BaseL, facts.InternName("Healer"),
      facts.InternName("Target"), 100, 0, 0, 0, 0, 0));
    var before = TiedTimeline(playerWinsFirst);
    var after = TiedTimeline(!playerWinsFirst);
    Assert.AreEqual(before.StateStamp(), after.StateStamp(), "The same claims sum to the same state stamp.");
    Assert.AreNotEqual(before.AnswerStamp(), after.AnswerStamp(), "Equal strength/time ties read arrival order.");
    var carry = new CensusCarry();
    var first = ClassificationReport.Build(before, facts, heals, null, null, null, carry);
    Assert.AreEqual(playerWinsFirst ? 1 : 0, first.Find("Target")?.HealedByCasters);
    var changed = ClassificationReport.Build(after, facts, heals, null, null, null, carry);
    Assert.AreEqual(playerWinsFirst ? 0 : 1, changed.Find("Target")?.HealedByCasters);
    AssertCarriedEqualsFresh(carry, after, facts, heals, "same claims, different tie order");
    Assert.AreEqual(playerWinsFirst ? 1 : 0, first.Find("Target")?.HealedByCasters);

    static EntityTimeline TiedTimeline(bool playerWins)
    {
      var timeline = new EntityTimeline();
      timeline.SetIdentity("Healer", playerWins ? IdentityKind.Npc : IdentityKind.Player,
        RuleStrength.Strong, playerWins ? "npc" : "player");
      timeline.SetIdentity("Healer", playerWins ? IdentityKind.Player : IdentityKind.Npc,
        RuleStrength.Strong, playerWins ? "player" : "npc");
      timeline.SetIdentity("Target", IdentityKind.Player, RuleStrength.Strong, "R15-healed");
      return timeline;
    }
  }

  private static EntityTimeline HealerTimeline(int strength)
  {
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Healer", IdentityKind.Player, strength, "test-healer");
    timeline.SetIdentity("Target", IdentityKind.Player, RuleStrength.Strong, "R15-healed");
    return timeline;
  }

  private static void Fold(CensusCarry carry, EntityTimeline timeline, DamageFactTable facts, HealFactTable heals)
    => carry.Fold(facts, heals, timeline, facts.InternedNames, facts.InternedNames.Count, facts.FactCount, heals.HealCount);

  /// <summary>One fold through the carry, and the same census built by a window that brought no carry at all.</summary>
  private static void AssertCarriedEqualsFresh(CensusCarry carry, EntityTimeline timeline, DamageFactTable facts,
                                               HealFactTable heals, string because)
  {
    var carried = ByName(Carried(carry, timeline, facts, heals));
    var fresh = ByName(ClassificationReport.Build(timeline, facts, heals, null, null).Rows);

    Assert.AreEqual(fresh.Count, carried.Count, $"Row count differs from a whole-capture census ({because}).");

    foreach (var pair in fresh)
    {
      Assert.IsTrue(carried.TryGetValue(pair.Key, out var got), $"{pair.Key} is missing from the carried census ({because}).");
      var want = pair.Value;

      Assert.AreEqual(want.Damage, got.Damage, $"{pair.Key} damage ({because}).");
      Assert.AreEqual(want.Healing, got.Healing, $"{pair.Key} healing ({because}).");
      Assert.AreEqual(want.Events, got.Events, $"{pair.Key} line count ({because}).");
      Assert.AreEqual(want.HitsOnRaid, got.HitsOnRaid, $"{pair.Key} damaged-players sentence ({because}).");
      Assert.AreEqual(want.HitsOnMobs, got.HitsOnMobs, $"{pair.Key} damaged-mobs sentence ({because}).");
      Assert.AreEqual(want.HitsOnPets, got.HitsOnPets, $"{pair.Key} damaged-pets sentence ({because}).");
      Assert.AreEqual(want.SelfHits, got.SelfHits, $"{pair.Key} self-hit count ({because}).");
      Assert.AreEqual(want.HealedByCasters, got.HealedByCasters, $"{pair.Key} healer crowd ({because}).");
      Assert.AreEqual(want.ReasonDetail, got.ReasonDetail, $"{pair.Key} cast proof ({because}).");
      Assert.AreEqual(want.Kind, got.Kind, $"{pair.Key} verdict ({because}).");
      Assert.AreEqual(want.TypeDisplay, got.TypeDisplay, $"{pair.Key} type sentence ({because}).");
    }
  }

  private static IReadOnlyList<ClassificationReport.Row> Carried(CensusCarry carry, EntityTimeline timeline,
                                                                 DamageFactTable facts, HealFactTable heals)
    => ClassificationReport.Build(timeline, facts, heals, null, null, null, carry).Rows;

  private static Dictionary<string, ClassificationReport.Row> ByName(IReadOnlyList<ClassificationReport.Row> rows)
  {
    var byName = new Dictionary<string, ClassificationReport.Row>(StringComparer.OrdinalIgnoreCase);
    foreach (var row in rows) byName[row.Name] = row;
    return byName;
  }

  private static EntityTimeline ClaimNames(out List<string> raid, out string mob)
  {
    const string mobName = "A Fungal Crawler";
    var timeline = new EntityTimeline();
    raid = ["Zynkar", "Berrymint", "Kalinda"];

    foreach (var name in raid) SetKind(timeline, name, IdentityKind.Player);
    SetKind(timeline, mobName, IdentityKind.Npc);
    mob = mobName;
    return timeline;
  }

  private static void SetKind(EntityTimeline timeline, string name, IdentityKind kind)
    => timeline.SetIdentity(name, kind, RuleStrength.Certain, $"test-{kind}");

  private static void AssertRow(IReadOnlyList<ClassificationReport.Row> rows, string name,
                                double damage = double.NaN, double healing = double.NaN, long events = -1,
                                int hitsOnRaid = -1, int hitsOnMobs = -1, int hitsOnPets = -1, int selfHits = -1)
  {
    Assert.IsTrue(ByName(rows).TryGetValue(name, out var row), $"No row for {name}.");
    if (!double.IsNaN(damage)) Assert.AreEqual(damage, row.Damage, $"{name} damage.");
    if (!double.IsNaN(healing)) Assert.AreEqual(healing, row.Healing, $"{name} healing.");
    if (events >= 0) Assert.AreEqual(events, row.Events, $"{name} line count.");
    if (hitsOnRaid >= 0) Assert.AreEqual(hitsOnRaid, row.HitsOnRaid, $"{name} damaged-players count.");
    if (hitsOnMobs >= 0) Assert.AreEqual(hitsOnMobs, row.HitsOnMobs, $"{name} damaged-mobs count.");
    if (hitsOnPets >= 0) Assert.AreEqual(hitsOnPets, row.HitsOnPets, $"{name} damaged-pets count.");
    if (selfHits >= 0) Assert.AreEqual(selfHits, row.SelfHits, $"{name} self-hit count.");
  }
}
