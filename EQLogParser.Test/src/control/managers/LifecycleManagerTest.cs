using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * Who may raise CombatEvents.ActiveDataCleared. The seven grids and charts blank on this signal, so it belongs to the
   * app's CLEAR PATH - LifecycleManager's new-capture fan-out (the log closed / another one opens) - and not to a store.
   * It used to fire inside FightManager.Clear as well, which meant the clearest signal in the application died with whichever
   * store was scheduled for deletion; that store is gone now and the law outlived it. The fight list's Clear All is NOT a second
   * raiser: it re-opens the same file as a monitor session (MainWindow.ClearAllFights) and CloseLogFile brings it here, so one
   * raise still covers every way the app loses a capture - and ClearedSessionMemoryTest pins what does NOT go with it. These
   * tests are the law from both sides: the path raises, and a bare store reset (which will outlive nothing - it dies with the
   * pipeline) stays silent on its own.
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
