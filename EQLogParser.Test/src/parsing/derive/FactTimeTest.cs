using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * The clock a fact row carries: seconds since 2000-01-01 stored in an int, read back as the dotnet-epoch second every
 * consumer has always read (docs → FactTime / "The clock a fact row carries"). Two things must be true and both are
 * cheap to assert: the base is what the comment says it is, and the round trip is exact — because the failure mode of
 * this whole design is a silent time shift, which would not throw, would not warn, and would show up as DPS windows in
 * the wrong place or rows that never close.
 *
 * The third group covers the edges: "no time" (0) round trips to 0 rather than to a plausible date, and anything outside
 * the representable window CLAMPS AND COUNTS rather than wrapping — wrapping is precisely the defect the old field's
 * comment recorded, and an untested clamp is the same bug with better manners.
 */
[TestClass]
public class FactTimeTest
{
    [TestInitialize]
    public void Reset() => FactTime.ResetOutOfRange();

    [TestMethod]
    public void TheBaseIsTheYear2000AndNotAnArithmeticClaim()
    {
      var actual = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).Ticks / TimeSpan.TicksPerSecond;
      Assert.AreEqual(FactTime.EpochSeconds, actual,
        "FactTime's stated base must be the year 2000 in dotnet-epoch seconds — every stored second is this number away from reality");
    }

    [TestMethod]
    public void ARealLogStampRoundTripsExactly()
    {
      // The value the parser actually hands the capture, not a made-up one: log line → dotnet-epoch seconds → row → back.
      var stamps = new[]
      {
        "[Sun Apr 26 18:40:10 2026] ",
        "[Mon Dec 31 23:59:59 2029] ",
        "[Fri Jan 01 00:00:00 2021] ",
      };

      foreach (var line in stamps)
      {
        var epoch = DateUtil.StandardDateToDotNetSeconds(line + "x");
        var stored = FactTime.FromEpochSeconds(epoch);
        var back = FactTime.ToEpochSeconds(stored);

        Assert.AreEqual((long)Math.Round(epoch), back, $"the stamp printed from {line.Trim()} came back as something else");
        Assert.AreEqual(0, FactTime.OutOfRange, $"{line.Trim()} was representable and must not have clamped");
      }
    }

    [TestMethod]
    public void BothFactRowsReadBackTheSecondTheyWereGiven()
    {
      var facts = new DamageFactTable(8);
      var heals = new HealFactTable(facts, 8);

      const long stamp = 63_902_822_400L;   // 2026-01-01T00:00:00, dotnet-epoch seconds
      var a = facts.InternName("Reisil");
      var b = facts.InternName("A grizzled skeleton");

      facts.AddFact(new DamageFact(1, stamp, a, b, total: 1234, typeId: LabelTypes.IdOf(Labels.Melee),
        flags: 0, modMask: LineModifiersParser.None, subIdx: DamageFactTable.NoSubtype));
      heals.AddHeal(new HealFact(2, stamp, a, b, total: 500, overTotal: 0, typeId: LabelTypes.IdOf(Labels.Heal),
        flags: 0, modMask: LineModifiersParser.None, subIdx: HealFact.NoSpell));

      Assert.AreEqual(stamp, facts.Facts[0].TimeS, "a damage fact's TimeS is the second that went in");
      Assert.AreEqual(stamp, heals.Heals[0].TimeS, "a heal fact's TimeS is the second that went in");
      Assert.AreEqual(0, FactTime.OutOfRange);

      // And the row is still the size the memory pass bought: the narrow field is real, not a property over a long.
      Assert.AreEqual(24, Unsafe.SizeOf<DamageFact>());
      Assert.AreEqual(28, Unsafe.SizeOf<HealFact>());
    }

    [TestMethod]
    public void ARowWithNoTimeStillReadsZeroRatherThanADate()
    {
      var stored = FactTime.FromEpochSeconds(0L);
      Assert.AreEqual(FactTime.NoTime, stored, "an unresolved BeginTime is stored as the sentinel, not as a second");
      Assert.AreEqual(0L, FactTime.ToEpochSeconds(stored), "and reads back as 0, exactly as it did when the field was a long");

      Assert.AreEqual(FactTime.NoTime, FactTime.FromEpochSeconds(double.NaN),
        "NaN is not a time; it must not reach the row as some year");
    }

    [TestMethod]
    public void ASecondOutsideTheWindowIsClampedAndCounted()
    {
      // Year 1 (what an unparsed stamp used to store as-is) and the far future, both outside 1931-2068.
      Assert.AreEqual(0, FactTime.OutOfRange);

      var early = FactTime.FromEpochSeconds(1_000_000L);
      Assert.AreNotEqual(0L, FactTime.ToEpochSeconds(early), "a clamp is a clamp: it reads as an edge of the window, not as nothing");

      var late = FactTime.FromEpochSeconds(FactTime.EpochSeconds + (long)int.MaxValue * 4);
      Assert.AreEqual(FactTime.MaxStored, late, "past 2068 the row sits at the window's top");

      Assert.AreEqual(2, FactTime.OutOfRange,
        "every clamp is counted — an uncounted clamp is how a clock bug becomes invisible again");
    }

    [TestMethod]
    public void FixtureClocksAreInsideTheWindow()
    {
      /*
       * The one way this suite could quietly stop meaning anything: a fixture whose `T0` is a shorthand "second" (1_000)
       * rather than a date. Those clamp — every fact in the fixture lands on the same instant and a projection test starts
       * testing a pile of simultaneous events. Fixture clocks are real-shaped, so assert that they stay that way.
       */
      Assert.AreEqual(0, FactTime.OutOfRange);
      var stored = FactTime.FromEpochSeconds((long)FixtureTime.Base);
      Assert.AreNotEqual(FactTime.NoTime, stored);
      Assert.AreEqual((long)FixtureTime.Base, FactTime.ToEpochSeconds(stored));
      Assert.AreEqual(0, FactTime.OutOfRange, "the fixture base must be representable without clamping");
    }
}
