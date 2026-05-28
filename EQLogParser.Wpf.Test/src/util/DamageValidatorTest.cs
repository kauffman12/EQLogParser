using EQLogParser;

namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class DamageValidatorTest
  {
    [TestMethod]
    public void IsValid_AllEnabled_AcceptsAllDamageTypes()
    {
      var validator = new DamageValidator(true, true, true, true, true, true);
      Assert.IsTrue(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 0)));
      Assert.IsTrue(validator.IsValid(CreateDamageRecord(type: Labels.Bane, mask: 0)));
      Assert.IsTrue(validator.IsValid(CreateDamageRecord(type: Labels.Ds, mask: 0)));
      Assert.IsTrue(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 64)));   // assassinate
      Assert.IsTrue(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 128)));  // headshot
      Assert.IsTrue(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 2048))); // finishing blow
      Assert.IsTrue(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 256)));  // slay undead
    }

    [TestMethod]
    public void IsValid_AssassinateDisabled_RejectsAssassinate()
    {
      var validator = new DamageValidator(false, true, true, true, true, true);
      Assert.IsFalse(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 64)));
      Assert.IsTrue(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 0)));
    }

    [TestMethod]
    public void IsValid_BaneDisabled_RejectsBane()
    {
      var validator = new DamageValidator(true, false, true, true, true, true);
      Assert.IsFalse(validator.IsValid(CreateDamageRecord(type: Labels.Bane, mask: 0)));
      Assert.IsTrue(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 0)));
    }

    [TestMethod]
    public void IsValid_DsDisabled_RejectsDs()
    {
      var validator = new DamageValidator(true, true, false, true, true, true);
      Assert.IsFalse(validator.IsValid(CreateDamageRecord(type: Labels.Ds, mask: 0)));
      Assert.IsTrue(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 0)));
    }

    [TestMethod]
    public void IsValid_FinishingBlowDisabled_RejectsFinishingBlow()
    {
      var validator = new DamageValidator(true, true, true, false, true, true);
      Assert.IsFalse(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 2048)));
      Assert.IsTrue(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 0)));
    }

    [TestMethod]
    public void IsValid_HeadshotDisabled_RejectsHeadshot()
    {
      var validator = new DamageValidator(true, true, true, true, false, true);
      Assert.IsFalse(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 128)));
      Assert.IsTrue(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 0)));
    }

    [TestMethod]
    public void IsValid_SlayUndeadDisabled_RejectsSlayUndead()
    {
      var validator = new DamageValidator(true, true, true, true, true, false);
      Assert.IsFalse(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 256)));
      Assert.IsTrue(validator.IsValid(CreateDamageRecord(type: Labels.Melee, mask: 0)));
    }

    [TestMethod]
    public void IsValid_NullRecord_ReturnsFalse()
    {
      var validator = new DamageValidator(true, true, true, true, true, true);
      Assert.IsFalse(validator.IsValid(null));
    }

    [TestMethod]
    public void IsDamageLimited_AllEnabled_ReturnsFalse()
    {
      var validator = new DamageValidator(true, true, true, true, true, true);
      Assert.IsFalse(validator.IsDamageLimited());
    }

    [TestMethod]
    public void IsDamageLimited_OneDisabled_ReturnsTrue()
    {
      var validator = new DamageValidator(false, true, true, true, true, true);
      Assert.IsTrue(validator.IsDamageLimited());
    }

    [TestMethod]
    public void IsDamageLimited_AllDisabled_ReturnsTrue()
    {
      var validator = new DamageValidator(false, false, false, false, false, false);
      Assert.IsTrue(validator.IsDamageLimited());
    }

    [TestMethod]
    public void StaticIsDamageValid_AssassinateDisabled_ReturnsFalse()
    {
      Assert.IsFalse(DamageValidator.IsDamageValid(64, Labels.Melee, false, true, true, true, true, true));
    }

    [TestMethod]
    public void StaticIsDamageValid_AllEnabled_ReturnsTrue()
    {
      Assert.IsTrue(DamageValidator.IsDamageValid(0, Labels.Melee, true, true, true, true, true, true));
    }

    private static DamageRecord CreateDamageRecord(string type, short mask)
    {
      return new DamageRecord
      {
        Attacker = "TestAttacker",
        Defender = "TestDefender",
        Type = type,
        ModifiersMask = mask,
        Total = 100,
      };
    }
  }
}
