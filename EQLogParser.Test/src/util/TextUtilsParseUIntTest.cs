using EQLogParser;

#nullable enable annotations

namespace EQLogParserTest
{
  [TestClass]
  public class TextUtilsParseUIntTest
  {
    [TestMethod]
    [DataRow("5", 5u)]
    [DataRow("42", 42u)]
    [DataRow("123", 123u)]
    [DataRow("4294967295", uint.MaxValue)]
    [DataRow("0", 0u)]
    [DataRow("007", 7u)]
    [DataRow("00042", 42u)]
    [DataRow("0000000", 0u)]
    public void StringAndSpanReadUnsignedValuesWithoutNarrowing(string token, uint expected)
    {
      Assert.AreEqual(expected, TextUtils.ParseUInt(token));
      Assert.AreEqual(expected, TextUtils.ParseUInt(token.AsSpan()));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" 42")]
    [DataRow("-42")]
    [DataRow("42.5")]
    [DataRow("abc")]
    [DataRow("12a34")]
    [DataRow("@#$")]
    [DataRow("+42")]
    [DataRow("1,234")]
    [DataRow("1 2")]
    [DataRow("0xFF")]
    [DataRow("4294967296")]
    public void InvalidUnsignedTokensUseTheFallbackThroughBothDoors(string? token)
    {
      Assert.AreEqual(uint.MaxValue, TextUtils.ParseUInt(token));
      Assert.AreEqual(uint.MaxValue, TextUtils.ParseUInt(token.AsSpan()));
      foreach (var fallback in new[] { 0u, 999u, 12345u })
      {
        Assert.AreEqual(fallback, TextUtils.ParseUInt(token, fallback));
        Assert.AreEqual(fallback, TextUtils.ParseUInt(token.AsSpan(), fallback));
      }
    }

    [TestMethod]
    [DataRow("50", 50.0)]
    [DataRow("-10", -10.0)]
    [DataRow("12.5", 12.5)]
    [DataRow("-3.14", -3.14)]
    [DataRow("0", 0.0)]
    [DataRow(".5", 0.5)]
    [DataRow("5.", 5.0)]
    [DataRow("+42", 42.0)]
    public void DecimalTokensKeepTheirSignAndFraction(string token, double expected)
    {
      Assert.AreEqual(expected, TextUtils.ParseDouble(token.AsSpan()), 1e-9);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("-")]
    [DataRow("abc")]
    [DataRow("12a34")]
    public void InvalidDecimalTokensUseNaNOrTheRequestedFallback(string? token)
    {
      Assert.IsTrue(double.IsNaN(TextUtils.ParseDouble(token.AsSpan())));
      Assert.AreEqual(99.0, TextUtils.ParseDouble(token.AsSpan(), 99.0));
    }
  }
}
