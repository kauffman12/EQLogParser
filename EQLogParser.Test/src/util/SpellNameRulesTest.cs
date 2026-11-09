namespace EQLogParser;

[TestClass]
public sealed class SpellNameRulesTest
{
  private static readonly string[] KnownNames =
  [
    "Ethereal Fire XIII", "Ethereal Fire XIII Rk. II", "Ethereal Fire XIII Rk. III",
    "Ethereal Fire XIV", "Ethereal Fire XIV Rk. II",
    "Burnout VI", "Burnout VI Rk. II", "Burnout VII", "Burnout VII Rk. III",
    "Spellbreaker's Mending XI Azia", "Spellbreaker's Mending XI Azia Rk. II",
    "Adrenaline Surge Effect XII Azia", "Adrenaline Surge Effect XII Beza", "Adrenaline Surge Effect XII Caza",
    "Blood Magic VIII (Azia)", "Blood Magic VIII (Beza)", "Blood Magic VIII (Caza)",
    "Companion's Blessing (Azia) XL", "Companion's Blessing (Azia) XLI",
    "Flames of Power Dena I", "Flames of Power Dena II", "Flames of Power Dena III"
  ];

  private static readonly SpellNameRules Rules = new(KnownNames);

  [TestMethod]
  [DataRow("Ethereal Fire XIII", "Ethereal Fire XIII", "")]
  [DataRow("Ethereal Fire XIII Rk. II", "Ethereal Fire XIII", "II")]
  [DataRow("Ethereal Fire XIII Rk. III", "Ethereal Fire XIII", "III")]
  [DataRow("Ethereal Fire XIV Rk. II", "Ethereal Fire XIV", "II")]
  [DataRow("Burnout VI", "Burnout VI", "")]
  [DataRow("Burnout VI Rk. II", "Burnout VI", "II")]
  [DataRow("Burnout VII Rk. III", "Burnout VII", "III")]
  [DataRow("Spellbreaker's Mending XI Azia", "Spellbreaker's Mending XI Azia", "")]
  [DataRow("Spellbreaker's Mending XI Azia Rk. II", "Spellbreaker's Mending XI Azia", "II")]
  [DataRow("Ethereal Skyblaze", "Ethereal Skyblaze", "")]
  [DataRow("Ethereal Skyblaze Rk. III", "Ethereal Skyblaze", "III")]
  [DataRow("Arcane Arietta Rk.II", "Arcane Arietta", "II")]
  [DataRow("Arcane Arietta RK.III", "Arcane Arietta", "III")]
  [DataRow("Illusion Benefit Vena Mk. II", "Illusion Benefit Vena", "II")]
  [DataRow("Shield Reflect Rk. III Effect", "Shield Reflect Effect", "III")]
  [DataRow("Aria of Absolution Rk. III (Self)", "Aria of Absolution (Self)", "III")]
  [DataRow("Future Spell XX Rk. III", "Future Spell XX", "III")]
  [DataRow("Spell (Rk. II)", "Spell", "II")]
  [DataRow("Spell Rk. 3", "Spell", "3")]
  [DataRow("Spell Rk. IIII", "Spell Rk. IIII", "")]
  [DataRow("Spell Rk.", "Spell Rk.", "")]
  [DataRow("SpellmarkIII", "SpellmarkIII", "")]
  [DataRow("Abundant Healing XVI", "Abundant Healing", "XVI")]
  [DataRow("Focus of Arcanum VI", "Focus of Arcanum", "VI")]
  [DataRow("Adrenaline Surge Effect XII Azia", "Adrenaline Surge Effect XII", "Azia")]
  [DataRow("Adrenaline Surge Effect XII Beza", "Adrenaline Surge Effect XII", "Beza")]
  [DataRow("Adrenaline Surge Effect XII Caza", "Adrenaline Surge Effect XII", "Caza")]
  [DataRow("Adrenaline Surge Effect XII", "Adrenaline Surge Effect XII", "")]
  [DataRow("Blood Magic VIII (Azia)", "Blood Magic VIII", "Azia")]
  [DataRow("Blood Magic VIII (Beza)", "Blood Magic VIII", "Beza")]
  [DataRow("Blood Magic VIII (Caza)", "Blood Magic VIII", "Caza")]
  [DataRow("Blood Magic VIII", "Blood Magic VIII", "")]
  [DataRow("Companion's Blessing (Azia) XL", "Companion's Blessing (Azia)", "XL")]
  [DataRow("Companion's Blessing (Azia)", "Companion's Blessing (Azia)", "")]
  [DataRow("Companion's Blessing I (Azia)", "Companion's Blessing I", "Azia")]
  [DataRow("Flames of Power Dena I", "Flames of Power Dena", "I")]
  [DataRow("Flames of Power Dena II", "Flames of Power Dena", "II")]
  [DataRow("Flames of Power Dena III", "Flames of Power Dena", "III")]
  [DataRow("Flames of Power Dena IX", "Flames of Power Dena", "IX")]
  [DataRow("Flames of Power Dena", "Flames of Power Dena", "")]
  [DataRow("Spell Rk. II Rk. III", "Spell Rk. II", "III")]
  [DataRow("Spell Rk. II Caza", "Spell Rk. II", "Caza")]
  [DataRow("Spell 100 0 100", "Spell 100 0", "100")]
  [DataRow("Mount Blessing Azia Push", "Mount Blessing Push", "Azia")]
  [DataRow("Anticipated Healing VII Beza", "Anticipated Healing VII", "Beza")]
  [DataRow("Spell Ena", "Spell", "Ena")]
  [DataRow("Spell Geza", "Spell", "Geza")]
  [DataRow("Spell Kala", "Spell", "Kala")]
  [DataRow("Spell Ozah", "Spell", "Ozah")]
  [DataRow("Spell Vena", "Spell", "Vena")]
  [DataRow("Reciprocal Roar Strike 6", "Reciprocal Roar Strike", "6")]
  [DataRow("Aeolian Third", "Aeolian Third", "")]
  [DataRow("Aeolian Fifth", "Aeolian Fifth", "")]
  [DataRow("Aeolian Octave", "Aeolian Octave", "")]
  [DataRow("Masterful Dorian Fifth", "Masterful Dorian Fifth", "")]
  [DataRow("Fifth Wind Rk. II", "Fifth Wind", "II")]
  [DataRow("Genesis", "Genesis", "")]
  [DataRow("Azia", "Azia", "")]
  [DataRow("III", "III", "")]
  [DataRow("Rk. III", "Rk. III", "")]
  [DataRow("", "", "")]
  public void NamesAndRanksHaveSeparateAnswers(string full, string name, string rank)
  {
    var split = Rules.Split(full);
    Assert.AreEqual(name, split.Name, full);
    Assert.AreEqual(rank, split.Rank, full);
  }

