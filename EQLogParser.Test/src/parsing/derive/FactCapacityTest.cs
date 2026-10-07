using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable enable annotations

namespace EQLogParser
{
  /*
   * A log file knows its own length before it is read, and both fact tables' constructors take a starting capacity.
   * This is the estimate that joins them, held to the two captures it was measured on:
   *
   * | capture | bytes | damage facts | heal facts |
   * |---|---|---|---|
   * | `eqlog_Kizant_xegony-2.txt` | 467,635,613 | 2,286,368 | 1,247,984 |
   * | `eqlog_Kizant_xegony-09-03-26.txt` | 997,656,755 | 4,832,102 | 2,670,809 |
   *
   * Seven months apart and the density is the same to within 1 % (one damage fact per ~205 bytes, one heal per ~375),
   * which is the only reason a fixed constant is defensible. Without a hint the arrays double from their defaults and
   * land on the next power-of-two: the bigger capture finished with 4.8 M facts in 8.4 M slots and 2.67 M heals in
   * 4.19 M — ~162 MB of reserved address space holding nothing at the moment the load reported itself done.
   *
   * The direction of error matters more than its size, and that is the law these tests really hold: a table that runs
   * one fact past its capacity DOUBLES, so undershooting by 1 % costs 100 % more slots — while overshooting costs the
   * overshoot, and `CombatCapture.CompactRows` hands most of that back at end of file. Hence the margin, and hence the
   * assertion that a hint always covers the measured count rather than merely "comes close".
   */
  [TestClass]
  public class FactCapacityTest
  {
    private const long BigCaptureBytes = 997_656_755;
    private const int BigCaptureDamageFacts = 4_832_102;
    private const int BigCaptureHealFacts = 2_670_809;

    private const long OlderCaptureBytes = 467_635_613;
    private const int OlderCaptureDamageFacts = 2_286_368;
    private const int OlderCaptureHealFacts = 1_247_984;

    // Bound on waste: a hint may not reserve more than a third over what the capture actually wrote. Without this half
    // the "always cover the count" test could be satisfied by precommitting 64 M slots.
    private const double MaxOverhead = 1.35;

    [TestMethod]
    public void AHintedCapacityCoversWhatTheCaptureActuallyWrote()
    {
      AssertIsUseful(FactCapacity.DamageForBytes(BigCaptureBytes), BigCaptureDamageFacts);
      AssertIsUseful(FactCapacity.HealForBytes(BigCaptureBytes), BigCaptureHealFacts);
      AssertIsUseful(FactCapacity.DamageForBytes(OlderCaptureBytes), OlderCaptureDamageFacts);
      AssertIsUseful(FactCapacity.HealForBytes(OlderCaptureBytes), OlderCaptureHealFacts);
    }

    [TestMethod]
    public void AHintIsCheaperThanTheDoublingItReplaces()
    {
      // What the arrays end up at when nobody sizes them: the power-of-two above the count, starting from each table's
      // own default. The hint has to beat that on both streams or it is not a memory change at all.
      var doubledDamage = PowerOfTwoAtLeast(BigCaptureDamageFacts, FactCapacity.DefaultDamageSlots);
      var doubledHeals = PowerOfTwoAtLeast(BigCaptureHealFacts, FactCapacity.DefaultHealSlots);

      Assert.IsTrue(FactCapacity.DamageForBytes(BigCaptureBytes) < doubledDamage,
        $"damage hint {FactCapacity.DamageForBytes(BigCaptureBytes):N0} should beat doubling to {doubledDamage:N0}");
      Assert.IsTrue(FactCapacity.HealForBytes(BigCaptureBytes) < doubledHeals,
        $"heal hint {FactCapacity.HealForBytes(BigCaptureBytes):N0} should beat doubling to {doubledHeals:N0}");
    }

    [TestMethod]
    public void NoHintLeavesTheTablesOnTheirOwnDefaults()
    {
      Assert.AreEqual(FactCapacity.DefaultDamageSlots, FactCapacity.DamageForBytes(0));
      Assert.AreEqual(FactCapacity.DefaultHealSlots, FactCapacity.HealForBytes(0));

      // A length nobody read (missing file, a redirect that lied) must not become a reservation.
      Assert.AreEqual(FactCapacity.DefaultDamageSlots, FactCapacity.DamageForBytes(-1));
      Assert.AreEqual(FactCapacity.DefaultHealSlots, FactCapacity.HealForBytes(-5_000_000_000));

      // A small log is smaller than the smallest sensible array: also the default, never a 1-row precommit.
      Assert.AreEqual(FactCapacity.DefaultDamageSlots, FactCapacity.DamageForBytes(4_096));
    }

    [TestMethod]
    public void ABogusLengthCannotPrecommitTheMachine()
    {
      // 64 GB of "log" is a bad guess, not an order. Past the ceiling the table starts at the ceiling and doubles like
      // it always did, so the clamp costs correctness nothing even if a real capture ever needed more.
      Assert.AreEqual(FactCapacity.MaxDamageSlots, FactCapacity.DamageForBytes(64L * 1024 * 1024 * 1024));
      Assert.AreEqual(FactCapacity.MaxHealSlots, FactCapacity.HealForBytes(64L * 1024 * 1024 * 1024));
    }

    [TestMethod]
    public void MoreBytesNeverMeansLessRoom()
    {
      long previous = 0;
      int previousDamage = 0;
      int previousHeals = 0;
      foreach (var bytes in new[] { 1_000L, 1_000_000L, 50_000_000L, OlderCaptureBytes, BigCaptureBytes, 2L * 1024 * 1024 * 1024 })
      {
        var damage = FactCapacity.DamageForBytes(bytes);
        var heals = FactCapacity.HealForBytes(bytes);
        Assert.IsTrue(damage >= previousDamage, $"{bytes:N0} bytes got less damage room than {previous:N0}");
        Assert.IsTrue(heals >= previousHeals, $"{bytes:N0} bytes got less heal room than {previous:N0}");
        Assert.IsTrue(damage > heals, "damage lines outnumber heal lines on every capture measured");

        previous = bytes;
        previousDamage = damage;
        previousHeals = heals;
      }
    }

    private static void AssertIsUseful(int capacity, int factsWritten)
    {
      Assert.IsTrue(capacity >= factsWritten,
        $"capacity {capacity:N0} is under the {factsWritten:N0} facts this capture wrote — the load would double anyway");
      Assert.IsTrue(capacity <= factsWritten * MaxOverhead,
        $"capacity {capacity:N0} reserves more than {MaxOverhead:P0} over the {factsWritten:N0} facts written");
    }

    private static int PowerOfTwoAtLeast(int needed, int start)
    {
      var capacity = start;
      while (capacity < needed && capacity < FactCapacity.MaxDamageSlots) capacity *= 2;
      return capacity;
    }
  }
}
