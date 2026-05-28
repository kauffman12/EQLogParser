using EQLogParser;
using System.Collections.Generic;

namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class HealingValidatorTest
  {
    private EQDataStore? _dataStore;

    [TestInitialize]
    public void Setup()
    {
      // Set current directory so EQDataStore can find data files (data/spells.txt, etc.)
      Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;

      // Create a fresh data store which loads from data files
      _dataStore = new EQDataStore();
      EQDataStore.Instance = _dataStore;
    }

    [TestMethod]
    public void IsValid_Lightweight_NullRecord_ReturnsFalse()
    {
      var validator = new HealingValidator(true, true);
      Assert.IsFalse(validator.IsValid(0, null, null));
    }

    [TestMethod]
    public void IsValid_Lightweight_SwarmPetsDisabled_RejectsPets()
    {
      var validator = new HealingValidator(true, false);
      var petHeal = new HealRecord { Healer = "Paladin", Healed = "Fenrir`s pet", Type = Labels.Heal, SubType = "Divine Favor" };
      Assert.IsFalse(validator.IsValid(0, petHeal, null));
    }

    [TestMethod]
    public void IsValid_Lightweight_SwarmPetsDisabled_RejectsWards()
    {
      var validator = new HealingValidator(true, false);
      var wardHeal = new HealRecord { Healer = "Paladin", Healed = "Fenrir`s ward", Type = Labels.Heal, SubType = "Divine Favor" };
      Assert.IsFalse(validator.IsValid(0, wardHeal, null));
    }

    [TestMethod]
    public void IsValid_Lightweight_SwarmPetsEnabled_AcceptsPets()
    {
      var validator = new HealingValidator(true, true);
      var petHeal = new HealRecord { Healer = "Paladin", Healed = "Fenrir`s pet", Type = Labels.Heal, SubType = "Divine Favor" };
      Assert.IsTrue(validator.IsValid(0, petHeal, null));
    }

    [TestMethod]
    public void IsValid_Full_AoeDisabled_RejectsTargetaeHeals()
    {
      var validator = new HealingValidator(false, true);
      var heal = new HealRecord { Healer = "Cleric", Healed = "Player", Type = Labels.Heal, SubType = "Wake of Tranquility" };
      Assert.IsFalse(validator.IsValid(0, heal, new(), new(), new()));
    }

    [TestMethod]
    public void IsValid_Full_AoeDisabled_RejectsNearbyplayersaeHeals()
    {
      var validator = new HealingValidator(false, true);
      var heal = new HealRecord { Healer = "Bard", Healed = "Player", Type = Labels.Heal, SubType = "Mass Elemental Transvergence" };
      Assert.IsFalse(validator.IsValid(0, heal, new(), new(), new()));
    }

    [TestMethod]
    public void IsValid_Full_AoeDisabled_RejectsTargetringaeHeals()
    {
      var validator = new HealingValidator(false, true);
      var heal = new HealRecord { Healer = "Cleric", Healed = "Player", Type = Labels.Heal, SubType = "Splash of Sanctification" };
      Assert.IsFalse(validator.IsValid(0, heal, new(), new(), new()));
    }

    [TestMethod]
    public void IsValid_Full_AoeDisabled_RejectsCasterpbplayersHeals()
    {
      var validator = new HealingValidator(false, true);
      var heal = new HealRecord { Healer = "Bard", Healed = "Player", Type = Labels.Heal, SubType = "Aura of Cleansing IV" };
      Assert.IsFalse(validator.IsValid(0, heal, new(), new(), new()));
    }

    [TestMethod]
    public void IsValid_Full_AoeDisabled_AcceptsSingleTargetHeals()
    {
      var validator = new HealingValidator(false, true);
      var heal = new HealRecord { Healer = "Cleric", Healed = "Player", Type = Labels.Heal, SubType = "Spirit of Persistence" };
      Assert.IsTrue(validator.IsValid(0, heal, new(), new(), new()));
    }

    [TestMethod]
    public void IsValid_Full_AoeEnabled_AcceptsWithoutSpellData()
    {
      var validator = new HealingValidator(true, true);
      var heal = new HealRecord { Healer = "Cleric", Healed = "Player", Type = Labels.Heal, SubType = "Unknown Spell" };
      Assert.IsTrue(validator.IsValid(0, heal, new(), new(), new()));
    }

    [TestMethod]
    public void IsValid_Full_NullRecord_ReturnsFalse()
    {
      var validator = new HealingValidator(true, true);
      Assert.IsFalse(validator.IsValid(0, null, new(), new(), new()));
    }

    [TestMethod]
    public void IsValid_Full_SwarmPetsDisabled_RejectsPets()
    {
      var validator = new HealingValidator(true, false);
      var petHeal = new HealRecord { Healer = "Paladin", Healed = "Fenrir`s pet", Type = Labels.Heal, SubType = "Divine Favor" };
      Assert.IsFalse(validator.IsValid(0, petHeal, new(), new(), new()));
    }

    [TestMethod]
    public void IsHealingLimited_AllEnabled_ReturnsFalse()
    {
      var validator = new HealingValidator(true, true);
      Assert.IsFalse(validator.IsHealingLimited());
    }

    [TestMethod]
    public void IsHealingLimited_AoeDisabled_ReturnsTrue()
    {
      var validator = new HealingValidator(false, true);
      Assert.IsTrue(validator.IsHealingLimited());
    }

    [TestMethod]
    public void IsHealingLimited_SwarmPetsDisabled_ReturnsTrue()
    {
      var validator = new HealingValidator(true, false);
      Assert.IsTrue(validator.IsHealingLimited());
    }

    [TestMethod]
    public void IsValid_DefaultConstructor_ReadsFromAppSettings()
    {
      // Just verify it doesn't throw with default constructor
      var validator = new HealingValidator();
      Assert.IsNotNull(validator);
    }
  }
}