  [TestMethod]
  public void TheUnmarkedBaseDoesNotDependOnDataOrderOrWhichRankWasReadFirst()
  {
    var rules = new SpellNameRules(KnownNames.Reverse());
    Assert.AreEqual(new SpellNameParts("Ethereal Fire XIII", ""), rules.Split("Ethereal Fire XIII"));
    Assert.AreEqual(new SpellNameParts("Ethereal Fire XIII", "III"), rules.Split("Ethereal Fire XIII Rk. III"));
    Assert.AreEqual(new SpellNameParts("ethereal fire xiii", ""), rules.Split("ethereal fire xiii"));
    Assert.AreNotEqual(rules.Split("Ethereal Fire XIII").Name, rules.Split("Ethereal Fire XIV").Name);
    Assert.AreEqual(new SpellNameParts("Blood Magic VIII", ""), rules.Split("Blood Magic VIII"));
    Assert.AreEqual(new SpellNameParts("Blood Magic VIII", "Caza"), rules.Split("Blood Magic VIII (Caza)"));
  }

  [TestMethod]
  public void ExplicitRanksWorkWithoutAnyDataFile()
  {
    var rules = new SpellNameRules([]);
    Assert.AreEqual(new SpellNameParts("Future Spell XX", "III"), rules.Split("Future Spell XX Rk. III"));
  }
}
