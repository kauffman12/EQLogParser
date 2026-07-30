using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class TextUtilsParseUIntTest
  {
    #region ParseUInt From String Tests

    [TestMethod]
    public void TestParseUInt_String_SingleDigit()
    {
      var result = TextUtils.ParseUInt("5");
      Assert.AreEqual(5u, result);
    }

    [TestMethod]
    public void TestParseUInt_String_TwoDigits()
    {
      var result = TextUtils.ParseUInt("42");
      Assert.AreEqual(42u, result);
    }

    [TestMethod]
    public void TestParseUInt_String_ThreeDigits()
    {
      var result = TextUtils.ParseUInt("123");
      Assert.AreEqual(123u, result);
    }

    [TestMethod]
    public void TestParseUInt_String_LargeNumber()
    {
      var result = TextUtils.ParseUInt("4294967295");
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_Zero()
    {
      var result = TextUtils.ParseUInt("0");
      Assert.AreEqual(0u, result);
    }

    [TestMethod]
    public void TestParseUInt_String_Empty_ReturnsDefaultValue()
    {
      var result = TextUtils.ParseUInt("");
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_Null_ReturnsDefaultValue()
    {
      var result = TextUtils.ParseUInt(null!);
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_WithSpaces_ReturnsDefaultValue()
    {
      var result = TextUtils.ParseUInt(" 42");
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_LeadingZero()
    {
      var result = TextUtils.ParseUInt("007");
      Assert.AreEqual(7u, result);
    }

    #endregion

    #region ParseUInt From ReadOnlySpan Tests

    [TestMethod]
    public void TestParseUInt_Span_SingleDigit()
    {
      var span = "5".AsSpan();
      var result = TextUtils.ParseUInt(span);
      Assert.AreEqual(5u, result);
    }

    [TestMethod]
    public void TestParseUInt_Span_TwoDigits()
    {
      var span = "42".AsSpan();
      var result = TextUtils.ParseUInt(span);
      Assert.AreEqual(42u, result);
    }

    [TestMethod]
    public void TestParseUInt_Span_ThreeDigits()
    {
      var span = "123".AsSpan();
      var result = TextUtils.ParseUInt(span);
      Assert.AreEqual(123u, result);
    }

    [TestMethod]
    public void TestParseUInt_Span_LargeNumber()
    {
      var span = "4294967295".AsSpan();
      var result = TextUtils.ParseUInt(span);
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_Span_Zero()
    {
      var span = "0".AsSpan();
      var result = TextUtils.ParseUInt(span);
      Assert.AreEqual(0u, result);
    }

    [TestMethod]
    public void TestParseUInt_Span_Empty_ReturnsDefaultValue()
    {
      var span = "".AsSpan();
      var result = TextUtils.ParseUInt(span);
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_Span_CustomDefaultValue()
    {
      var span = "abc".AsSpan();
      var result = TextUtils.ParseUInt(span, 999u);
      Assert.AreEqual(999u, result);
    }

    #endregion

    #region ParseUInt Invalid Input Tests

    [TestMethod]
    public void TestParseUInt_String_NegativeSign_ReturnsDefaultValue()
    {
      var result = TextUtils.ParseUInt("-42");
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_DecimalPoint_ReturnsDefaultValue()
    {
      var result = TextUtils.ParseUInt("42.5");
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_Letter_ReturnsDefaultValue()
    {
      var result = TextUtils.ParseUInt("abc");
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_MixedLettersAndDigits_ReturnsDefaultValue()
    {
      var result = TextUtils.ParseUInt("12a34");
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_SpecialChar_ReturnsDefaultValue()
    {
      var result = TextUtils.ParseUInt("@#$");
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_PlusSign_ReturnsDefaultValue()
    {
      var result = TextUtils.ParseUInt("+42");
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_Comma_ReturnsDefaultValue()
    {
      var result = TextUtils.ParseUInt("1,234");
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_SpaceInMiddle_ReturnsDefaultValue()
    {
      var result = TextUtils.ParseUInt("1 2");
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_HexPrefix_ReturnsDefaultValue()
    {
      var result = TextUtils.ParseUInt("0xFF");
      Assert.AreEqual(uint.MaxValue, result);
    }

    #endregion

    #region ParseUInt Edge Cases

    [TestMethod]
    public void TestParseUInt_String_LeadingZeros()
    {
      var result = TextUtils.ParseUInt("00042");
      Assert.AreEqual(42u, result);
    }

    [TestMethod]
    public void TestParseUInt_String_ManyZeros()
    {
      var result = TextUtils.ParseUInt("0000000");
      Assert.AreEqual(0u, result);
    }

    [TestMethod]
    public void TestParseUInt_Span_EmptySpan_ReturnsDefaultValue()
    {
      ReadOnlySpan<char> span = default;
      var result = TextUtils.ParseUInt(span);
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_MaxValue_StillValid()
    {
      var result = TextUtils.ParseUInt(uint.MaxValue.ToString());
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_OnePastMax_ReturnsDefaultValue()
    {
      // 4294967296 is one past uint.MaxValue
      var result = TextUtils.ParseUInt("4294967296");
      // The custom parser returns the actual parsed value, not overflow
      // Since it parses digit by digit and stops at the end, it should still work
      // But since it exceeds uint, the behavior depends on the implementation
      // The custom parser builds it as uint, so it will wrap or return default
      Assert.AreEqual(uint.MaxValue, result);
    }

    #endregion

    #region ParseUInt With Custom Default

    [TestMethod]
    public void TestParseUInt_String_CustomDefault_Zero()
    {
      var result = TextUtils.ParseUInt("abc", 0u);
      Assert.AreEqual(0u, result);
    }

    [TestMethod]
    public void TestParseUInt_String_CustomDefault_ForceMax()
    {
      var result = TextUtils.ParseUInt("xyz", uint.MaxValue);
      Assert.AreEqual(uint.MaxValue, result);
    }

    [TestMethod]
    public void TestParseUInt_String_CustomDefault_CustomValue()
    {
      var result = TextUtils.ParseUInt("invalid", 12345u);
      Assert.AreEqual(12345u, result);
    }

    #endregion

    #region ParseDouble Tests

    [TestMethod]
    public void TestParseDouble_PositiveInteger()
    {
      var result = TextUtils.ParseDouble("50".AsSpan());
      Assert.AreEqual(50.0, result);
    }

    [TestMethod]
    public void TestParseDouble_NegativeInteger()
    {
      var result = TextUtils.ParseDouble("-10".AsSpan());
      Assert.AreEqual(-10.0, result);
    }

    [TestMethod]
    public void TestParseDouble_PositiveDecimal()
    {
      var result = TextUtils.ParseDouble("12.5".AsSpan());
      Assert.AreEqual(12.5, result);
    }

    [TestMethod]
    public void TestParseDouble_NegativeDecimal()
    {
      var result = TextUtils.ParseDouble("-3.14".AsSpan());
      Assert.AreEqual(-3.14, result);
    }

    [TestMethod]
    public void TestParseDouble_Zero()
    {
      var result = TextUtils.ParseDouble("0".AsSpan());
      Assert.AreEqual(0.0, result);
    }

    [TestMethod]
    public void TestParseDouble_LeadingDecimalPoint()
    {
      var result = TextUtils.ParseDouble(".5".AsSpan());
      Assert.AreEqual(0.5, result);
    }

    [TestMethod]
    public void TestParseDouble_TrailingDecimalPoint()
    {
      var result = TextUtils.ParseDouble("5.".AsSpan());
      Assert.AreEqual(5.0, result);
    }

    [TestMethod]
    public void TestParseDouble_PlusSign()
    {
      var result = TextUtils.ParseDouble("+42".AsSpan());
      Assert.AreEqual(42.0, result);
    }

    [TestMethod]
    public void TestParseDouble_Empty_ReturnsNaN()
    {
      var result = TextUtils.ParseDouble("".AsSpan());
      Assert.IsTrue(double.IsNaN(result));
    }

    [TestMethod]
    public void TestParseDouble_NullString_ReturnsNaN()
    {
      var result = TextUtils.ParseDouble(ReadOnlySpan<char>.Empty);
      Assert.IsTrue(double.IsNaN(result));
    }

    [TestMethod]
    public void TestParseDouble_LoneMinusSign_ReturnsNaN()
    {
      var result = TextUtils.ParseDouble("-".AsSpan());
      Assert.IsTrue(double.IsNaN(result));
    }

    [TestMethod]
    public void TestParseDouble_NonNumeric_ReturnsNaN()
    {
      var result = TextUtils.ParseDouble("abc".AsSpan());
      Assert.IsTrue(double.IsNaN(result));
    }

    [TestMethod]
    public void TestParseDouble_MixedAlphaNumeric_ReturnsNaN()
    {
      var result = TextUtils.ParseDouble("12a34".AsSpan());
      Assert.IsTrue(double.IsNaN(result));
    }

    [TestMethod]
    public void TestParseDouble_CustomDefault()
    {
      var result = TextUtils.ParseDouble("abc".AsSpan(), 99.0);
      Assert.AreEqual(99.0, result);
    }

    #endregion
  }
}
