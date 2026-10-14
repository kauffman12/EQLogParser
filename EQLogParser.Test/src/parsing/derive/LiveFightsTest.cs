namespace EQLogParser;

[TestClass]
public class LiveFightsTest
{
  private const double T0 = 1_000_000;

  [TestMethod]
  public void LivenessUsesTheEngagementGapAndExcludesDeadRows()
  {
    var row = new DerivedFight { LastDamageTime = T0 };
    Assert.IsTrue(LiveFights.IsLive(row, T0 + 30, LiveFights.GapS));
    Assert.IsFalse(LiveFights.IsLive(row, T0 + 31, LiveFights.GapS));
    row.Dead = true;
    Assert.IsFalse(LiveFights.AnyLive([row], T0, LiveFights.GapS));
  }

  [TestMethod]
  public void ActivityUsesTheNewestDirectionAndFallsBackToTheRow()
  {
    var row = new DerivedFight { LastDamageTime = T0, LastTankingTime = T0 + 2, LastTime = T0 + 5 };
    Assert.AreEqual(T0 + 2, LiveFights.LastActivityAt(row));
    row.LastDamageTime = T0 + 3;
    Assert.AreEqual(T0 + 3, LiveFights.LastActivityAt(row));
    row.LastDamageTime = row.LastTankingTime = double.NaN;
    Assert.AreEqual(T0 + 5, LiveFights.LastActivityAt(row));
  }

  [TestMethod]
  public void HiddenPetRowsAreStillActivity()
  {
    var pet = new DerivedFight { RaidPet = true, LastDamageTime = T0 };
    Assert.IsTrue(LiveFights.AnyLive([pet], T0, LiveFights.GapS));
  }

  [TestMethod]
  public void LiveCensusDoesNotAnnounceTheSameActivityTwice()
  {
    var row = new DerivedFight { LastDamageTime = T0 };
    Assert.IsTrue(LiveFights.HasFreshDamage([row], T0 - 1, T0, LiveFights.GapS));
    Assert.IsFalse(LiveFights.HasFreshDamage([row], T0, T0, LiveFights.GapS));
    row.Dead = true;
    Assert.IsTrue(double.IsNaN(LiveFights.LatestActivityAt([row])));
    Assert.IsTrue(double.IsNaN(LiveFights.LatestActivityAt([])));
  }

  [TestMethod]
  public void TimedLivenessUsesTheRequestedGap()
  {
    var row = new DerivedFight { LastDamageTime = T0 };
    Assert.IsTrue(LiveFights.AnyLive([row], T0 + 12, LiveFights.TimeoutFor(0)));
    Assert.IsFalse(LiveFights.AnyLive([row], T0 + 12, LiveFights.TimeoutFor(5)));
  }
}
