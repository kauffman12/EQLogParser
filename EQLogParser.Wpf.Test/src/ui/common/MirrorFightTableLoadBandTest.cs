using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * The loading band over the derived fight list (mirrorLoadOverlay), and the one thing it is allowed to say.
 *
 * Bulk ingest parks both derive lanes by design, so during the load of a big capture this window shows NO rows.
 * The reading phase is announced by the application-wide status line at the top of the window - percent and
 * seconds off the same 500 ms pump - and a second copy inside the dock duplicates it, so the pump's sub-100 ticks
 * must leave this panel silent. What only this panel can say is the gap the top bar cannot: EOF has happened and
 * the first snapshot has not, "Building derived fight list..." over an indeterminate bar. The first snapshot takes
 * it down for the session and it stays down (real rows beat a bar).
 *
 * The header status line gets the companion law: a completed derive says NOTHING there - the "Derived HH:mm:ss -
 * N fights, M facts, X ms" line never fit the dock beside three columns. Only an override verdict claims the
 * space, and placeholders ("Capturing...", "No log open", stale failure lines) clear when rows land.
 *
 * Same construction contract as MirrorFightTableStartupTest - STA thread because WPF will not build a
 * FrameworkElement on MTA, stubbed app-level StaticResources because the test host never loads App.xaml.
 */
[TestClass]
public sealed class MirrorFightTableLoadBandTest
{
  private string _savedConfigDir = "";
  private string _tempDir = "";

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _tempDir = Path.Combine(Path.GetTempPath(), "mirrorband-" + Guid.NewGuid().ToString("N"));
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
      res["TemplateToolTip"] ??= new ControlTemplate(typeof(Control));
      res["EQIconStyle"] ??= new Style(typeof(System.Windows.Controls.Image));
    });
  }

  // The panel's handlers arrive through Dispatcher.InvokeAsync; an STA test thread runs no message loop, so
  // every step flushes the queue at the lowest priority before asserting on what it changed.
  private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.SystemIdle);

  [TestMethod]
  public void AReadingTickSaysNothingHere()
  {
    Sta.Run(() =>
    {
      var table = new MirrorFightTable();
      Assert.AreEqual(Visibility.Collapsed, table.mirrorLoadOverlay.Visibility, "a fresh panel shows no band");

      // Mid-file pump ticks: the application status line is the one that counts percent. This window stays out
      // of the conversation - a duplicate progress report in the dock is what the user asked to have removed.
      table.ReportCaptureProgress(35);
      Flush();
      Assert.AreEqual(Visibility.Collapsed, table.mirrorLoadOverlay.Visibility, "reading is announced at the top, not here");
      Assert.AreEqual("", table.mirrorLoadText.Text, "no headline is written for a phase this panel does not report");
    });
  }

  [TestMethod]
  public void AEofTickRaisesTheBuildingBand()
  {
    Sta.Run(() =>
    {
      var table = new MirrorFightTable();

      // EOF: reading is done but the list is not - only this panel can say so, over an indeterminate bar.
      table.ReportCaptureProgress(100);
      Flush();
      Assert.AreEqual(Visibility.Visible, table.mirrorLoadOverlay.Visibility, "EOF raises the building band");
      StringAssert.Contains(table.mirrorLoadText.Text, "Building");
      Assert.IsTrue(table.mirrorLoadBar.IsIndeterminate, "the wait for the first derive has no known length");
    });
  }

  [TestMethod]
  public void TheFirstSnapshotTakesTheBandDownForGood()
  {
    Sta.Run(() =>
    {
      var table = new MirrorFightTable();
      table.ReportCaptureProgress(100);
      Flush();
      Assert.AreEqual(Visibility.Visible, table.mirrorLoadOverlay.Visibility);

      // The placeholder the panel shows before any data; rows landing must retire it, not leave it hanging.
      table.mirrorStatus.Text = "Capturing...";

      table.OnDerived(new DerivedSnapshot { DerivedAt = DateTime.Now, FightCount = 2, FactCount = 4_000 });
      Flush();
      Assert.AreEqual(Visibility.Collapsed, table.mirrorLoadOverlay.Visibility, "rows beat the bar");
      Assert.IsFalse(table.mirrorStatus.Text.Contains("Derived"), "a completed derive writes no stats line in the header");
      Assert.AreEqual(string.Empty, table.mirrorStatus.Text, "the capturing placeholder retires with the first rows");

      // A late pump tick - a tail running past 100 - must not resurrect the band for this session.
      table.ReportCaptureProgress(100);
      Flush();
      Assert.AreEqual(Visibility.Collapsed, table.mirrorLoadOverlay.Visibility, "a settled session stays settled");
    });
  }
}
