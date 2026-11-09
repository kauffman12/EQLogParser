namespace EQLogParser;

[TestClass]
public sealed class SpellNameDataTest
{
  [TestMethod]
  [DataRow("Ethereal Fire XIII", "Ethereal Fire XIII", "", 1)]
  [DataRow("Ethereal Fire XIII Rk. II", "Ethereal Fire XIII", "II", 5)]
  [DataRow("Ethereal Fire XIII Rk. III", "Ethereal Fire XIII", "III", 10)]
  [DataRow("Tashan XVII", "Tashan XVII", "", 1)]
  [DataRow("Holy Remedy XIV Rk. III", "Holy Remedy XIV", "III", 10)]
  [DataRow("Burnout VI", "Burnout VI", "", 1)]
  [DataRow("Burnout VII Rk. II", "Burnout VII", "II", 5)]
  [DataRow("Fire Elemental Bolt XI Rk. VI", "Fire Elemental Bolt XI", "VI", 0)]
  [DataRow("Arcane Arietta Rk.II", "Arcane Arietta", "II", 5)]
  [DataRow("Shield Reflect Rk. III Effect", "Shield Reflect Effect", "III", 0)]
  [DataRow("Aria of Absolution Rk. III (Self)", "Aria of Absolution (Self)", "III", 0)]
  [DataRow("Gather Mana VIII", "Gather Mana", "VIII", 8)]
  [DataRow("Adrenaline Surge Effect XII Azia", "Adrenaline Surge Effect XII", "Azia", 0)]
  [DataRow("Adrenaline Surge Effect XII Beza", "Adrenaline Surge Effect XII", "Beza", 0)]
  [DataRow("Adrenaline Surge Effect XII Caza", "Adrenaline Surge Effect XII", "Caza", 0)]
  [DataRow("Blood Magic VIII (Azia)", "Blood Magic VIII", "Azia", 1)]
  [DataRow("Blood Magic VIII (Beza)", "Blood Magic VIII", "Beza", 2)]
  [DataRow("Blood Magic VIII (Caza)", "Blood Magic VIII", "Caza", 3)]
  [DataRow("Companion's Blessing (Azia) XL", "Companion's Blessing (Azia)", "XL", 40)]
  [DataRow("Flames of Power Dena IX", "Flames of Power Dena", "IX", 9)]
  [DataRow("Spellbreaker's Mending XI Azia", "Spellbreaker's Mending XI Azia", "", 1)]
  [DataRow("Spellbreaker's Mending XI Azia Rk. III", "Spellbreaker's Mending XI Azia", "III", 10)]
  public void ShippedDataKeepsItsNamesAndDoesNotMistakeTheMetadataForDisplayRank(
    string full, string name, string rank, int metadataRank)
  {
    var store = PipelineHarness.SharedDataStore();
    var spell = store.GetSpellByName(full);
    Assert.IsNotNull(spell, full);
    Assert.AreEqual(metadataRank, (int)spell.Rank, full);
    Assert.AreEqual(name, spell.NameAbbrv, full);
    Assert.AreEqual(new SpellNameParts(name, rank), store.SplitSpellName(full), full);
    Assert.IsNotNull(store.GetSpellByAbbrv(name), "damage classification must still find the family: " + full);
  }

  [TestMethod]
  public void EveryExplicitRankInTheShippedDataAgreesWithItsUnmarkedBase()
  {
    var store = PipelineHarness.SharedDataStore();
    var names = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "data", "spells.txt"))
      .Select(line => line.Split('^')[1]).Distinct().ToArray();
    var checkedNames = 0;
    foreach (var full in names)
    {
      var split = store.SplitSpellName(full);
      Assert.IsFalse(string.IsNullOrWhiteSpace(split.Name), full);
      Assert.IsFalse(split.Rank.Any(char.IsWhiteSpace), "only one rank identifier: " + full);
      Assert.AreEqual(split.Name, store.AbbreviateSpellName(split.Name), "normalizing twice: " + full);
      foreach (var rank in new[] { "II", "III" })
      {
        var suffix = " Rk. " + rank;
        if (!full.EndsWith(suffix, StringComparison.Ordinal)) continue;
        var expected = full[..^suffix.Length];
        Assert.AreEqual(expected, split.Name, full);
        Assert.AreEqual(rank, split.Rank, full);
        Assert.AreEqual(expected, store.AbbreviateSpellName(expected), "unmarked first rank: " + full);
        checkedNames++;
      }
    }
    Assert.IsGreaterThan(0, checkedNames, "the test must actually read the shipped spell data");
  }

  [TestMethod]
  [DataRow("Adrenaline Surge Effect XII Azia", "Adrenaline Surge Effect XII Beza",
    "Adrenaline Surge Effect XII Caza", "Adrenaline Surge Effect XII")]
  [DataRow("Blood Magic VIII (Azia)", "Blood Magic VIII (Beza)", "Blood Magic VIII (Caza)", "Blood Magic VIII")]
  [DataRow("Flames of Power Dena IX", "Flames of Power Dena X", "Flames of Power Dena XI", "Flames of Power Dena")]
  public void ThreeRankVariantsShareOneGroupingName(string first, string second, string third, string expected)
  {
    var store = PipelineHarness.SharedDataStore();
    foreach (var full in new[] { first, second, third })
    {
      var spell = store.GetSpellByName(full);
      Assert.IsNotNull(spell, full);
      Assert.AreEqual(expected, spell.NameAbbrv, full);
    }
    Assert.AreEqual(expected, store.SplitSpellName(expected).Name);
    Assert.AreEqual(string.Empty, store.SplitSpellName(expected).Rank, "never strip the resulting name again");
  }

  [TestMethod]
  public void LandingMessageAmbiguityStillCollapsesRanksButNotDifferentNumberedSpells()
  {
    var store = PipelineHarness.SharedDataStore();
    var comparer = new SpellAbbrvComparer();
    var first = store.GetSpellByName("Burnout VI");
    var ranked = store.GetSpellByName("Burnout VI Rk. III");
    var next = store.GetSpellByName("Burnout VII");
    Assert.IsNotNull(first);
    Assert.IsNotNull(ranked);
    Assert.IsNotNull(next);
    Assert.IsTrue(comparer.Equals(first, ranked));
    Assert.IsFalse(comparer.Equals(first, next));

    RecordsStore.Instance.Clear();
    try
    {
      var words = LineWords.Build("Testpet goes berserk.");
      Assert.IsTrue(store.TryGetLandsOnOther(words, words.Length - 1, out var found, out var player));
      Assert.AreEqual("Testpet", player);
      Assert.IsTrue(found.SpellData.Any(spell => spell.NameAbbrv == "Burnout VI"));
      Assert.IsTrue(found.SpellData.Any(spell => spell.NameAbbrv == "Burnout VII"),
        "a common landing message is not proof of which numbered spell was cast");
    }
    finally
    {
      RecordsStore.Instance.Clear();
    }
  }
}
