namespace EQLogParser;

[TestClass]
public class StruckThroughSwingTest
{
  private const short Strike = LineModifiersParser.Strikethrough;
  private const short Riposte = LineModifiersParser.Riposte;

  [TestMethod]
  public void APiercedRiposteAndItsNumberLineAreOneSwing()
  {
    var swings = new DamageSwingAccounting();
    var row = new PlayerSubStats();
    Count(swings, row, Defense(), 100);
    Count(swings, row, Record(Labels.Dot, 111948, 0, "Ryft", "A silk moth"), 100);
    var credit = Count(swings, row, Hit(), 100);
    Assert.IsTrue(credit.AttemptCountedEarlier);
    Assert.AreEqual(Strike, credit.ModifiersCountedEarlier);
    Assert.AreEqual(1u, row.MeleeAttempts);
    Assert.AreEqual(0u, row.RiposteHits, "a pierced defense did not protect the tank");
    Assert.AreEqual(1u, row.StrikethroughHits);
    Assert.AreEqual(1u, row.MeleeHits);
    Assert.AreEqual(0L, row.TotalRiposte, "the attacker's damage is not a successful riposte");
  }

  [TestMethod]
  public void ASentenceWithNoNumberLineStillCountsAnAttemptNotAProtectedRiposte()
  {
    var swings = new DamageSwingAccounting();
    var row = new PlayerSubStats();
    Count(swings, row, Defense(), 100);
    Count(swings, row, Defense(), 100);
    Assert.AreEqual(2u, row.MeleeAttempts);
    Assert.AreEqual(0u, row.RiposteHits);
    Assert.AreEqual(2u, row.StrikethroughHits);
    Assert.AreEqual(0u, row.MeleeHits);
    StatsUtil.CalculateRates(row, new PlayerStats(), null);
    Assert.AreEqual(0u, row.MeleeUndefended);
  }

  [TestMethod]
  public void AHitConsumesOnlyOneOfTwoSentencesAndOnlyOnce()
  {
    var swings = new DamageSwingAccounting();
    var row = new PlayerSubStats();
    Count(swings, row, Defense(), 100);
    Count(swings, row, Defense(), 100);
    Assert.IsTrue(Count(swings, row, Hit(), 100).AttemptCountedEarlier);
    Assert.IsFalse(Count(swings, row, Hit(), 100).AttemptCountedEarlier);
    Assert.AreEqual(3u, row.MeleeAttempts);
    Assert.AreEqual(3u, row.StrikethroughHits);
    Assert.AreEqual(2u, row.MeleeHits);
  }

  [TestMethod]
  public void AProtectedRiposteAndTwoReturnBlowsKeepTheirDifferentMeanings()
  {
    var swings = new DamageSwingAccounting();
    var tank = new PlayerSubStats();
    var damage = new PlayerSubStats();
    Count(swings, tank, Defense(LineModifiersParser.None), 100);
    var first = Record(Labels.Melee, 25159, Riposte, "Ryft", "A silk moth");
    var second = Record(Labels.Melee, 329802, Riposte, "Ryft", "A silk moth");
    Assert.IsFalse(Count(swings, damage, first, 100).AttemptCountedEarlier);
    Assert.IsFalse(Count(swings, damage, second, 100).AttemptCountedEarlier);
    Assert.AreEqual(1u, tank.MeleeAttempts);
    Assert.AreEqual(1u, tank.RiposteHits, "one defense stopped the incoming swing");
    Assert.AreEqual(2u, damage.MeleeHits);
    Assert.AreEqual(2u, damage.RiposteHits, "two outgoing hits were delivered as riposte damage");
    Assert.AreEqual(354961L, damage.TotalRiposte);
  }

  [TestMethod]
  [DataRow(100d, true)]
  [DataRow(101d, true)]
  [DataRow(102d, false)]
  [DataRow(99d, false)]
  public void TheWindowIsForwardAndAtMostOneSecond(double hitTime, bool paired)
  {
    var swings = new DamageSwingAccounting();
    swings.Observe(Defense(), 100);
    Assert.AreEqual(paired, swings.Observe(Hit(), hitTime).AttemptCountedEarlier);
  }

  [TestMethod]
  public void AnotherDefenderOrDirectionCannotClaimTheSentence()
  {
    var swings = new DamageSwingAccounting();
    swings.Observe(Defense(), 100);
    Assert.IsFalse(swings.Observe(Record(Labels.Melee, 1, Strike | Riposte, "A silk moth", "Shmid"), 100)
      .AttemptCountedEarlier);
    Assert.IsFalse(swings.Observe(Record(Labels.Melee, 1, Strike | Riposte, "Ryft", "A silk moth"), 100)
      .AttemptCountedEarlier);
    Assert.IsTrue(swings.Observe(Hit(), 100).AttemptCountedEarlier);
  }

  [TestMethod]
  [DataRow((short)0)]
  [DataRow((short)16)]
  [DataRow((short)32)]
  public void AnIncompatibleHitStartsANewSwingAndRetiresTheOldSentence(short mask)
  {
    var swings = new DamageSwingAccounting();
    swings.Observe(Defense(), 100);
    Assert.IsFalse(swings.Observe(Hit(mask), 100).AttemptCountedEarlier);
    Assert.IsFalse(swings.Observe(Hit(), 100).AttemptCountedEarlier);
  }

