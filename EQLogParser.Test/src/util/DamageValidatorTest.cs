namespace EQLogParser
{
  [TestClass]
  public class DamageValidatorTest
  {
    [TestMethod]
    [DataRow(Labels.Melee, 0)]
    [DataRow(Labels.Bane, 0)]
    [DataRow(Labels.Ds, 0)]
    [DataRow(Labels.Melee, 64)]
    [DataRow(Labels.Melee, 128)]
    [DataRow(Labels.Melee, 2048)]
    [DataRow(Labels.Melee, 256)]
    public void AllEnabledAcceptsEveryDamageKindThroughBothDoors(string type, int mask)
    {
      var validator = new DamageValidator(true, true, true, true, true, true);
      Assert.IsTrue(validator.IsValid(Record(type, (short)mask)));
      Assert.IsTrue(DamageValidator.IsDamageValid((short)mask, type, true, true, true, true, true, true));
      Assert.IsFalse(validator.IsDamageLimited());
    }

    [TestMethod]
    [DataRow(false, true, true, true, true, true, Labels.Melee, 64)]
    [DataRow(true, false, true, true, true, true, Labels.Bane, 0)]
    [DataRow(true, true, false, true, true, true, Labels.Ds, 0)]
    [DataRow(true, true, true, false, true, true, Labels.Melee, 2048)]
    [DataRow(true, true, true, true, false, true, Labels.Melee, 128)]
    [DataRow(true, true, true, true, true, false, Labels.Melee, 256)]
    public void EachDisabledOptionRejectsOnlyItsOwnKind(bool assassinate, bool bane, bool ds, bool finishing,
      bool headshot, bool slay, string type, int mask)
    {
      var validator = new DamageValidator(assassinate, bane, ds, finishing, headshot, slay);
      Assert.IsFalse(validator.IsValid(Record(type, (short)mask)));
      Assert.IsFalse(DamageValidator.IsDamageValid((short)mask, type, assassinate, bane, ds, finishing, headshot, slay));
      Assert.IsTrue(validator.IsValid(Record(Labels.Melee, 0)));
      Assert.IsTrue(validator.IsDamageLimited());
    }

    [TestMethod]
    public void NoRecordIsRefusedAndAllDisabledIsLimited()
    {
      Assert.IsFalse(new DamageValidator(true, true, true, true, true, true).IsValid(null));
      Assert.IsTrue(new DamageValidator(false, false, false, false, false, false).IsDamageLimited());
    }

    private static DamageRecord Record(string type, short mask) => new()
    {
      Attacker = "TestAttacker", Defender = "TestDefender", Type = type, ModifiersMask = mask, Total = 100
    };
  }
}
