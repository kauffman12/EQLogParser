using System.Windows;
using System.Windows.Controls;
using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * The startup contract for the derived fight list: `new FightTable()` must not throw, and its toolbar
 * handlers must distinguish "XAML is still parsing" from "the user clicked". MainWindow's own XAML constructs this
 * pane, so a constructor exception here is not a failed window - it is CreateAppError and no app at all.
 *
 * The reported crashes came in BOTH shapes, one build apart: first ShowHpChanged ran with fightShowHp itself still
 * null, then (with a sender-null guard in place) it fired again AFTER this checkbox was wired but BEFORE a column
 * declared later in the markup existed - NRE on damageColumn. Lesson, and the convention every legacy table
 * already follows: guard on the PANE's readiness (`fightGrid?.View is null`), never on the sender's. The grid's View
 * materializes only when the constructor assigns ItemsSource, so it is null for every synthetic pre-load firing and
 * non-null for every real one - including the constructor's own sync of the saved dials.
 *
 * No test had ever CONSTRUCTED either fight table (NamesTableTest deliberately tests only its formatting seam),
 * which is how a guaranteed startup crash crossed a whole batch of commits.
 *
 * Theme bootstrap: this pane's XAML resolves three StaticResource keys that live in App.xaml
 * (CustomCheckBoxTemplate, TemplateToolTip, EQIconStyle). The test host has no Application and never loads App.xaml,
 * so those lookups would throw XamlParseException before the guards under test ever run. We supply minimal stubs:
 * these tests pin the CONSTRUCTION contract - field wiring order and handler sentinels - not theme fidelity.
 */
[TestClass]
public sealed class FightTableStartupTest
{
  private string _savedConfigDir = "";
  private string _tempDir = "";

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _tempDir = Path.Combine(Path.GetTempPath(), "fightstartup-" + Guid.NewGuid().ToString("N"));
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

  // The three app-level StaticResource keys this pane's XAML needs, stubbed for the headless test host.
  // Idempotent: Application is a process singleton, so one stub pass serves every test in the run.
  private static void EnsureAppResources()
  {
    Sta.Run(() =>
    {
      _ = Application.Current ?? new Application();
      var res = Application.Current!.Resources;
      res["CustomCheckBoxTemplate"] ??= new ControlTemplate(typeof(CheckBox));
      res["TemplateToolTip"] ??= new ControlTemplate(typeof(Control));
      // ImageAwesome derives from Image; a Style's TargetType must be the styled element or a base of it.
      res["EQIconStyle"] ??= new Style(typeof(System.Windows.Controls.Image));
    });
  }

  [TestMethod]
  public void TheTableConstructsWithoutThrowing()
  {
    // This line alone reproduces both reported crashes: it is MainWindow's own construction path for this pane,
    // run with no session and no log - the state at startup.
    FightTable? table = null;
    Sta.Run(() => table = new FightTable());
    Assert.IsNotNull(table);
  }

  [TestMethod]
  public void AXamlCheckedBoxDoesNotOverwriteASavedDialBeforeItIsRead()
  {
    // Both dials saved OFF. The XAML IsChecked="True" fires its handler mid-parse; if that synthetic toggle were
    // allowed to act, it would write ON over the stored value BEFORE the constructor reads it, and the user's
    // chosen-off boxes come up checked on every start.
    ConfigUtil.SetSetting("NpcShowInactivityBreaks", false);
    ConfigUtil.SetSetting("NpcShowHitPoints", false);

    FightTable? table = null;
    Sta.Run(() => table = new FightTable());

    Assert.IsFalse(table!.fightShowBreaks.IsChecked == true, "the saved OFF survived XAML's IsChecked=True");
    Assert.IsFalse(table.fightShowHp.IsChecked == true);
  }

  [TestMethod]
  public void ARealToggleAfterLoadStillReachesTheColumn()
  {
    // The other half of the sentinel fix: absorbing synthetic pre-load firings must not swallow REAL ones.
    // Unchecking HP on a constructed pane is the user's click - the column must hide and the dial persist.
    ConfigUtil.SetSetting("NpcShowHitPoints", true);

    FightTable? table = null;
    Sta.Run(() =>
    {
      table = new FightTable();
      table.fightShowHp.IsChecked = false;
    });

    Assert.IsTrue(table!.damageColumn.IsHidden, "a post-load uncheck hid the column");
    Assert.IsFalse(ConfigUtil.IfSet("NpcShowHitPoints", true), "the dial was saved");
  }
}