  [TestMethod]
  public void AnOrdinaryBlockAndALaterHitAreTwoSwingsEvenInTheSameSecond()
  {
    var swings = new DamageSwingAccounting();
    var row = new PlayerSubStats();
    Count(swings, row, Record(Labels.Block, 0, LineModifiersParser.None), 100);
    Assert.IsFalse(Count(swings, row, Hit(), 100).AttemptCountedEarlier);
    Assert.AreEqual(2u, row.MeleeAttempts);
    Assert.AreEqual(1u, row.Blocks);
  }

  [TestMethod]
  public void AnInterveningDefenseOfTheSamePairRetiresTheOldSentence()
  {
    var swings = new DamageSwingAccounting();
    swings.Observe(Defense(), 100);
    swings.Observe(Record(Labels.Miss, 0, LineModifiersParser.None), 100);
    Assert.IsFalse(swings.Observe(Hit(), 100).AttemptCountedEarlier);
  }

  [TestMethod]
  public void AnAbsorbLeavesItsNumberLineOwingTheStrikethroughCredit()
  {
    var swings = new DamageSwingAccounting();
    var row = new PlayerSubStats();
    Count(swings, row, Record(Labels.Absorb, 0, Strike), 100);
    var credit = Count(swings, row, Hit(Strike), 100);
    Assert.IsTrue(credit.AttemptCountedEarlier);
    Assert.AreEqual((short)0, credit.ModifiersCountedEarlier);
    Assert.AreEqual(1u, row.MeleeAttempts);
    Assert.AreEqual(1u, row.Absorbs);
    Assert.AreEqual(1u, row.StrikethroughHits);
  }

  [TestMethod]
  public void MoreThanEightPendingPairsDoNotEvictAValidSentence()
  {
    var swings = new DamageSwingAccounting();
    for (var i = 0; i < 12; i++) swings.Observe(Record(Labels.Riposte, 0, Strike, $"Mob{i}", "Ryft"), 100);
    for (var i = 0; i < 12; i++)
      Assert.IsTrue(swings.Observe(Record(Labels.Melee, 1, Strike | Riposte, $"Mob{i}", "Ryft"), 100)
        .AttemptCountedEarlier);
  }

  [TestMethod]
  public void NamesMatchWithoutCaseAndWalksOwnTheirState()
  {
    var swings = new DamageSwingAccounting();
    var hit = Hit();
    swings.Observe(Record(Labels.Riposte, 0, Strike, "a silk moth", "RYFT"), 100);
    Assert.IsTrue(swings.Observe(hit, 100).AttemptCountedEarlier);
    Assert.IsFalse(new DamageSwingAccounting().Observe(hit, 100).AttemptCountedEarlier,
      "reusing a published record in a narrower selection must not inherit another build's credit");
    Assert.AreEqual((short)(Strike | Riposte), hit.ModifiersMask);
  }

  [TestMethod]
  public void UnspentTakesTheCompoundRiposteBitButLeavesTheSwingsOwnModifiers()
  {
    Assert.AreEqual((short)0, LineModifiersParser.Unspent(Strike | Riposte, Strike));
    Assert.AreEqual((short)8, LineModifiersParser.Unspent(Strike | Riposte | 8, Strike));
    Assert.AreEqual((short)(Strike | Riposte), LineModifiersParser.Unspent(Strike | Riposte, 0));
    Assert.AreEqual(LineModifiersParser.None, LineModifiersParser.Unspent(LineModifiersParser.None, Strike));
  }

  [TestMethod]
  public void AccuracyDoesNotDivideByZeroOrAnUnsignedUnderflow()
  {
    var row = new PlayerSubStats { Hits = 1, MeleeAttempts = 2, Blocks = 1, Parries = 1 };
    StatsUtil.CalculateRates(row, new PlayerStats(), null);
    Assert.AreEqual(0f, row.MeleeAccRate);
    row.Blocks = 3;
    row.MeleeHits = 1;
    row.MeleeAccRate = 99;
    StatsUtil.CalculateRates(row, new PlayerStats(), null);
    Assert.AreEqual(0f, row.MeleeAccRate);
  }

  private static DamageRecord Defense(short mask = Strike) => Record(Labels.Riposte, 0, mask);
  private static DamageRecord Hit(short mask = Strike | Riposte) => Record(Labels.Melee, 132465, mask);

  private static DamageRecord Record(string type, long total, short mask,
    string attacker = "A silk moth", string defender = "Ryft") => new()
  {
    Attacker = attacker, Defender = defender, Total = total, Type = type, SubType = type, ModifiersMask = mask
  };

  private static DamageSwingCredit Count(DamageSwingAccounting swings, PlayerSubStats row,
    DamageRecord record, double time)
  {
    var credit = swings.Observe(record, time);
    StatsUtil.UpdateDamageStats(row, record, swing: credit);
    return credit;
  }
}
