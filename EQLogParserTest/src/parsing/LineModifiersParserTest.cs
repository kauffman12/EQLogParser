using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class LineModifiersParserTest
  {
    [TestMethod]
    public void TestBuildVector_Critical()
    {
      var result = LineModifiersParser.BuildVector("Critical");
      Assert.AreEqual(LineModifiersParser.Crit, result);
      Assert.IsTrue(LineModifiersParser.IsCrit(result));
      Assert.IsFalse(LineModifiersParser.IsTwincast(result));
      Assert.IsFalse(LineModifiersParser.IsLucky(result));
    }

    [TestMethod]
    public void TestBuildVector_LuckyCritical()
    {
      var result = LineModifiersParser.BuildVector("Lucky Critical");
      Assert.IsTrue(LineModifiersParser.IsCrit(result));
      Assert.IsTrue(LineModifiersParser.IsLucky(result));
      Assert.IsFalse(LineModifiersParser.IsTwincast(result));
    }

    [TestMethod]
    public void TestBuildVector_RiposteStrikethrough()
    {
      var result = LineModifiersParser.BuildVector("Riposte Strikethrough");
      Assert.IsFalse(LineModifiersParser.IsRiposte(result));
      Assert.IsTrue(LineModifiersParser.IsStrikethrough(result));
    }

    [TestMethod]
    public void TestBuildVector_Flurry()
    {
      var result = LineModifiersParser.BuildVector("Flurry");
      Assert.IsTrue(LineModifiersParser.IsFlurry(result));
      Assert.IsFalse(LineModifiersParser.IsCrit(result));
    }

    [TestMethod]
    public void TestBuildVector_LuckyCriticalFlurry()
    {
      var result = LineModifiersParser.BuildVector("Lucky Critical Flurry");
      Assert.IsTrue(LineModifiersParser.IsCrit(result));
      Assert.IsTrue(LineModifiersParser.IsLucky(result));
      Assert.IsTrue(LineModifiersParser.IsFlurry(result));
      Assert.IsFalse(LineModifiersParser.IsTwincast(result));
    }

    [TestMethod]
    public void TestBuildVector_Twincast()
    {
      var result = LineModifiersParser.BuildVector("Twincast");
      Assert.IsTrue(LineModifiersParser.IsTwincast(result));
      Assert.IsFalse(LineModifiersParser.IsCrit(result));
    }

    [TestMethod]
    public void TestBuildVector_TwincastCritical()
    {
      var result = LineModifiersParser.BuildVector("Twincast Critical");
      Assert.IsTrue(LineModifiersParser.IsTwincast(result));
      Assert.IsTrue(LineModifiersParser.IsCrit(result));
    }

    [TestMethod]
    public void TestBuildVector_Assassinate()
    {
      var result = LineModifiersParser.BuildVector("Assassinate");
      Assert.IsTrue(LineModifiersParser.IsAssassinate(result));
    }

    [TestMethod]
    public void TestBuildVector_Headshot()
    {
      var result = LineModifiersParser.BuildVector("Headshot");
      Assert.IsTrue(LineModifiersParser.IsHeadshot(result));
    }

    [TestMethod]
    public void TestBuildVector_DoubleBowShot()
    {
      var result = LineModifiersParser.BuildVector("Double Bow Shot");
      Assert.IsTrue(LineModifiersParser.IsDoubleBowShot(result));
    }

    [TestMethod]
    public void TestBuildVector_FinishingBlow()
    {
      var result = LineModifiersParser.BuildVector("Finishing Blow");
      Assert.IsTrue(LineModifiersParser.IsFinishingBlow(result));
    }

    [TestMethod]
    public void TestBuildVector_SlayUndead()
    {
      var result = LineModifiersParser.BuildVector("Slay Undead");
      Assert.IsTrue(LineModifiersParser.IsSlayUndead(result));
    }

    [TestMethod]
    public void TestBuildVector_Rampage()
    {
      var result = LineModifiersParser.BuildVector("Rampage");
      Assert.IsTrue(LineModifiersParser.IsRampage(result));
    }

    [TestMethod]
    public void TestBuildVector_WildRampage()
    {
      var result = LineModifiersParser.BuildVector("Wild Rampage");
      Assert.IsTrue(LineModifiersParser.IsRampage(result));
    }

    [TestMethod]
    public void TestBuildVector_CripplingBlow()
    {
      var result = LineModifiersParser.BuildVector("Crippling Blow");
      Assert.IsTrue(LineModifiersParser.IsCrit(result));
    }

    [TestMethod]
    public void TestBuildVector_DeadlyStrike()
    {
      var result = LineModifiersParser.BuildVector("Deadly Strike");
      Assert.IsTrue(LineModifiersParser.IsCrit(result));
    }

    [TestMethod]
    public void TestBuildVector_StrikethroughOnly()
    {
      var result = LineModifiersParser.BuildVector("Strikethrough");
      Assert.IsTrue(LineModifiersParser.IsStrikethrough(result));
      Assert.IsFalse(LineModifiersParser.IsRiposte(result));
    }

    [TestMethod]
    public void TestBuildVector_EmptyString()
    {
      var result = LineModifiersParser.BuildVector("");
      Assert.AreEqual(0, result);
    }

    [TestMethod]
    public void TestBuildVector_NullModifiers()
    {
      var result = LineModifiersParser.BuildVector(null!);
      Assert.AreEqual(0, result);
    }

    [TestMethod]
    public void TestBuildVector_NoModifiers()
    {
      var result = LineModifiersParser.BuildVector("No Modifier");
      Assert.AreEqual(0, result);
    }

    [TestMethod]
    public void TestMaskCache_CacheHit()
    {
      var modifiers = "Lucky Critical Flurry";
      var first = LineModifiersParser.BuildVector(modifiers);
      var second = LineModifiersParser.BuildVector(modifiers);
      Assert.AreEqual(first, second);
    }
  }
}
