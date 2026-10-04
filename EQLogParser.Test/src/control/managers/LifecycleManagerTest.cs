using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * Who may raise CombatEvents.ActiveDataCleared. The seven grids and charts blank on this signal, so its owners are
   * the app's CLEAR PATHS - LifecycleManager's new-capture fan-out (the log closed / another one opens) and the fight
   * list's Clear All button - and not FightManager. Raising it from FightManager.Clear worked right up until the day
   * the legacy store gets deleted, which is the event's quiet deadline: a board that keeps showing the previous
   * night's raid because nobody raised "cleared" is worse than an empty one. These two tests are the same law seen
   * from both sides: the path raises, and a bare store reset (which will outlive nothing - it dies with the pipeline)
   * stays silent on its own.
   */
  [TestClass]
  public sealed class LifecycleManagerTest
  {
    [TestMethod]
    public void ANewCaptureClearRaisesTheSignalAndPassesThePayloadThrough()
    {
      var fired = 0;
      bool? payload = null;
      Action<bool> handler = serverChanged => { fired++; payload = serverChanged; };

      CombatEvents.ActiveDataCleared += handler;
      try
      {
        LifecycleManager.Clear(serverChanged: true);

        Assert.AreEqual(1, fired, "the clear path is the one that announces the clear");
        Assert.IsTrue(payload, "serverChanged rides through to the views that reset more than their rows");

        LifecycleManager.Clear(serverChanged: false);
        Assert.AreEqual(2, fired, "every clear raises, not just the first");
        Assert.IsFalse(payload, "and the payload follows the path, not a leftover from the last one");
      }
      finally
      {
        CombatEvents.ActiveDataCleared -= handler;
      }
    }

  }
}
