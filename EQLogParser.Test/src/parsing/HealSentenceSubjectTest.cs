namespace EQLogParser;

[TestClass]
public sealed class HealSentenceSubjectTest
{
  [TestInitialize]
  public void Initialize()
  {
    HealTap.Clear();
    PlayerRegistry.Instance.Clear();
  }

  [TestCleanup]
  public void Cleanup()
  {
    HealTap.Clear();
    PlayerRegistry.Instance.Clear();
  }

  [TestMethod]
  [DataRow("Foob glows with holy light. Jazrakhan`s pet", "Jazrakhan`s pet")]
  [DataRow("Foob glows with holy light! A killer moth", "A killer moth")]
  [DataRow("Foob glows with holy light. Nymera`s ward", "Nymera")]
  [DataRow("Nymera`s ward breaks! Farzi", "Farzi")]
  [DataRow("Foob is soothed by Brell's Soothing Wave. Farzi", "Farzi")]
  [DataRow("Foob glows with holy light. The gods have", "The gods")]
  public void OnlyTheHealingSentencesActorReceivesCredit(string prefix, string healer)
  {
    Assert.IsTrue(HealingLineParser.Process(new LineData
    {
      Action = prefix + " healed Foob for 6109 hit points by Divine Interstition Rk. III.",
      BeginTime = FixtureTime.Base
    }));
    var record = HealTap.All().Single().Item2;
    Assert.AreEqual(healer, record.Healer);
    Assert.AreEqual("Foob", record.Healed);
    Assert.AreEqual(6109u, record.Total);
  }

  [TestMethod]
  public void ALongProcPreambleCannotTripTheHealerNameLengthGuard()
  {
    var prefix = new string('x', 100) + ". Jazrakhan`s pet";
    OnlyTheHealingSentencesActorReceivesCredit(prefix, "Jazrakhan`s pet");
  }

  [TestMethod]
  [DataRow("Foob glows. Bastion of Divinity Rk. II")]
  [DataRow("Foob glows! Bastion of Divinity")]
  [DataRow("Foob glows. Foob is")]
  [DataRow("Foob glows. Foob has been")]
  public void PreamblesDoNotInventActorsForSpellSubjectsOrPassives(string prefix)
  {
    Assert.IsFalse(HealingLineParser.Process(new LineData
    {
      Action = prefix + " healed Foob over time for 6670 hit points by Bastion of Divinity Effect II.",
      BeginTime = FixtureTime.Base
    }));
    Assert.HasCount(0, HealTap.All());
  }
}
