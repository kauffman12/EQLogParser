using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * Which capture a repaint is allowed to come from, and what the list does when that changes.
 *
 * Both laws came from one report: "clear all does nothing — it just leaves the fight list full of content", plus the two
 * shapes next to it (opening a second file keeps the first one's rows on screen until the new capture's first pass, and
 * opening a file with nothing derivable in it leaves the previous raid there indefinitely). They share a cause: `Derived`
 * is the only thing that ever replaces these rows, so whenever no NEW pass is coming — an end-of-file re-open, an empty
 * file, a cleared one — whatever was already displayed becomes the answer to a question nobody is asking any more. And a
 * derive pass that was already running when its session was disposed still announces itself a heartbeat later, which
 * paints the dead capture back over the live panel.
 *
 * So there are two seams, and each is pinned here:
 *   - the pane blanks on a session change (ClearForNewCapture), rather than waiting for a pass that may never arrive;
 *   - a snapshot says which capture wrote it (DerivedSnapshot.SessionId) and anything not from the open one is dropped.
 *
 * ActiveChanged cannot be raised from outside DeriveEngine, and an engine needs a real log file, so these drive the two
 * internal seams directly. The band's own rules are in FightTableLoadBandTest.
 *
 * Same construction contract as FightTableStartupTest - STA thread because WPF will not build a FrameworkElement on MTA,
 * stubbed app-level StaticResources because the test host never loads App.xaml.
 */
[TestClass]
public sealed class FightTableSessionSwitchTest
{
  private const double T0 = 1_000;

  private string _savedConfigDir = "";
  private string _tempDir = "";

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _tempDir = Path.Combine(Path.GetTempPath(), "fighterswitch-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);
    ConfigUtil.ConfigDir = _tempDir;
    PlayerRegistry.Instance.Clear();
    EnsureAppResources();
  }

  [TestCleanup]
  public void Cleanup()
  {
    PlayerRegistry.Instance.Clear();
    ConfigUtil.ConfigDir = _savedConfigDir;
    try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (IOException) { }
  }

  private static void EnsureAppResources()
  {
    Sta.Run(() =>
    {
      _ = Application.Current ?? new Application();
      var res = Application.Current!.Resources;
      res["CustomCheckBoxTemplate"] ??= new ControlTemplate(typeof(CheckBox));
      res["TemplateToolTip"] ??= new DataTemplate();
      res["EQIconStyle"] ??= new Style(typeof(System.Windows.Controls.Image));
    });
  }

  private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Normal);

  private static DerivedFight Fight(string name, double beginS, double lastS) =>
    new()
    {
      Name = name,
      Id = 1,
      BeginTime = T0 + beginS,
      LastTime = T0 + lastS,
      DamageTotal = 100,
      DamageHits = 3,
      DamageToOwner = 10,
      DamageByOwner = 90,
    };

  // Two fights and the divider between them, built the way the engine builds a snapshot's display rows.
  private static DerivedSnapshot Snapshot(int sessionId = 0) =>
    Stamp(DerivedFightRows.Build([Fight("Grul", 0, 20), Fight("MoK", 4_000, 4_030)],
                                 new EntityTimeline(), 0, new DamageFactTable(8), new FightFactIndex()), sessionId);

  private static DerivedSnapshot Stamp(DerivedSnapshot snapshot, int sessionId)
  {
    snapshot.SessionId = sessionId;
    return snapshot;
  }

  [TestMethod]
  public void ANewCaptureBlanksTheListInsteadOfKeepingTheOldOne()
  {
    Sta.Run(() =>
    {
      var table = new FightTable();
      table.OnDerived(Snapshot());
      Flush();
      Assert.IsTrue(table.RowCount > 0, "the fixture: a pass puts rows on the pane");

      /*
       * The session seam moved. No pass from the capture that made these rows is coming, and no pass from the new one has;
       * what is on screen describes a log that no longer answers, so it goes now rather than whenever something happens to
       * overwrite it. This is the whole of "clear all does nothing" and of "I opened an empty file and last night is still here".
       */
      table.ClearForNewCapture();
      Flush();
      Assert.AreEqual(0, table.RowCount, "a session change leaves nothing displayed until the new capture says otherwise");
    });
  }

  [TestMethod]
  public void ASnapshotFromACaptureThatClosedDoesNotRepaint()
  {
    Sta.Run(() =>
    {
      var table = new FightTable();

      // Some other capture's pass (no session is open here, and a closed one is not "current"): the list stays as it is.
      table.OnDerived(Stamp(Snapshot(), sessionId: 7));
      Flush();
      Assert.AreEqual(0, table.RowCount, "a pass that is not the open capture's must not put rows on this grid");

      // Same again after a session change: the stamp, not the arrival order, is what decides.
      table.ClearForNewCapture();
      table.OnDerived(Stamp(Snapshot(), sessionId: 7));
      Flush();
      Assert.AreEqual(0, table.RowCount, "a stale pass cannot refill a list that was just blanked");
    });
  }

  [TestMethod]
  public void AnUnattributedSnapshotBelongsToWhicheverCaptureIsOpen()
  {
    /*
     * The stamp's escape hatch, pinned so it cannot be "tidied" into a strict equality that breaks every test (and every
     * hand-built snapshot) in the assembly: SessionId 0 means "unattributed", and unattributed matches whatever session is
     * current. Production stamps every snapshot at its one producer (DeriveEngine), so 0 never arrives from there.
     */
    Sta.Run(() =>
    {
      var table = new FightTable();
      table.OnDerived(Snapshot());
      Flush();
      Assert.IsTrue(table.RowCount > 0, "an unstamped snapshot still lands");
    });
  }
}
