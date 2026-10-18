namespace EQLogParser
{
  [DoNotParallelize]
  [TestClass]
  public class HealingValidatorTest
  {
    private Action _restore = () => { };

    [TestInitialize]
    public void Setup()
    {
      var cwd = Environment.CurrentDirectory;
      var lookup = CombatRecordLookup.HealingSpellByName;
      _restore = () =>
      {
        Environment.CurrentDirectory = cwd;
        CombatRecordLookup.HealingSpellByName = lookup;
      };
      Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
      var store = PipelineHarness.SharedDataStore();
      CombatRecordLookup.HealingSpellByName = store.GetHealingSpellByName;
    }

    [TestCleanup]
    public void Cleanup() => _restore();

    [TestMethod]
    [DataRow("Fenrir`s pet", false, false)]
    [DataRow("Fenrir`s ward", false, false)]
    [DataRow("Fenrir`s pet", true, true)]
    public void BothValidationDoorsRespectThePetSetting(string target, bool enabled, bool expected)
    {
      var validator = new HealingValidator(true, enabled);
      var heal = new HealRecord
      {
        Healer = "Paladin", Healed = target, Type = Labels.Heal, SubType = "Divine Favor"
      };
      Assert.AreEqual(expected, validator.IsValid(0, heal, null));
      Assert.AreEqual(expected, validator.IsValid(0, heal, new(), new(), new()));
    }

    [TestMethod]
    [DataRow("Blessing of Light", false, false)]
    [DataRow("Erollisi's Chorus", false, false)]
    [DataRow("Splash of Sanctification", false, false)]
    [DataRow("Aura of Greater Restoration", false, false)]
    [DataRow("Ahnkaul's Mending", false, true)]
    [DataRow("Unknown Spell", true, true)]
    public void TheFullDoorFiltersAeShapesOnlyWhenRequested(string spell, bool aoeEnabled, bool expected)
    {
      var validator = new HealingValidator(aoeEnabled, true);
      var heal = new HealRecord { Healer = "Cleric", Healed = "Player", Type = Labels.Heal, SubType = spell };
      Assert.AreEqual(expected, validator.IsValid(0, heal, new(), new(), new()));
    }

    [TestMethod]
    [DataRow(true, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, true)]
    public void LimitsReflectTheRequestedOptions(bool aoe, bool pets, bool expected)
    {
      Assert.AreEqual(expected, new HealingValidator(aoe, pets).IsHealingLimited());
    }

    [TestMethod]
    public void BothDoorsRefuseAMissingRecord()
    {
      var validator = new HealingValidator(true, true);
      Assert.IsFalse(validator.IsValid(0, null, null));
      Assert.IsFalse(validator.IsValid(0, null, new(), new(), new()));
    }

    [TestMethod]
    public void TheDefaultConstructorReadsTheCurrentOptions()
    {
      var aoe = AppSettings.IsAoEHealingEnabled;
      var pets = AppSettings.IsHealingSwarmPetsEnabled;
      try
      {
        AppSettings.IsAoEHealingEnabled = false;
        AppSettings.IsHealingSwarmPetsEnabled = false;
        var validator = new HealingValidator();
        Assert.IsTrue(validator.IsHealingLimited());
        Assert.IsFalse(validator.IsValid(0, new HealRecord { Healed = "Fenrir`s pet" }, null));
      }
      finally
      {
        AppSettings.IsAoEHealingEnabled = aoe;
        AppSettings.IsHealingSwarmPetsEnabled = pets;
      }
    }
  }
}
