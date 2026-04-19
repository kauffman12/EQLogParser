using System;
using System.Globalization;
using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class TextUtilsTest
  {
    [TestMethod]
    public void TestToUpper_SingleChar()
    {
      var result = TextUtils.ToUpper("hello");
      Assert.AreEqual("Hello", result);
    }

    [TestMethod]
    public void TestToUpper_MultiWordName()
    {
      var result = TextUtils.ToUpper("john doe");
      Assert.AreEqual("John doe", result);
    }

    [TestMethod]
    public void TestToUpper_AlreadyUpper()
    {
      var result = TextUtils.ToUpper("HELLO");
      Assert.AreEqual("HELLO", result);
    }

    [TestMethod]
    public void TestToUpper_MixedCase()
    {
      var result = TextUtils.ToUpper("hELLO");
      Assert.AreEqual("HELLO", result);
    }

    [TestMethod]
    public void TestToUpper_EmptyString()
    {
      var result = TextUtils.ToUpper("");
      Assert.AreEqual("", result);
    }

    [TestMethod]
    public void TestToUpper_Null()
    {
      var result = TextUtils.ToUpper(null);
      Assert.IsNull(result);
    }

    [TestMethod]
    public void TestToUpper_SingleLetter()
    {
      var result = TextUtils.ToUpper("a");
      Assert.AreEqual("A", result);
    }

    [TestMethod]
    public void TestToUpper_SpecialChars()
    {
      var result = TextUtils.ToUpper("test-name_123");
      Assert.AreEqual("Test-name_123", result);
    }

    [TestMethod]
    public void TestToUpper_CultureInvariant()
      => Assert.AreEqual("I", TextUtils.ToUpper("i", CultureInfo.InvariantCulture));

    [TestMethod]
    public void TestToUpper_CultureSpecific()
    {
      var turkish = CultureInfo.GetCultureInfo("tr-TR");
      var result = TextUtils.ToUpper("i", turkish);
      Assert.AreEqual("\u0130", result);
    }

    [TestMethod]
    public void TestToUpper_MultiCharMiddleLower()
    {
      var result = TextUtils.ToUpper("aBCdEF");
      Assert.AreEqual("ABCdEF", result);
    }

    [TestMethod]
    public void TestToUpper_NumericStart()
    {
      var result = TextUtils.ToUpper("123abc");
      Assert.AreEqual("123abc", result);
    }

    [TestMethod]
    public void TestToUpper_SpaceStart()
    {
      var result = TextUtils.ToUpper(" hello");
      Assert.AreEqual(" hello", result);
    }

    [TestMethod]
    public void TestToUpper_LongName()
    {
      var result = TextUtils.ToUpper("susarrak the crusader");
      Assert.AreEqual("Susarrak the crusader", result);
    }

    [TestMethod]
    public void TestToUpper_AllLowercaseName()
    {
      var result = TextUtils.ToUpper("an ice giant");
      Assert.AreEqual("An ice giant", result);
    }

    [TestMethod]
    public void TestToUpper_PetOwnerName()
    {
      var result = TextUtils.ToUpper("lobekn");
      Assert.AreEqual("Lobekn", result);
    }

    [TestMethod]
    public void TestToUpper_ApostropheName()
    {
      var result = TextUtils.ToUpper("kizante`s");
      Assert.AreEqual("Kizante`s", result);
    }

    [TestMethod]
    public void TestToUpper_CommaName()
    {
      var result = TextUtils.ToUpper("ogna, artisan of war");
      Assert.AreEqual("Ogna, artisan of war", result);
    }

    [TestMethod]
    public void TestToUpper_DotName()
    {
      var result = TextUtils.ToUpper("test.test");
      Assert.AreEqual("Test.test", result);
    }

    [TestMethod]
    public void TestToUpper_BacktickName()
    {
      var result = TextUtils.ToUpper("tuyen`s");
      Assert.AreEqual("Tuyen`s", result);
    }
  }
}
