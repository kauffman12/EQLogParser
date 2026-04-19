using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class LineModifiersParserTest
  {
    [TestMethod]
    public void TestParseDamage_Critical()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Critical", 0, true);
      Assert.AreEqual(LineModifiersParser.Crit, result);
      Assert.IsTrue(LineModifiersParser.IsCrit(result));
      Assert.IsFalse(LineModifiersParser.IsTwincast(result));
      Assert.IsFalse(LineModifiersParser.IsLucky(result));
    }

    [TestMethod]
    public void TestParseDamage_LuckyCritical()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Lucky Critical", 0, true);
      Assert.IsTrue(LineModifiersParser.IsCrit(result));
      Assert.IsTrue(LineModifiersParser.IsLucky(result));
      Assert.IsFalse(LineModifiersParser.IsTwincast(result));
    }

    [TestMethod]
    public void TestParseDamage_RiposteStrikethrough()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Riposte Strikethrough", 0, true);
      Assert.IsTrue(LineModifiersParser.IsRiposte(result));
      Assert.IsTrue(LineModifiersParser.IsStrikethrough(result));
    }

    [TestMethod]
    public void TestParseDamage_Flurry()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Flurry", 0, true);
      Assert.IsTrue(LineModifiersParser.IsFlurry(result));
      Assert.IsFalse(LineModifiersParser.IsCrit(result));
    }

    [TestMethod]
    public void TestParseDamage_LuckyCriticalFlurry()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Lucky Critical Flurry", 0, true);
      Assert.IsTrue(LineModifiersParser.IsCrit(result));
      Assert.IsTrue(LineModifiersParser.IsLucky(result));
      Assert.IsTrue(LineModifiersParser.IsFlurry(result));
      Assert.IsFalse(LineModifiersParser.IsTwincast(result));
    }

    [TestMethod]
    public void TestParseDamage_Twincast()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Twincast", 0, true);
      Assert.IsTrue(LineModifiersParser.IsTwincast(result));
      Assert.IsFalse(LineModifiersParser.IsCrit(result));
    }

    [TestMethod]
    public void TestParseDamage_TwincastCritical()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Twincast Critical", 0, true);
      Assert.IsTrue(LineModifiersParser.IsTwincast(result));
      Assert.IsTrue(LineModifiersParser.IsCrit(result));
    }

    [TestMethod]
    public void TestParseDamage_Assassinate()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Assassinate", 0, true);
      Assert.IsTrue(LineModifiersParser.IsAssassinate(result));
    }

    [TestMethod]
    public void TestParseDamage_Headshot()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Headshot", 0, true);
      Assert.IsTrue(LineModifiersParser.IsHeadshot(result));
    }

    [TestMethod]
    public void TestParseDamage_DoubleBowShot()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Double Bow Shot", 0, true);
      Assert.IsTrue(LineModifiersParser.IsDoubleBowShot(result));
    }

    [TestMethod]
    public void TestParseDamage_FinishingBlow()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Finishing Blow", 0, true);
      Assert.IsTrue(LineModifiersParser.IsFinishingBlow(result));
    }

    [TestMethod]
    public void TestParseDamage_SlayUndead()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Slay Undead", 0, true);
      Assert.IsTrue(LineModifiersParser.IsSlayUndead(result));
    }

    [TestMethod]
    public void TestParseDamage_Rampage()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Rampage", 0, true);
      Assert.IsTrue(LineModifiersParser.IsRampage(result));
    }

    [TestMethod]
    public void TestParseDamage_WildRampage()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Wild Rampage", 0, true);
      Assert.IsTrue(LineModifiersParser.IsRampage(result));
    }

    [TestMethod]
    public void TestParseDamage_CripplingBlow()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Crippling Blow", 0, true);
      Assert.IsTrue(LineModifiersParser.IsCrit(result));
    }

    [TestMethod]
    public void TestParseDamage_DeadlyStrike()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Deadly Strike", 0, true);
      Assert.IsTrue(LineModifiersParser.IsCrit(result));
    }

    [TestMethod]
    public void TestParseDamage_StrikethroughOnly()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Strikethrough", 0, true);
      Assert.IsTrue(LineModifiersParser.IsStrikethrough(result));
      Assert.IsFalse(LineModifiersParser.IsRiposte(result));
    }

    [TestMethod]
    public void TestParseDamage_EmptyString()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "", 0, true);
      Assert.AreEqual(LineModifiersParser.None, result);
    }

    [TestMethod]
    public void TestParseDamage_NullModifiers()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", null!, 0, true);
      Assert.AreEqual(LineModifiersParser.None, result);
    }

    [TestMethod]
    public void TestParseDamage_NoModifiers()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "No Modifier", 0, true);
      Assert.AreEqual(0, result);
    }

    [TestMethod]
    public void TestParseHeal_Twincast()
    {
      var result = LineModifiersParser.ParseHeal("TestPlayer", "Twincast", 0);
      Assert.IsTrue(LineModifiersParser.IsTwincast(result));
    }

    [TestMethod]
    public void TestParseHeal_Critical()
    {
      var result = LineModifiersParser.ParseHeal("TestPlayer", "Critical", 0);
      Assert.IsTrue(LineModifiersParser.IsCrit(result));
    }

    [TestMethod]
    public void TestParseHeal_LuckyCritical()
    {
      var result = LineModifiersParser.ParseHeal("TestPlayer", "Lucky Critical", 0);
      Assert.IsTrue(LineModifiersParser.IsCrit(result));
      Assert.IsTrue(LineModifiersParser.IsLucky(result));
    }

    [TestMethod]
    public void TestMaskCache_CacheHit()
    {
      var modifiers = "Lucky Critical Flurry";
      var first = LineModifiersParser.ParseDamage("TestPlayer", modifiers, 0, true);
      var second = LineModifiersParser.ParseDamage("TestPlayer", modifiers, 1, true);
      Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void TestRiposteWithStrikethrough()
    {
      var result = LineModifiersParser.ParseDamage("TestPlayer", "Riposte Strikethrough", 0, true);
      Assert.IsTrue(LineModifiersParser.IsStrikethrough(result));
      Assert.IsTrue(LineModifiersParser.IsRiposte(result));
    }

    [TestMethod]
    public void TestRealLogModifiers()
    {
      var result1 = LineModifiersParser.ParseDamage("Nniki", "Lucky Critical Flurry", 0, true);
      Assert.IsTrue(LineModifiersParser.IsCrit(result1));
      Assert.IsTrue(LineModifiersParser.IsLucky(result1));
      Assert.IsTrue(LineModifiersParser.IsFlurry(result1));

      var result2 = LineModifiersParser.ParseDamage("An ice giant", "Riposte Strikethrough", 0, false);
      Assert.IsTrue(LineModifiersParser.IsRiposte(result2));
      Assert.IsTrue(LineModifiersParser.IsStrikethrough(result2));

      var result3 = LineModifiersParser.ParseDamage("Carblis", "Lucky Critical", 0, true);
      Assert.IsTrue(LineModifiersParser.IsCrit(result3));
      Assert.IsTrue(LineModifiersParser.IsLucky(result3));
    }
  }
}
