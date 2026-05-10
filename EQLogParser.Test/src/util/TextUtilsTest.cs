using EQLogParser;
using System.Globalization;

namespace EQLogParserTest
{
  [TestClass]
  public class TextUtilsTest
  {
    [TestMethod]
    public void TestToUpper_SingleChar()
    {
      var result = TextUtils.CapitalizeFirst("hello");
      Assert.AreEqual("Hello", result);
    }

    [TestMethod]
    public void TestToUpper_MultiWordName()
    {
      var result = TextUtils.CapitalizeFirst("john doe");
      Assert.AreEqual("John doe", result);
    }

    [TestMethod]
    public void TestToUpper_AlreadyUpper()
    {
      var result = TextUtils.CapitalizeFirst("HELLO");
      Assert.AreEqual("HELLO", result);
    }

    [TestMethod]
    public void TestToUpper_MixedCase()
    {
      var result = TextUtils.CapitalizeFirst("hELLO");
      Assert.AreEqual("HELLO", result);
    }

    [TestMethod]
    public void TestToUpper_EmptyString()
    {
      var result = TextUtils.CapitalizeFirst("");
      Assert.AreEqual("", result);
    }

    [TestMethod]
    public void TestToUpper_Null()
    {
      var result = TextUtils.CapitalizeFirst(null);
      Assert.IsNull(result);
    }

    [TestMethod]
    public void TestToUpper_SingleLetter()
    {
      var result = TextUtils.CapitalizeFirst("a");
      Assert.AreEqual("A", result);
    }

    [TestMethod]
    public void TestToUpper_SpecialChars()
    {
      var result = TextUtils.CapitalizeFirst("test-name_123");
      Assert.AreEqual("Test-name_123", result);
    }

    [TestMethod]
    public void TestToUpper_CultureInvariant()
      => Assert.AreEqual("I", TextUtils.CapitalizeFirst("i", CultureInfo.InvariantCulture));

    [TestMethod]
    public void TestToUpper_CultureSpecific()
    {
      var turkish = CultureInfo.GetCultureInfo("tr-TR");
      var result = TextUtils.CapitalizeFirst("i", turkish);
      Assert.AreEqual("\u0130", result);
    }

    [TestMethod]
    public void TestToUpper_MultiCharMiddleLower()
    {
      var result = TextUtils.CapitalizeFirst("aBCdEF");
      Assert.AreEqual("ABCdEF", result);
    }

    [TestMethod]
    public void TestToUpper_NumericStart()
    {
      var result = TextUtils.CapitalizeFirst("123abc");
      Assert.AreEqual("123abc", result);
    }

    [TestMethod]
    public void TestToUpper_SpaceStart()
    {
      var result = TextUtils.CapitalizeFirst(" hello");
      Assert.AreEqual(" hello", result);
    }

    [TestMethod]
    public void TestToUpper_LongName()
    {
      var result = TextUtils.CapitalizeFirst("susarrak the crusader");
      Assert.AreEqual("Susarrak the crusader", result);
    }

    [TestMethod]
    public void TestToUpper_AllLowercaseName()
    {
      var result = TextUtils.CapitalizeFirst("an ice giant");
      Assert.AreEqual("An ice giant", result);
    }

    [TestMethod]
    public void TestToUpper_PetOwnerName()
    {
      var result = TextUtils.CapitalizeFirst("lobekn");
      Assert.AreEqual("Lobekn", result);
    }

    [TestMethod]
    public void TestToUpper_ApostropheName()
    {
      var result = TextUtils.CapitalizeFirst("kizante`s");
      Assert.AreEqual("Kizante`s", result);
    }

    [TestMethod]
    public void TestToUpper_CommaName()
    {
      var result = TextUtils.CapitalizeFirst("ogna, artisan of war");
      Assert.AreEqual("Ogna, artisan of war", result);
    }

    [TestMethod]
    public void TestToUpper_DotName()
    {
      var result = TextUtils.CapitalizeFirst("test.test");
      Assert.AreEqual("Test.test", result);
    }

    [TestMethod]
    public void TestToUpper_BacktickName()
    {
      var result = TextUtils.CapitalizeFirst("tuyen`s");
      Assert.AreEqual("Tuyen`s", result);
    }
  }
}
