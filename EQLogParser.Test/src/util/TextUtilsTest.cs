using EQLogParser;
using System.Globalization;

#nullable enable annotations

namespace EQLogParserTest
{
  [TestClass]
  public class TextUtilsTest
  {
    [TestMethod]
    [DataRow("hello", "Hello")]
    [DataRow("john doe", "John doe")]
    [DataRow("HELLO", "HELLO")]
    [DataRow("hELLO", "HELLO")]
    [DataRow("", "")]
    [DataRow(null, null)]
    [DataRow("a", "A")]
    [DataRow("test-name_123", "Test-name_123")]
    [DataRow("aBCdEF", "ABCdEF")]
    [DataRow("123abc", "123abc")]
    [DataRow(" hello", " hello")]
    [DataRow("susarrak the crusader", "Susarrak the crusader")]
    [DataRow("an ice giant", "An ice giant")]
    [DataRow("lobekn", "Lobekn")]
    [DataRow("kizante`s", "Kizante`s")]
    [DataRow("ogna, artisan of war", "Ogna, artisan of war")]
    [DataRow("test.test", "Test.test")]
    [DataRow("tuyen`s", "Tuyen`s")]
    public void CapitalizationChangesOnlyTheFirstCharacter(string? text, string? expected)
    {
      Assert.AreEqual(expected, TextUtils.CapitalizeFirst(text));
    }

    [TestMethod]
    [DataRow("", "I")]
    [DataRow("tr-TR", "\u0130")]
    public void AnExplicitCultureControlsCapitalization(string culture, string expected)
    {
      Assert.AreEqual(expected, TextUtils.CapitalizeFirst("i", CultureInfo.GetCultureInfo(culture)));
    }
  }
}
