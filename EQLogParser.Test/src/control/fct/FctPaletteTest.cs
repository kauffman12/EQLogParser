namespace EQLogParser;

/*
 * The seven-hue dial (FctPalette) and the hex text settings.ini speaks. What is pinned: the palette starts at the shipped
 * table (the constants remain the law, the dial is its agent), the round-trip survives hand-editing — '#', dropped alpha —
 * and nonsense lands on shipped rather than reaching the canvas as garbage, same pure-parse law as every other FCT key.
 * The dial is a process global like FctScale, so the class resets through FctAmbient and the assembly's DoNotParallelize stands.
 */
[TestClass]
public class FctPaletteTest
{
  [TestInitialize]
  public void TestInitialize() => FctAmbient.Reset();

  [TestMethod]
  public void ThePaletteStartsAtTheShippedTable()
  {
    Assert.AreEqual(FctStyle.DamageDealtArgb, FctPalette.DamageDealt, "dealt damage starts yellow, whoever has run what before");
    Assert.AreEqual(FctStyle.DamageTakenArgb, FctPalette.DamageTaken, "taken damage starts red");
    Assert.AreEqual(FctStyle.HealingArgb, FctPalette.Healing, "healing starts green");
    Assert.AreEqual(FctStyle.CritArgb, FctPalette.Crit, "the crit class starts deep orange");
    Assert.AreEqual(FctStyle.SpecialArgb, FctPalette.Special, "marks start epic purple");
    Assert.AreEqual(FctStyle.WordsArgb, FctPalette.Words, "every word shares the one pale");
    Assert.AreEqual(FctStyle.SourceArgb, FctPalette.Source, "labels start neutral grey");
  }

  [TestMethod]
  public void ColoursRoundTripThroughSettingsText()
  {
    foreach (var shipped in new[]
             {
               FctStyle.DamageDealtArgb, FctStyle.DamageTakenArgb, FctStyle.HealingArgb, FctStyle.CritArgb,
               FctStyle.SpecialArgb, FctStyle.WordsArgb, FctStyle.SourceArgb,
             })
    {
      Assert.AreEqual(shipped, FctOverlaySettings.ParseColor(FctOverlaySettings.ColorText(shipped), 0),
        $"what the panel writes must load back as what it wrote (not {shipped:X8})");
    }

    /* The hand-edit courtesies: a leading '#' and a dropped alpha both mean the same colour, because settings.ini is
       furniture players open in an editor, not a binary. */
    Assert.AreEqual(unchecked((int)0xFFFFD75E), FctOverlaySettings.ParseColor("#FFD75E", 0), "six digits are opaque");
    Assert.AreEqual(unchecked((int)0xFF7FE061), FctOverlaySettings.ParseColor("ff7fe061", 0), "lowercase hex loads too");
    Assert.AreEqual(unchecked((int)0x80FF0000), FctOverlaySettings.ParseColor(" #80FF0000 ", 0), "and stray whitespace");
  }

  [TestMethod]
  public void NonsenseColoursLandOnShipped()
  {
    foreach (var junk in new[] { null, "", "red", "12345", "GGHHIIJK", "FFD75" })
    {
      Assert.AreEqual(FctStyle.CritArgb, FctOverlaySettings.ParseColor(junk, FctStyle.CritArgb),
        $"'{junk}' is not a colour the canvas should ever be asked to draw");
    }
  }

  [TestMethod]
  public void StagedStateCarriesTheHuesAndResetFindsTheWayBack()
  {
    var state = new FctConfigState { ColorHealing = unchecked((int)0xFF00FF00), ColorWords = unchecked((int)0xFF123456) };

    var clone = state.Clone();
    Assert.AreEqual(state.ColorHealing, clone.ColorHealing, "a preview carries hues like any other staged value");
    Assert.AreEqual(state.ColorWords, clone.ColorWords, "every one of the seven — Clone is the panel's way across the window boundary");

    FctPalette.Apply(state);
    Assert.AreEqual(unchecked((int)0xFF00FF00), FctPalette.Healing, "applying staged state moves the dial");

    FctPalette.Reset();
    Assert.AreEqual(FctStyle.HealingArgb, FctPalette.Healing, "and Reset is the way back — a default state IS the shipped palette");
    Assert.AreEqual(FctStyle.WordsArgb, FctPalette.Words, "for all seven, from one place");
  }
}
