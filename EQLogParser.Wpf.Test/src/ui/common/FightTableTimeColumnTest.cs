using System.Windows;
using System.Windows.Controls;

using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * The derived fight list's one column that is neither Auto-sized nor Auto-filled: "Initial Hit Time" takes its
 * width from ThemeConfig.CurrentDateTimeWidth, and the law under test is WHERE that read happens.
 *
 * MainWindow builds this pane inside its own InitializeComponent - before SetMainWindow → ThemeConfig.Init - so a
 * constructor that reads the theme statics gets 0.0 (they are plain fields, assigned only by
 * ThemeConfig.SetThemeFontSizes). A zero-width fixed column exists but renders as nothing: that is how the pane
 * shipped for months with no Initial Hit Time at all while its HP sibling looked fine, because ColumnSizer=Auto
 * makes the sizer ignore Width - which is also why DataGridUtil.RefreshTableColumns skips sizer columns. Every
 * other themed grid dodged the trap purely by being constructed after theme init (HitLogViewer reads widths in its
 * constructor too, and is safe only because the summary panes build it lazily).
 *
 * What is measured here is ApplyTimeColumnWidth - the single place that computes the width, called by both hooks
 * (fightGrid.Loaded and ThemeConfig.EventsThemeChanged) - together with the fact that no earlier read is cached:
 * a pane built under an uninitialized theme still takes a width that arrives later.
 *
 * Why neither hook is fired at the pane:
 *   - Raising FrameworkElement.LoadedEvent by hand drives SfDataGrid's own load path (its ItemsSource callback,
 *     container/view creation) in a host with no PresentationSource. This suite has already paid twice for pulling
 *     render-thread paths into a windowless thread (the indeterminate bar's animation; the 60 s Sta wedges), so the
 *     grid is not woken up to prove a one-line handler is attached.
 *   - ThemeConfig's raisers all need a live MainWindow: SetThemeFontSizes dereferences _mainWindow.npcWindow and
 *     SetThemeResources touches main.statusText, so no headless process can raise EventsThemeChanged at all.
 * Both are stated rather than tested; the arithmetic they hand to ApplyTimeColumnWidth is what this pins.
 *
 * Same construction contract as FightTableStartupTest - STA thread because WPF will not build a FrameworkElement on
 * MTA, stubbed app-level StaticResources because the test host never loads App.xaml. CurrentDateTimeWidth is a
 * process static and gets restored, like the FCT dials.
 */
[TestClass]
public sealed class FightTableTimeColumnTest
{
  private string _savedConfigDir = "";
  private string _tempDir = "";
  private double _savedDateTimeWidth;

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _tempDir = Path.Combine(Path.GetTempPath(), "fightcolumn-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);
    ConfigUtil.ConfigDir = _tempDir;
    _savedDateTimeWidth = ThemeConfig.CurrentDateTimeWidth;
    PlayerRegistry.Instance.Clear();
    EnsureAppResources();
  }

  [TestCleanup]
  public void Cleanup()
  {
    ThemeConfig.CurrentDateTimeWidth = _savedDateTimeWidth;
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
      // TemplateToolTip must be a DATA template (GridColumnBase.ToolTipTemplate is typed DataTemplate); a
      // ControlTemplate parses fine here and detonates InitializeComponent with a property-type error.
      res["TemplateToolTip"] ??= new DataTemplate();
      res["EQIconStyle"] ??= new Style(typeof(System.Windows.Controls.Image));
    });
  }

  [TestMethod]
  public void TheTimeColumnWidthIsReadWhenAppliedAndNeverInTheConstructor()
  {
    // A number no vendor default and no font size produces, so a constructor that read the theme static would be
    // caught red-handed. It must not: MainWindow builds this pane before ThemeConfig exists, and whatever it read
    // there is the width the column keeps - which is how a 0.0 became "no Initial Hit Time at all".
    ThemeConfig.CurrentDateTimeWidth = 123.0;

    double atConstruction = -1;
    double afterApply = -1;

    Sta.Run(() =>
    {
      var table = new FightTable();
      atConstruction = table.beginColumn.Width;   // DP read on its own thread, like every other assertion here

      table.ApplyTimeColumnWidth();               // what fightGrid.Loaded and EventsThemeChanged both do
      afterApply = table.beginColumn.Width;
    });

    Assert.AreNotEqual(123.0, atConstruction, "the constructor did not read the theme - it cannot see one yet");
    Assert.AreEqual(123.0, afterApply, "and the width arrives when it is applied");
  }

  [TestMethod]
  public void APaneBuiltUnderAnUnsetThemeStillGetsItsColumnFromTheThemeThatComesLater()
  {
    // The startup order in one pair of lines: ThemeConfig's statics are plain fields assigned by
    // SetThemeFontSizes, so a pane built first reads nothing. Nothing may be banked from that read - the value is
    // taken from the static each time it is applied, which is what a real theme init (font 12 → 10*12-10 = 110)
    // depends on.
    ThemeConfig.CurrentDateTimeWidth = 0.0;

    double afterApply = -1;

    Sta.Run(() =>
    {
      var table = new FightTable();
      ThemeConfig.CurrentDateTimeWidth = 110.0;
      table.ApplyTimeColumnWidth();
      afterApply = table.beginColumn.Width;
    });

    Assert.AreEqual(110.0, afterApply, "an empty theme at construction does not poison the column for the session");
  }

  [TestMethod]
  public void AFontScaleChangeMovesTheTimeColumnLikeEveryOtherThemedGrid()
  {
    ThemeConfig.CurrentDateTimeWidth = 110.0;

    double small = -1;
    double large = -1;

    Sta.Run(() =>
    {
      var table = new FightTable();
      table.ApplyTimeColumnWidth();     // fightGrid.Loaded does exactly this
      small = table.beginColumn.Width;

      ThemeConfig.CurrentDateTimeWidth = 150.0;
      table.ApplyTimeColumnWidth();     // and so does EventsThemeChanged
      large = table.beginColumn.Width;
    });

    Assert.AreEqual(110.0, small);
    Assert.AreEqual(150.0, large, "a font-scale change carries the time column too - it is not a hand-picked number");
  }
}
