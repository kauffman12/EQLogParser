using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * The loading band over the derived fight list (loadOverlay), and the one thing it is allowed to say.
 *
 * Bulk ingest parks both derive lanes by design, so during the load of a big capture this window shows NO rows. What it
 * says while it has none is "Building derived fight list..." over an indeterminate bar, with the captured-fact count as
 * the moving part - and it says it for EVERY open whose read has history in it, chosen by hand or not: the grid is empty
 * because it is being built, and a pane that sits silent and blank reads as a broken feature (or, before the list blanked
 * on a session change, as last night's raid). The file's own PERCENT stays out of here - the application-wide status line
 * at the top of the window counts that off the same 500 ms pump, and a second copy in the dock duplicates it.
 *
 * The one open that stays silent is a follow-from-end-of-file read (the startup auto-monitor, and Clear All): it hands
 * over no lines, so it owes no first build, and an empty list with "Monitoring Log" up top is the
 * correct answer rather than a wait. `linesRead` on ReportCaptureProgress is that whole distinction.
 *
 * The first snapshot takes the band down for good and it stays down for the session - real rows beat a bar, and a quiet
 * stretch mid-file can legitimately complete a derive under 100 %.
 *
 * The pane carries no status text at all: the top-right message section was removed on request, so a derive, a selection
 * or a failed pass has nothing to say there - DeriveEngine journals failures to eqlogparser.log (repeating stack plus
 * retry interval) and the boards answer a selection. What remains is only what a band can own: why the list is empty.
 *
 * Same construction contract as FightTableStartupTest - STA thread because WPF will not build a
 * FrameworkElement on MTA, stubbed app-level StaticResources because the test host never loads App.xaml.
 */
[TestClass]
public sealed class FightTableLoadBandTest
{
  private string _savedConfigDir = "";
  private string _tempDir = "";

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _tempDir = Path.Combine(Path.GetTempPath(), "fightband-" + Guid.NewGuid().ToString("N"));
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
      // TemplateToolTip must be a DATA template: GridColumnBase.ToolTipTemplate is typed DataTemplate and
      // App.xaml's real one is a DataTemplate. Handing it a ControlTemplate parses fine here and detonates
      // the pane's InitializeComponent with "'ControlTemplate' is not a valid value for property 'ToolTipTemplate'".
      res["TemplateToolTip"] ??= new DataTemplate();
      res["EQIconStyle"] ??= new Style(typeof(System.Windows.Controls.Image));
    });
  }

  /*
   * The panel's handlers arrive through Dispatcher.InvokeAsync at Normal, so the flush must pump AT Normal:
   * that drains every queued handler and stops above Loaded/Render. Pumping LOWER (SystemIdle) additionally runs
   * the pane's first arrange pass - and with the band visible that pass starts the indeterminate bar's INFINITE
   * animation on a windowless test thread, which took the render-thread path and hung the run for the full 60 s
   * Sta budget (measured on Windows: both band-showing tests hung, AReadingTick - same pump, band collapsed -
   * sailed through in milliseconds). Priority is load-bearing in both directions.
   */
  private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Normal);

  [TestMethod]
  public void AnOpenWithHistorySaysWhyTheListIsEmpty()
  {
    Sta.Run(() =>
    {
      var table = new FightTable();

      // A pump tick on a read that HAS handed lines over: the grid is empty because the first pass cannot run during a
      // bulk load, and only this panel can say so. Whether the operator picked the file is nobody's business here - an
      // open that was asked for is still an open whose list would otherwise sit blank with no explanation.
      table.ReportCaptureProgress(120_000);
      Flush();
      Assert.AreEqual(Visibility.Visible, table.loadOverlay.Visibility, "reading a file with history raises the band");
      StringAssert.Contains(table.loadText.Text, "Building");
      Assert.IsTrue(table.loadBar.IsIndeterminate, "the wait for the first derive has no known length");
    });
  }

  [TestMethod]
  public void AFollowFromEndOpenStaysSilent()
  {
    Sta.Run(() =>
    {
      var table = new FightTable();

      /*
       * Clear All and the startup auto-monitor both re-open at end of file: zero lines handed over, so no first build is
       * owed and the honest state is an empty list (one status phrase, "Monitoring Log", for both kinds of open). A band here
       * would sit saying "building" for the rest of the evening over a log that finished loading.
       */
      table.ReportCaptureProgress(0);
      Flush();
      Assert.AreEqual(Visibility.Collapsed, table.loadOverlay.Visibility, "an open that read nothing owes no build");

      // Later ticks of the same open - still nothing to build, because nothing was ever read.
      table.ReportCaptureProgress(0);
      Flush();
      Assert.AreEqual(Visibility.Collapsed, table.loadOverlay.Visibility, "EOF with no history read is not a wait either");
      Assert.AreEqual("", table.loadText.Text, "no headline is written for a phase this panel does not report");
    });
  }

  [TestMethod]
  public void LinesLandingLaterStartsTheAnnouncement()
  {
    Sta.Run(() =>
    {
      var table = new FightTable();

      // The first pump tick of an open can precede the reader's first handover; the next one that finds lines turns the
      // band up. "No history" is only ever a conclusion drawn from a session that has still read nothing AT all, which is
      // why the flag is monotonic for the session rather than a reading of one tick.
      table.ReportCaptureProgress(0);
      Flush();
      Assert.AreEqual(Visibility.Collapsed, table.loadOverlay.Visibility);

      table.ReportCaptureProgress(5_000);
      Flush();
      Assert.AreEqual(Visibility.Visible, table.loadOverlay.Visibility, "lines did land: the build is owed after all");
    });
  }

  [TestMethod]
  public void TheFirstSnapshotTakesTheBandDownForGood()
  {
    Sta.Run(() =>
    {
      var table = new FightTable();
      table.ReportCaptureProgress(120_000);
      Flush();
      Assert.AreEqual(Visibility.Visible, table.loadOverlay.Visibility);

      table.OnDerived(new DerivedSnapshot { DerivedAt = DateTime.Now, FightCount = 2, FactCount = 4_000 });
      Flush();
      Assert.AreEqual(Visibility.Collapsed, table.loadOverlay.Visibility, "rows beat the bar");

      // A late pump tick with plenty more lines landed must not resurrect the band for this session.
      table.ReportCaptureProgress(400_000);
      Flush();
      Assert.AreEqual(Visibility.Collapsed, table.loadOverlay.Visibility, "a settled session stays settled");
    });
  }
}
