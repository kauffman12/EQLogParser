namespace EQLogParser;

[TestClass]
public class CharmSpellDataTest
{
  [TestMethod]
  public void SpaFlagBuildsMessagesAndIncludesMatchingNonCharmVariants()
  {
    PipelineHarness.EnsureDataStore();
    var cwd = Environment.CurrentDirectory;
    var folder = Directory.CreateTempSubdirectory("charm-spells-").FullName;
    try
    {
      Directory.CreateDirectory(Path.Combine(folder, "data"));
      File.WriteAllLines(Path.Combine(folder, "data", "spells.txt"),
      [
        Spell("1", "New Captivation", true, " moans."),
        Spell("2", "Not A Charm", false, " moans."),
        Spell("3", "Shared Captivation", true, " moans."),
        Spell("4", "Shared Captivation", false, " moans."),
        Spell("5", "Charm XVII", false, " has been charmed."),
        Spell("6", "Empty Message Charm", true, ""),
        Spell("7", "New Pact", true, " has been bound by an unbreakable pact."),
        Spell("8", "New Servitude", true, " becomes a servant of nature."),
        Spell("9", "New Possession", true, "'s eyes glaze over.")
      ]);
      Environment.CurrentDirectory = folder;
      var store = new EQDataStore();
      Assert.IsTrue(store.IsCharmSpell("New Captivation"));
      Assert.IsTrue(store.IsCharmSpell("new pact"));
      Assert.IsTrue(store.IsCharmSpell("Empty Message Charm"));
      Assert.IsFalse(store.IsCharmSpell("Charm XVII"), "the message/name never substitutes for SPA 22");
      Assert.IsFalse(store.TryGetCharmLanding("Creature has been charmed.", out _, out _));
      Assert.IsTrue(store.TryGetCharmLanding("Creature moans.", out var target, out var message));
      Assert.AreEqual("Creature", target);
      Assert.IsTrue(store.TryMatchCharmCast("New Captivation", message, out var charm));
      Assert.IsTrue(charm);
      Assert.IsTrue(store.TryMatchCharmCast("Not A Charm", message, out charm));
      Assert.IsFalse(charm);
      Assert.IsTrue(store.TryMatchCharmCast("Shared Captivation", message, out charm));
      Assert.IsFalse(charm, "same-name, same-message ambiguity must not invent charm");
      Assert.IsFalse(store.TryGetCharmLanding(" moans.", out _, out _), "a bare suffix has no target");
      Assert.IsFalse(store.TryGetCharmLanding("Creature cannot be charmed.", out _, out _));
      Assert.IsTrue(store.TryGetCharmLanding("Creature's eyes glaze over.", out target, out _));
      Assert.AreEqual("Creature", target, "possessive messages have no separating space");
    }
    finally
    {
      Environment.CurrentDirectory = cwd;
      Directory.Delete(folder, true);
    }
  }

  [TestMethod]
  public void TheShippedFormatHasTwentyOneColumnsAndTheGlyphMessagesStayAligned()
  {
    PipelineHarness.EnsureDataStore();
    foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "data", "spells.txt")))
    {
      var fields = line.Split('^');
      Assert.HasCount(21, fields, fields[0]);
      Assert.IsTrue(fields[15] is "0" or "1", fields[0]);
    }
    for (var id = 900001; id <= 900005; id++)
    {
      var rank = id - 900000;
      var name = "Glyph of Destruction " + new[] { "I", "II", "III", "IV", "V" }[rank - 1];
      var spell = EQDataStore.Instance.GetSpellByName(name);
      Assert.IsNotNull(spell);
      Assert.AreEqual(rank, (int)spell.Rank);
      Assert.IsFalse(spell.Charm);
      Assert.AreEqual("", spell.LandsOnYou);
      Assert.AreEqual(" is infused for destruction.", spell.LandsOnOther);
      Assert.AreEqual("Your Glyph of Destruction fades away.", spell.WearOff);
    }
  }

  private static string Spell(string id, string name, bool charm, string landsOnOther)
    => string.Join("^", id, name, "1", "60", "0", "0", "5", "0", "0", "0", "1", "0", "0", "0", "0",
      charm ? "1" : "0", "0", "0", "You have been charmed.", landsOnOther, "You are no longer charmed.");
}
