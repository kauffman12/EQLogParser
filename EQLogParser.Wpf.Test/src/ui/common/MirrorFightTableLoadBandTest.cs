using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * The loading band over the derived fight list (mirrorLoadOverlay).
 *
 * Bulk ingest parks both derive lanes by design, so during the load of a big capture this window shows NO rows -
 * and showed nothing else either, just a counter on a status line. The band replaced that: MainWindow's reader
 * pump feeds it byte progress, EOF flips it to an indeterminate "building", and the first snapshot takes it down
 * for the session (a quiet stretch mid-file can complete a derive under 100 %, and real rows beat a bar).
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
  public void TheBandTracksTheReaderUntilTheFirstSnapshot()
  {
    Sta.Run(() =>
    {
      var table = new MirrorFightTable();
      Assert.AreEqual(Visibility.Collapsed, table.mirrorLoadOverlay.Visibility, "a fresh panel shows no band");

      table.ReportCaptureProgress(35, 4);
      Flush();
      Assert.AreEqual(Visibility.Visible, table.mirrorLoadOverlay.Visibility, "the reader pump raises the band");
      Assert.IsFalse(table.mirrorLoadBar.IsIndeterminate, "byte progress is a determinate bar");
      Assert.AreEqual(35, table.mirrorLoadBar.Value, 0.5, "the bar tracks the percent it was handed");
      StringAssert.Contains(table.mirrorLoadText.Text, "35");

      // EOF: reading is done but the list is not - indeterminate until a snapshot lands.
      table.ReportCaptureProgress(100, 9);
      Flush();
      Assert.AreEqual(Visibility.Visible, table.mirrorLoadOverlay.Visibility, "EOF keeps the band up");
      Assert.IsTrue(table.mirrorLoadBar.IsIndeterminate, "the wait for the first derive has no known length");
    });
  }

  [TestMethod]
  public void TheFirstSnapshotTakesTheBandDownForGood()
  {
    Sta.Run(() =>
    {
      var table = new MirrorFightTable();
      table.ReportCaptureProgress(60, 3);
      Flush();
      Assert.AreEqual(Visibility.Visible, table.mirrorLoadOverlay.Visibility);

      table.OnDerived(new MirrorSnapshot { DerivedAt = DateTime.Now, FightCount = 2, FactCount = 4_000 });
      Flush();
      Assert.AreEqual(Visibility.Collapsed, table.mirrorLoadOverlay.Visibility, "rows beat the bar");

      // A late pump tick - a derive legitimately completed mid-file - must not resurrect the band for this session.
      table.ReportCaptureProgress(80, 12);
      Flush();
      Assert.AreEqual(Visibility.Collapsed, table.mirrorLoadOverlay.Visibility, "a settled session stays settled");
    });
  }
}
