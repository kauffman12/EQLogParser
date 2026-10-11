using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using EQLogParser;

namespace EQLogParser.Wpf.Test;

// These tests exercise the pane's bound data without loading a windowless grid.
// Actual highlight/click/scroll checks belong in docs/ReleaseChecklist.md, not always-inconclusive tests.
[TestClass]
public sealed class FightTableDialVisibilityTest
{
  private const double T0 = 1_000;
  private string _savedConfigDir = "";
  private string _tempDir = "";

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _tempDir = Path.Combine(Path.GetTempPath(), "fightdials-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);
    ConfigUtil.ConfigDir = _tempDir;
    PlayerRegistry.Instance.Clear();
    Sta.Run(() =>
    {
      var app = Application.Current ?? new Application();
      app.Resources["CustomCheckBoxTemplate"] ??= new ControlTemplate(typeof(CheckBox));
      app.Resources["TemplateToolTip"] ??= new DataTemplate();
      app.Resources["EQIconStyle"] ??= new Style(typeof(System.Windows.Controls.Image));
    });
  }

  [TestCleanup]
  public void Cleanup()
  {
    PlayerRegistry.Instance.Clear();
    ConfigUtil.ConfigDir = _savedConfigDir;
    try
    {
      if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }
    catch (IOException)
    {
      // A test-host file handle may outlive cleanup.
    }
  }

  [TestMethod]
  public void UncheckingTankingHidesOnlyUndamagedRowsWithoutDeletingThem()
  {
    string[] names = [];
    var total = 0;
    Sta.Run(() =>
    {
      var table = Paint(Snapshot(Fight("Grul", 0, 90), Fight("MoK", 30, 0)));
      table.fightShowTanking.IsChecked = false;
      names = BoundRows(table).Select(row => row.Name).ToArray();
      total = table.RowCountAll;
    });
    CollectionAssert.AreEqual(new[] { "Grul" }, names);
    Assert.AreEqual(2, total, "a checkbox must not delete capture rows");
  }

  [TestMethod]
  public void HiddenRowsCanEnterAndLeaveOnALivePatchWithoutReplacingSurvivors()
  {
    var survivorKept = false;
    var newcomerKept = false;
    string[] entered = [];
    string[] left = [];
    Sta.Run(() =>
    {
      var table = Paint(Snapshot(Fight("Grul", 0, 90), Fight("MoK", 30, 0)));
      var before = BoundRows(table);
      table.fightShowTanking.IsChecked = false;
      table.OnDerived(Snapshot(Fight("Grul", 0, 140), Fight("MoK", 30, 50)));
      Flush();
      var after = BoundRows(table);
      entered = after.Select(row => row.Name).ToArray();
      survivorKept = ReferenceEquals(before[0], after[0]);
      newcomerKept = ReferenceEquals(before[1], after[1]);

      // A reclassification can take damage away again without changing the row's stable key.
      table.OnDerived(Snapshot(Fight("Grul", 0, 140), Fight("MoK", 30, 0)));
      Flush();
      left = BoundRows(table).Select(row => row.Name).ToArray();
    });
    CollectionAssert.AreEqual(new[] { "Grul", "MoK" }, entered);
    CollectionAssert.AreEqual(new[] { "Grul" }, left);
    Assert.IsTrue(survivorKept, "the visible survivor is patched, not replaced");
    Assert.IsTrue(newcomerKept, "even the previously hidden row keeps its instance");
  }

  [TestMethod]
  public void DialsComposeAndHiddenDividersStillBoundGroups()
  {
    var counts = new List<int>();
    var groups = new List<string>();
    var total = 0;
    Sta.Run(() =>
    {
      var table = Paint(Snapshot(Fight("Grul", 0, 90), Fight("MoK", 4_000, 0)));
      var grul = BoundRows(table)[0];
      foreach (var (breaks, tanking) in new[] { (true, true), (false, true), (false, false), (true, false) })
      {
        table.fightShowBreaks.IsChecked = breaks;
        table.fightShowTanking.IsChecked = tanking;
        counts.Add(table.RowCount);
        groups.Add(string.Join(",", table.SectionRowsOf(grul).Select(row => row.Name)));
      }
      table.fightShowTanking.IsChecked = true;
      counts.Add(table.RowCount);
      total = table.RowCountAll;
    });
    CollectionAssert.AreEqual(new[] { 3, 2, 1, 2, 3 }, counts);
    CollectionAssert.AreEqual(new[] { "Grul", "Grul", "Grul", "Grul" }, groups,
      "hiding the divider must not make Select Group mean Select All");
    Assert.AreEqual(3, total);
  }

  private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Normal);

  private static DerivedFight Fight(string name, double beginS, long toOwner) => new()
  {
    Name = name,
    BeginTime = T0 + beginS,
    LastTime = T0 + beginS + 20,
    DamageTotal = toOwner + 90,
    DamageHits = 3,
    DamageToOwner = toOwner,
    DamageByOwner = 90,
  };

  private static DerivedSnapshot Snapshot(params DerivedFight[] fights)
  {
    for (var i = 0; i < fights.Length; i++) fights[i].Id = i + 1;
    return DerivedFightRows.Build(fights, new EntityTimeline(), 0, new DamageFactTable(8), new FightFactIndex());
  }

  private static List<DerivedFightRow> BoundRows(FightTable table) =>
    table.fightGrid.ItemsSource is IEnumerable<DerivedFightRow> rows
      ? rows.ToList() : throw new InvalidOperationException("The pane must bind its visible row list.");

  private static FightTable Paint(DerivedSnapshot snapshot)
  {
    var table = new FightTable();
    table.fightShowBreaks.IsChecked = true;
    table.fightShowTanking.IsChecked = true;
    table.OnDerived(snapshot);
    Flush();
    return table;
  }
}
