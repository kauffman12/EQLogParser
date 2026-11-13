using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using EQLogParser;

namespace EQLogParser.Wpf.Test;

// These tests exercise the pane's bound data without loading a windowless grid.
// Actual highlight/click/scroll behaviour needs a live window and is untested, rather than pinned by an always-inconclusive test.
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
    CollectionAssert.AreEqual(new[] { 3, 2, 1, 1, 3 }, counts);
    CollectionAssert.AreEqual(new[] { "Grul", "Grul", "Grul", "Grul" }, groups,
      "hiding the divider must not make Select Group mean Select All");
    Assert.AreEqual(3, total);
  }

  [TestMethod]
  public void CollapsedBreaksDoNotRelabelTheSnapshotOrChangeGroups()
  {
    var restored = false;
    var sharedFights = false;
    var sourceUntouched = false;
    var groupsUnchanged = false;
    string[] names = [];
    Sta.Run(() =>
    {
      var snapshot = Snapshot(Fight("First", 0, 90), Fight("Hidden", 300, 0), Fight("Last", 800, 90));
      var table = Paint(snapshot);
      var original = BoundRows(table);
      var originalNames = original.Select(row => row.Name).ToArray();
      table.fightShowTanking.IsChecked = false;
      var shown = BoundRows(table);
      names = shown.Select(row => row.Name).ToArray();
      sharedFights = ReferenceEquals(original[0], shown[0]) && ReferenceEquals(original[4], shown[2]);
      sourceUntouched = snapshot.Rows.Select(row => row.Name).SequenceEqual(originalNames)
        && !snapshot.Rows.Contains(shown[1]);
      groupsUnchanged = table.SectionRowsOf(shown[0]).SequenceEqual(new[] { shown[0] })
        && table.SectionRowsOf(shown[2]).SequenceEqual(new[] { shown[2] });
      table.fightShowTanking.IsChecked = true;
      restored = BoundRows(table).SequenceEqual(original)
        && BoundRows(table).Select(row => row.Name).SequenceEqual(originalNames);
    });
    CollectionAssert.AreEqual(new[] { "First", GapName(20, 800), "Last" }, names);
    Assert.IsTrue(sharedFights);
    Assert.IsTrue(sourceUntouched);
    Assert.IsTrue(groupsUnchanged);
    Assert.IsTrue(restored);
  }

  [TestMethod]
  public void AMergedDividerIsUpdatedInPlaceAndReplacedWhenItsHiddenFightBecomesVisible()
  {
    var dividerKept = false;
    var fightsKept = false;
    var boundariesRestored = false;
    string[] grown = [];
    string[] hiddenAgain = [];
    Sta.Run(() =>
    {
      var table = Paint(Snapshot(Fight("First", 0, 90), Fight("Middle", 300, 0), Fight("Last", 800, 90)));
      table.fightShowTanking.IsChecked = false;
      var before = BoundRows(table);
      table.OnDerived(Snapshot(Fight("First", 0, 90), Fight("Middle", 300, 0), Fight("Last", 900, 90)));
      Flush();
      var after = BoundRows(table);
      grown = after.Select(row => row.Name).ToArray();
      dividerKept = ReferenceEquals(before[1], after[1]);
      fightsKept = ReferenceEquals(before[0], after[0]);

      table.OnDerived(Snapshot(Fight("First", 0, 90), Fight("Middle", 300, 50), Fight("Last", 900, 90)));
      Flush();
      var entered = BoundRows(table);
      boundariesRestored = entered.Count == 5 && entered[1].IsDivider && entered[3].IsDivider
        && entered[2].Name == "Middle" && ReferenceEquals(after[0], entered[0])
        && ReferenceEquals(after[2], entered[4]);
      table.OnDerived(Snapshot(Fight("First", 0, 90), Fight("Middle", 300, 0), Fight("Last", 900, 90)));
      Flush();
      hiddenAgain = BoundRows(table).Select(row => row.Name).ToArray();
    });
    CollectionAssert.AreEqual(new[] { "First", GapName(20, 900), "Last" }, grown);
    CollectionAssert.AreEqual(grown, hiddenAgain);
    Assert.IsTrue(dividerKept);
    Assert.IsTrue(fightsKept);
    Assert.IsTrue(boundariesRestored);
  }

  [TestMethod]
  public void ClearingTheCaptureRetiresItsMergedDivider()
  {
    var retired = false;
    Sta.Run(() =>
    {
      var table = Paint(Snapshot(Fight("First", 0, 90), Fight("Hidden", 300, 0), Fight("Last", 800, 90)));
      table.fightShowTanking.IsChecked = false;
      var oldDivider = BoundRows(table)[1];
      table.ClearForNewCapture();
      table.OnDerived(Snapshot(Fight("First", 0, 90), Fight("Hidden", 300, 0), Fight("Last", 800, 90)));
      Flush();
      retired = !ReferenceEquals(oldDivider, BoundRows(table)[1]);
    });
    Assert.IsTrue(retired);
  }

  private static string GapName(double from, double to) => "Inactivity > " + DateUtil.FormatGeneralTime(to - from);

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
