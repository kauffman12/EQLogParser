using System.Globalization;

namespace EQLogParser
{
  /*
   * Number formatting is the one piece of FCT every player reads, so the tier boundaries and the rounding
   * carries are pinned here: 99,960 must not read "100.0k" and 999,500 must not read "1000k".
   */
  // the culture test below swaps CurrentCulture: keep this class out of the parallel pool
  [DoNotParallelize]
  [TestClass]
  public sealed class FctTextTest
  {
    [TestMethod]
    [DataRow(0d, "0")]
    [DataRow(7d, "7")]
    [DataRow(999d, "999")]
    [DataRow(1000d, "1,000")]
    [DataRow(1234d, "1,234")]
    [DataRow(9999d, "9,999")]
    [DataRow(10_000d, "10k")]
    [DataRow(12_500d, "12.5k")]
    [DataRow(45_600d, "45.6k")]
    [DataRow(99_949d, "99.9k")]
    [DataRow(99_960d, "100k")]
    [DataRow(100_000d, "100k")]
    [DataRow(234_000d, "234k")]
    [DataRow(999_400d, "999k")]
    [DataRow(999_500d, "1m")]
    [DataRow(1_000_000d, "1m")]
    [DataRow(1_500_000d, "1.5m")]
    public void FormatsHitValues(double value, string expected) => Assert.AreEqual(expected, FctText.FormatHitValue(value));

    /*
     * The count trailer, and the reason it is a count rather than a sum: one hit's face value plus how many of them landed.
     * "×1" is never drawn — a number standing for its own hit needs no explanation.
     */
    [TestMethod]
    [DataRow(2040d, 1, "2,040")]
    [DataRow(2040d, 2, "2,040 ×2")]
    [DataRow(412d, 7, "412 ×7")]
    [DataRow(12_500d, 3, "12.5k ×3")]
    public void FormatsTheValueWithItsRepeatCount(double value, int mergeCount, string expected) =>
      Assert.AreEqual(expected, FctText.FormatHit(value, mergeCount));

    /* The output is always invariant: a German locale must not print "12,5k" for a damage number. */
    [TestMethod]
    public void FormatsWithoutDependingOnTheCurrentCulture()
    {
      var original = CultureInfo.CurrentCulture;
      try
      {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        Assert.AreEqual("12.5k", FctText.FormatHitValue(12_500));
        Assert.AreEqual("12.5k ×3", FctText.FormatHit(12_500, 3), "and the count must not pick up a locale's separators");
        Assert.AreEqual("1,234", FctText.FormatHitValue(1234));
      }
      finally
      {
        CultureInfo.CurrentCulture = original;
      }
    }
  }
}
