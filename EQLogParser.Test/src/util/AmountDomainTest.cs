using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable enable annotations

namespace EQLogParser
{
  /*
   * The amount domain and the two parse helpers that guard it (docs/DesignNotes.md → "Amount widths measured across 44 million hits").
   *
   * MEASURED CEILINGS: the widest number a captured log ever printed is a 10 digit heal (1,276,118), so this domain is not about what a raid
   * can do. It exists so a manglenet report, a synthetic log or an emu under test is either read exactly or refused, and nobody has to decide
   * the width again while looking at one.
   *
   * FAILURE IS -1 AND NOTHING ELSE. The sibling below, ParseUInt, answers its DEFAULT on a bad token, which for its callers (DateUtil's clock
   * fields, loot counts, trigger values) means "no value" and is correct there. For an amount that default was a loaded gun: an unparseable hit
   * became uint.MaxValue and reached the board as a 4.29 billion damage event. So ParseAmount answers -1 — a number no log can print, since the
   * parsers read magnitudes — and the caller refuses the line instead of storing it.
   *
   * THE SIBLING STAYS NARROW ON PURPOSE, and its refusal is asserted here rather than trusted: widening the amounts was a decision about
   * amounts. A clock field that silently accepted a wide token would read a garbage number as a timestamp.
   */
  [TestClass]
  public sealed class AmountDomainTest
  {
    private const long Ceiling = TextUtils.MaxAmount;              // 281474976710655 — fifteen digits
    private const long OnePast = TextUtils.MaxAmount + 1L;
    private const long Wide = 1_099_511_627_775L;                  // 2^40 - 1, comfortably past uint.MaxValue

    [TestMethod]
    public void TheDomainIsFortyEightBitsAndItsOwnDigitCount()
    {
      // Asked through a runtime computation rather than a constant compare, so this says "the domain is 48 bits wide"
      // instead of restating the constant and being trivially true.
      Assert.AreEqual(48, BitLength(Ceiling), "the amount domain is 48 bits — the width the fact rows store");

      /*
       * Both gates get a case of their own: fifteen digits INSIDE the domain; fifteen digits PAST it, which the digit gate waves through and
       * the final compare refuses; and sixteen digits, which never reach the loop at all. Fifteen nines is 9.99e14 against a ceiling of
       * 2.8e14 — a wider domain is not the same as an unbounded one.
       */
      Assert.AreEqual(199_999_999_999_999L, TextUtils.ParseAmount("199999999999999"), "fifteen digits inside the domain read exactly");
      Assert.AreEqual(-1L, TextUtils.ParseAmount(new string('9', 15)), "fifteen nines is past the ceiling: refused, not wrapped");
      Assert.AreEqual(-1L, TextUtils.ParseAmount(new string('9', 16)), "sixteen digits are refused before any arithmetic runs");

      foreach (var (value, allowed) in new[] { (0L, true), (Ceiling, true), (Wide, true), (-1L, false), (OnePast, false), (long.MaxValue, false) })
      {
        Assert.AreEqual(allowed, TextUtils.InAmountRange(value), $"InAmountRange({value})");
      }
    }

    [TestMethod]
    public void ParseAmountReadsEveryBoundaryAndRefusesEverythingElse()
    {
      Assert.AreEqual(0L, TextUtils.ParseAmount("0"));
      Assert.AreEqual(1L, TextUtils.ParseAmount("1"));
      Assert.AreEqual(4294967295L, TextUtils.ParseAmount("4294967295"), "the old ceiling is an ordinary value now");
      Assert.AreEqual(uint.MaxValue + 1L, TextUtils.ParseAmount("4294967296"), "one past uint must NOT answer the old default");
      Assert.AreEqual(Wide, TextUtils.ParseAmount(Wide.ToString()));
      Assert.AreEqual(Ceiling, TextUtils.ParseAmount("281474976710655"), "the widest legal amount reads exactly");

      // Leading zeros are free because the gate counts SIGNIFICANT digits: sixteen zeros and a 12 is still twelve.
      Assert.AreEqual(12L, TextUtils.ParseAmount("000000000000000012"));
      Assert.AreEqual(0L, TextUtils.ParseAmount("0000"), "all zeros is zero, not 'no number'");

      // One past the domain: refused, and NOT clamped to the ceiling.
      Assert.AreEqual(-1L, TextUtils.ParseAmount(OnePast.ToString()), "a 16-digit amount is refused outright");
      Assert.AreEqual(-1L, TextUtils.ParseAmount("99999999999999999999"), "20 digits cannot be a hit");

      foreach (var junk in new[] { "", " ", "abc", "12a34", "1e5", "1,234", " 42", "1 2", "0xFF", "+42", "-5", "." })
      {
        Assert.AreEqual(-1L, TextUtils.ParseAmount(junk), $"a token that is not a magnitude is refused, not guessed: '{junk}'");
        Assert.IsFalse(TextUtils.TryParseAmount(junk, out var value), $"'{junk}' must not claim a value");
        Assert.AreEqual(0L, value, $"'{junk}' failed, so the out value carries no partial number");
      }

      // A minus is not a magnitude. The negative-HOT ruling lives in HealingLineParser and strips it BEFORE this call.
      Assert.IsFalse(TextUtils.TryParseAmount("-146133", out _), "ParseAmount never reads a sign");
    }

    [TestMethod]
    public void ParseUIntStaysNarrowBecauseTheClockLanesDependOnItsRefusal()
    {
      // These are the fields that still ask for a uint — hours, minutes, loot counts, trigger values. Their refusal of a wide or garbage
      // token is what stops a nonsense field becoming a timestamp, so nobody may "just widen" this helper.
      Assert.AreEqual(uint.MaxValue, TextUtils.ParseUInt("4294967295"));
      Assert.AreEqual(uint.MaxValue, TextUtils.ParseUInt("4294967296"), "one past uint still answers the default, not a wrapped value");
      Assert.AreEqual(uint.MaxValue, TextUtils.ParseUInt(Ceiling.ToString()), "the amount domain is NOT the clock domain");
      Assert.AreEqual(uint.MaxValue, TextUtils.ParseUInt(string.Empty));
      Assert.AreEqual(99u, TextUtils.ParseUInt("nonsense", 99u), "the default still means 'no value', which ParseAmount replaces with -1");
      Assert.AreEqual(7u, TextUtils.ParseUInt("007"), "leading zeros behaved before and still do");
    }

    // How many bits a value needs, counted at runtime so an assertion about the domain's width is a measurement.
    private static int BitLength(long value)
    {
      var bits = 0;
      for (var v = value; v > 0L; v >>= 1) bits++;
      return bits;
    }
  }
}
