namespace EQLogParser;

[TestClass]
public class CharmSpellDataTest
{
  [TestMethod]
  public void CharmNamesComeFromEveryVariantsLandsOnOtherMessage()
  {
    PipelineHarness.EnsureDataStore();
    var cwd = Environment.CurrentDirectory;
    var folder = Directory.CreateTempSubdirectory("charm-spells-").FullName;
    try
    {
      Directory.CreateDirectory(Path.Combine(folder, "data"));
      File.WriteAllLines(Path.Combine(folder, "data", "spells.txt"),
      [
        Spell("1", "New Captivation", " has been charmed."),
        Spell("2", "Another Captivation", " is charmed."),
        Spell("3", "Shared Captivation", " has been charmed."),
        Spell("4", "Shared Captivation", " bursts into flames."),
        Spell("5", "Charm XVII", " looks dazed."),
        Spell("6", "Misleading Wearoff", " glows brightly.")
      ]);
      Environment.CurrentDirectory = folder;
      var store = new EQDataStore();
      Assert.IsTrue(store.IsCharmSpell("New Captivation"), "a new spell needs no hard-coded name entry");
      Assert.IsTrue(store.IsCharmSpell("another captivation"), "both charm messages and case variants count");
      Assert.IsTrue(store.IsCharmSpell("Shared Captivation"), "one same-named non-charm variant must not hide the charm variant");
      Assert.IsFalse(store.IsCharmSpell("Charm XVII"), "a formerly curated name does not override the data");
      Assert.IsFalse(store.IsCharmSpell("Misleading Wearoff"), "lands-on-you and wear-off are not lands-on-other");
      Assert.IsFalse(store.IsCharmSpell("Unknown Charm"));
      Assert.IsFalse(store.IsCharmSpell(null));
    }
    finally
    {
      Environment.CurrentDirectory = cwd;
      Directory.Delete(folder, true);
    }
  }

  [TestMethod]
  [DataRow(" has been charmed.", true)]
  [DataRow(" is charmed.", true)]
  [DataRow(" IS CHARMED. ", true)]
  [DataRow(" cannot be charmed.", false)]
  [DataRow(" is no longer charmed.", false)]
  [DataRow(" has been charmed by a stranger.", false)]
  [DataRow("", false)]
  [DataRow(null, false)]
  public void TheWholeLandsOnOtherMessageMustConfirmCharm(string? message, bool expected)
  {
    Assert.AreEqual(expected, CharmSpells.IsCharmMessage(message));
  }

  private static string Spell(string id, string name, string landsOnOther)
    => string.Join("^", id, name, "1", "60", "0", "0", "5", "0", "0", "0", "1", "0", "0", "0", "0",
      "0", "0", "You have been charmed.", landsOnOther, "You are no longer charmed.");
}
