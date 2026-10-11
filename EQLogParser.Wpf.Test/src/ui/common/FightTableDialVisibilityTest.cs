using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * What the two row dials do to the Fight List, measured through the pane's grid (2026-11).
 *
 * The field report these came from: "i uncheck tanking, the selection happens quickly but nothing actually happens". The rule was never the
 * problem — hiding the rows nobody fought is one predicate. The mechanism was: the GRID held that predicate (`View.Filter`), and a grid stores
 * selection by ROW INDEX while re-filtering reshuffles which fight sits at each index underneath it. On top of that the pane had to abandon its
 * incremental patch whenever the filter was on, because `Fight.DamageToOwner` is a field a patch mutates and this build of Syncfusion has no
 * live-filter refresh — so the unchecked mode rebuilt every row, every pass, while the checked mode patched a few of many.
 *
 * Visibility belongs to the pane now (`ShownRows`; the projection's own laws are headless-tested in EQLogParser.Test/src/ui/ShownRowsTest.cs),
 * and these tests pin what only this assembly can reach: that the list bound to the grid IS the visible list, that a live pass still PATCHES
 * with the dial off (the regression), that the two dials compose, and that a selection carries across a toggle by instance — or leaves with the
 * row and says so out loud.
 *
 * Two constraints inherited from this suite's history (see FightTableTimeColumnTest): the grid is never woken up to its Loaded path in a host with
 * no PresentationSource, so anything needing a realized view reports INCONCLUSIVE rather than passing on an empty collection or wedging the STA
 * thread; and reading rows goes through ItemsSource (plain data), never View.Records. Otherwise the same construction contract as
 * FightTableSessionSwitchTest — STA thread, stubbed app-level StaticResources.
 */
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

  // Selection lives in the grid's view, which materializes only once the control loads. No view, nothing to ask: the honest answer is INCONCLUSIVE.
  private static void RequireSelectionSurface(FightTable table)
  {
    if (table.fightGrid.View is null) Assert.Inconclusive("the grid's view never materialized in this host; selection needs a realized grid");
  }

  // `toOwner` is the raid's output ON this anchor: zero is what the Tanking dial hides — a row that exists only because the mob was hitting us.
  private static DerivedFight Fight(string name, double beginS, double lastS, long toOwner) =>
    new()
    {
      Name = name,
      Id = 1,
      BeginTime = T0 + beginS,
      LastTime = T0 + lastS,
      DamageTotal = 100,
      DamageHits = 3,
      DamageToOwner = toOwner,
      DamageByOwner = 90,
    };

  private static DerivedSnapshot Snapshot(params DerivedFight[] fights) =>
    Stamp(DerivedFightRows.Build(fights, new EntityTimeline(), 0, new DamageFactTable(8), new FightFactIndex()), 0);

  private static DerivedSnapshot Stamp(DerivedSnapshot snapshot, int sessionId)
  {
    snapshot.SessionId = sessionId;
    return snapshot;
  }

  /*
   * The rows bound to the grid — which, since the dials stopped being a grid filter, IS the pane's visible list. ItemsSource is plain data; the
   * alternative (View.Records) would need the load path this suite refuses to wake.
   */
  private static List<DerivedFightRow> OnScreen(FightTable table) =>
    ((IEnumerable<DerivedFightRow>)table.fightGrid.ItemsSource!).ToList();

  private static FightTable Paint(DerivedSnapshot snapshot)
  {
    var table = new FightTable();
    table.OnDerived(snapshot);
    Flush();
    return table;
  }

  [TestMethod]
  public void UncheckingTankingLeavesTheRowsNobodyFoughtOffTheList()
  {
    Sta.Run(() =>
    {
      var table = Paint(Snapshot(Fight("Grul", 0, 20, toOwner: 90), Fight("MoK", 30, 50, toOwner: 0)));
      Assert.AreEqual(2, table.RowCount, "the fixture: both fights are on the list with the dials at their defaults");

      table.fightShowTanking.IsChecked = false;
      Flush();

      var shown = OnScreen(table);
      Assert.AreEqual(1, shown.Count, "the row whose whole story is incoming damage leaves");
      Assert.AreEqual("Grul", shown[0].Name, "and the fight the raid actually damaged stays");
      Assert.AreEqual(2, table.RowCountAll, "hiding is a view decision: the capture's own rows are not the dial's to delete");
    });
  }

  /*
   * The regression this change exists for. With Tanking off the pane used to rebuild every row on every pass, because the grid filtered on a field
   * the patch mutates and there is no live-filter refresh on this control — so the state a live raid spends its night in (unfiltered, unsorted) was
   * exactly the state that could not be patched. A pass now keeps the row objects alive with the dial off: same reference, updated numbers.
   */
  [TestMethod]
  public void WithTankingOffANextPassStillPatchesInsteadOfRebuildingTheList()
  {
    Sta.Run(() =>
    {
      var table = Paint(Snapshot(Fight("Grul", 0, 20, toOwner: 90), Fight("MoK", 30, 50, toOwner: 0)));
      table.fightShowTanking.IsChecked = false;
      Flush();

      var before = OnScreen(table).Single(row => row.Name == "Grul");
      table.OnDerived(Snapshot(Fight("Grul", 0, 25, toOwner: 140), Fight("MoK", 30, 50, toOwner: 0)));
      Flush();

      var after = OnScreen(table).Single(row => row.Name == "Grul");
      Assert.AreSame(before, after, "a live pass with the dial off patches the row in place rather than handing the grid a new one");
      Assert.AreEqual(1, OnScreen(table).Count, "and the dial still decides what is on the list afterwards");
    });
  }

  [TestMethod]
  public void UncheckingInactivityTakesTheSpacerOutAndTankingDecidesTheRest()
  {
    // Both dials are one predicate over one list, so they compose. This asserts neither dial leaks a decision into the other's rows.
    Sta.Run(() =>
    {
      var table = Paint(Snapshot(Fight("Grul", 0, 20, toOwner: 90), Fight("MoK", 4_000, 4_030, toOwner: 0)));
      Assert.AreEqual(3, OnScreen(table).Count, "two fights and the gap between them");

      table.fightShowBreaks.IsChecked = false;
      Flush();
      Assert.AreEqual(2, OnScreen(table).Count, "the spacer is what that dial owns, and nothing else");

      table.fightShowTanking.IsChecked = false;
      Flush();
      Assert.AreEqual(1, OnScreen(table).Count);
      Assert.AreEqual("Grul", OnScreen(table)[0].Name);

      // Back to both on: everything returns, in the capture's own order.
      table.fightShowBreaks.IsChecked = true;
      table.fightShowTanking.IsChecked = true;
      Flush();
      Assert.AreEqual(3, OnScreen(table).Count);
    });
  }

  /*
   * The click that "did nothing". A selection carried across a toggle has to be the SAME row object, not an equivalent one: the control keys its
   * highlight, and everything downstream of it, off that reference. The build this replaced re-mapped rows underneath a filter that was reordering
   * the list, which is how a highlight came to mean a different fight than it sat on.
   */
  [TestMethod]
  public void ASelectedFightThatStaysVisibleKeepsItsSelectionAcrossTheDial()
  {
    Sta.Run(() =>
    {
      var table = Paint(Snapshot(Fight("Grul", 0, 20, toOwner: 90), Fight("MoK", 30, 50, toOwner: 0)));
      RequireSelectionSurface(table);

      var grul = OnScreen(table).First(row => row.Name == "Grul");
      table.SelectRowForTest(grul);

      table.fightShowTanking.IsChecked = false;
      Flush();

      Assert.AreEqual(1, OnScreen(table).Count);
      Assert.IsTrue(table.IsSelectedForTest(grul), "the row that survived the toggle keeps its highlight");
      Assert.AreSame(grul, OnScreen(table)[0], "and it is the same object, so the grid cannot point at a different fight than it shows");
    });
  }

  [TestMethod]
  public void ASelectedFightThatTheDialHidesLeavesTheSelectionAndSaysSo()
  {
    /*
     * The other half, with a board attached: a row that stops being visible cannot go on describing itself underneath. Dropping it from the
     * selection is only half of it — the pane must also ASK, or the report keeps showing the fight the list just lost. A dial is a hand, so this is
     * an operator gesture (BoardReason.DialChange), never a pass-driven one that a budget could answer for.
     */
    Sta.Run(() =>
    {
      var table = Paint(Snapshot(Fight("Grul", 0, 20, toOwner: 90), Fight("MoK", 30, 50, toOwner: 0)));
      RequireSelectionSurface(table);

      var asked = new List<BoardRequest>();
      table.DerivedSelectionChanged += (request, _) => asked.Add(request);

      var mok = OnScreen(table).First(row => row.Name == "MoK");
      table.SelectRowForTest(mok);

      table.fightShowTanking.IsChecked = false;
      Flush();

      Assert.IsFalse(table.IsSelectedForTest(mok), "a hidden row is not selected");
      Assert.IsTrue(asked.Any(request => request.Reason == BoardReason.DialChange),
                    "and the boards are told the list moved, rather than being left with the fight that disappeared");
    });
  }
}
