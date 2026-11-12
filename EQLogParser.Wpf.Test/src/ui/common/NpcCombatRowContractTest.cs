namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class NpcCombatRowContractTest
{
  [TestMethod]
  public void TheBoundRowPreservesEveryEstimatorColumnAndItsType()
  {
    var expected = typeof(NpcCombatStatsRow).GetProperties().ToDictionary(p => p.Name, p => p.PropertyType);
    var actual = typeof(NpcCombatRow).GetProperties().ToDictionary(p => p.Name, p => p.PropertyType);
    CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
    foreach (var (name, type) in expected)
    {
      Assert.AreEqual(type, actual[name], name + " must preserve its type, including unavailable health as null");
    }
  }
}
