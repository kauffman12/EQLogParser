using System.Reflection;

namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class NpcStatsPreparationTest
{
  [TestMethod]
  public void OnlyTheLatestProjectionCanBeDetachedBeforeTheWorkerWalksIt()
  {
    var accepted = false;
    var oldRejected = false;
    var otherSessionRejected = false;
    var closedRejected = false;
    Sta.Run(() =>
    {
      using var session = new DeriveEngine();
      var old = Snapshot(session.SessionId);
      var current = Snapshot(session.SessionId);
      var field = typeof(DeriveEngine).GetField("_meterSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
      Assert.IsNotNull(field);
      field.SetValue(session, current);

      var zones = new NpcZoneTimeline(null);
      accepted = session.PrepareNpcStats(current, zones, null) is not null;
      oldRejected = session.PrepareNpcStats(old, zones, null) is null;
      current.SessionId++;
      otherSessionRejected = session.PrepareNpcStats(current, zones, null) is null;
      current.SessionId = session.SessionId;
      session.Dispose();
      closedRejected = session.PrepareNpcStats(current, zones, null) is null;
    });

    Assert.IsTrue(accepted);
    Assert.IsTrue(oldRejected, "an older list cannot be combined with life fields mutated by a later pass");
    Assert.IsTrue(otherSessionRejected);
    Assert.IsTrue(closedRejected);
  }

  private static DerivedSnapshot Snapshot(int sessionId)
  {
    var facts = new DamageFactTable(8);
    var snapshot = DerivedFightRows.Build([], new EntityTimeline(), 0, facts, new FightFactIndex());
    snapshot.SessionId = sessionId;
    return snapshot;
  }
}
