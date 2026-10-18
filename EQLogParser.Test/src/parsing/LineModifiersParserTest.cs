#nullable enable annotations

namespace EQLogParser
{
  [TestClass]
  public class LineModifiersParserTest
  {
    [TestMethod]
    [DataRow("Critical", 2)]
    [DataRow("Lucky Critical", 6)]
    [DataRow("Riposte Strikethrough", 48)]
    [DataRow("Riposte", 32)]
    [DataRow("Flurry", 1024)]
    [DataRow("Lucky Critical Flurry", 1030)]
    [DataRow("Twincast", 1)]
    [DataRow("Twincast Critical", 3)]
    [DataRow("Assassinate", 64)]
    [DataRow("Headshot", 128)]
    [DataRow("Double Bow Shot", 512)]
    [DataRow("Finishing Blow", 2050)]
    [DataRow("Slay Undead", 256)]
    [DataRow("Rampage", 8)]
    [DataRow("Wild Rampage", 8)]
    [DataRow("Crippling Blow", 2)]
    [DataRow("Deadly Strike", 2)]
    [DataRow("Strikethrough", 16)]
    [DataRow("", 0)]
    [DataRow(null, 0)]
    [DataRow("No Modifier", 0)]
    public void ModifierWordsAndPredicatesAgreeWithTheMask(string? words, int expected)
    {
      var mask = LineModifiersParser.BuildVector(words);
      Assert.AreEqual((short)expected, mask);
      Assert.AreEqual((expected & 1) != 0, LineModifiersParser.IsTwincast(mask));
      Assert.AreEqual((expected & 2) != 0, LineModifiersParser.IsCrit(mask));
      Assert.AreEqual((expected & 4) != 0, LineModifiersParser.IsLucky(mask));
      Assert.AreEqual((expected & 8) != 0, LineModifiersParser.IsRampage(mask));
      Assert.AreEqual((expected & 16) != 0, LineModifiersParser.IsStrikethrough(mask));
      Assert.AreEqual((expected & 32) != 0 && (expected & 16) == 0, LineModifiersParser.IsRiposte(mask));
      Assert.AreEqual((expected & 64) != 0, LineModifiersParser.IsAssassinate(mask));
      Assert.AreEqual((expected & 128) != 0, LineModifiersParser.IsHeadshot(mask));
      Assert.AreEqual((expected & 256) != 0, LineModifiersParser.IsSlayUndead(mask));
      Assert.AreEqual((expected & 512) != 0, LineModifiersParser.IsDoubleBowShot(mask));
      Assert.AreEqual((expected & 1024) != 0, LineModifiersParser.IsFlurry(mask));
      Assert.AreEqual((expected & 2048) != 0, LineModifiersParser.IsFinishingBlow(mask));
    }

    [TestMethod]
    public void MaskMarksTakePrecedenceOverSpellNameMarks()
    {
      Assert.AreEqual(FctSpecial.Assassinate,
        LineModifiersParser.SpecialFor(LineModifiersParser.BuildVector("Assassinate Critical"), null));
      Assert.AreEqual(FctSpecial.Headshot,
        LineModifiersParser.SpecialFor(LineModifiersParser.BuildVector("Headshot"), null));
      Assert.AreEqual(FctSpecial.SlayUndead,
        LineModifiersParser.SpecialFor(LineModifiersParser.BuildVector("Slay Undead"), null));
      Assert.AreEqual(FctSpecial.FinishingBlow,
        LineModifiersParser.SpecialFor(LineModifiersParser.BuildVector("Finishing Blow Critical"), null));
      Assert.AreEqual(FctSpecial.None,
        LineModifiersParser.SpecialFor(LineModifiersParser.BuildVector("Critical Flurry"), "Incendiary"));
      Assert.AreEqual(FctSpecial.Assassinate,
        LineModifiersParser.SpecialFor(LineModifiersParser.BuildVector("Assassinate"), "Decapitation XVIII"));
    }

    [TestMethod]
    public void SpellMarksUseTheWholeLeadingSpellFamilyRatherThanASubstring()
    {
      foreach (var mask in new[] { LineModifiersParser.None, LineModifiersParser.Crit })
      {
        Assert.AreEqual(FctSpecial.Decapitation, LineModifiersParser.SpecialFor(mask, "Decapitation XVIII"));
        Assert.AreEqual(FctSpecial.ManaBurn, LineModifiersParser.SpecialFor(mask, "Mana Burn"));
        Assert.AreEqual(FctSpecial.ManaBurn, LineModifiersParser.SpecialFor(mask, "Mana Burn AB"));
        Assert.AreEqual(FctSpecial.LifeBurn, LineModifiersParser.SpecialFor(mask, "Life Burn"));
        Assert.AreEqual(FctSpecial.LifeBurn, LineModifiersParser.SpecialFor(mask, "Life Burn AB"));
      }
      foreach (var spell in new[] { "Greater Mana Burn Potion", "Burn Mana", "Crown of Stars", "Decrees of Kurn" })
      {
        Assert.AreEqual(FctSpecial.None, LineModifiersParser.SpecialFor(LineModifiersParser.None, spell));
      }
    }
  }
}
