using System.Globalization;

#nullable enable annotations

namespace EQLogParser.Wpf.Test;

[TestClass]
public class FontSizeParsingTest
{
  [TestMethod]
  [DataRow("en-US")]
  [DataRow("de-DE")]
  [DataRow("fr-FR")]
  public void FontSizeReadersAgreeOnInvariantDecimals(string culture)
  {
    var original = CultureInfo.CurrentCulture;
    try
    {
      CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
      Assert.IsTrue(UiElementUtil.TryParseFontSize(" 12.5 pt", out var size));
      Assert.AreEqual(12.5, size);
      Assert.AreEqual(size, UiElementUtil.ParseFontSize(" 12.5 pt"));
    }
    finally
    {
      CultureInfo.CurrentCulture = original;
    }
  }

  [TestMethod]
  [DataRow(null)]
  [DataRow("")]
  [DataRow("12.5")]
  [DataRow("12,5pt")]
  [DataRow("brokenpt")]
  [DataRow("NaNpt")]
  [DataRow("Infinitypt")]
  public void UnreadableSizesAreRefusedOrUseTheOpeningDefault(string? value)
  {
    Assert.IsFalse(UiElementUtil.TryParseFontSize(value, out _));
    Assert.AreEqual(12, UiElementUtil.ParseFontSize(value));
  }
}
