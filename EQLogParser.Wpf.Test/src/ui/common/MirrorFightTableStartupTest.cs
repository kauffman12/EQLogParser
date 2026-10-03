using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * The one startup contract for the derived fight list: `new MirrorFightTable()` must not throw. This pane is built by
 * MainWindow's own XAML, so a constructor exception here is not a failed window - it is CreateAppError and no app at all
 * (the reported crash: the toolbar checkbox's XAML IsChecked="True" fires its Checked handler DURING InitializeComponent,
 * before any of this control's named fields are wired, and the handler read `mirrorShowHp.IsChecked` off the still-null
 * field; NullReferenceException out through LoadBaml, 100% of launches).
 *
 * The legacy table survives its own IsChecked="True" via the `dataGrid?.View != null` contract. These tests pin both
 * halves for this control: that construction is survivable, and that a pre-load toggle fires harmlessly - acting on it
 * would also have overwritten the stored setting before the constructor read it, silently flipping the user's saved
 * dials. No test had ever CONSTRUCTED either table (NamesTableTest deliberately tests only the formatting seam), which
 * is how a guaranteed startup crash crossed a whole batch of commits.
 */
[TestClass]
public sealed class MirrorFightTableStartupTest
{
  private string _savedConfigDir = "";
  private string _tempDir = "";

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _tempDir = Path.Combine(Path.GetTempPath(), "mirrorfight-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);
    ConfigUtil.ConfigDir = _tempDir;
    PlayerRegistry.Instance.Clear();
  }

  [TestCleanup]
  public void Cleanup()
  {
    PlayerRegistry.Instance.Clear();
    ConfigUtil.ConfigDir = _savedConfigDir;
    try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (IOException) { }
  }

  [TestMethod]
  public void TheTableConstructsWithoutThrowing()
  {
    // This line alone reproduces the reported crash: it is MainWindow's own construction path for this pane,
    // run with no session and no log - the state at startup.
    MirrorFightTable? table = null;
    Sta.Run(() => table = new MirrorFightTable());
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

    MirrorFightTable? table = null;
    Sta.Run(() => table = new MirrorFightTable());

    Assert.IsFalse(table!.mirrorShowBreaks.IsChecked == true, "the saved OFF survived XAML's IsChecked=True");
    Assert.IsFalse(table.mirrorShowHp.IsChecked == true);
  }
}
