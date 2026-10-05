using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using EQLogParser;

namespace EQLogParser.Wpf.Test
{
  /// <summary>
  /// The row-level laws the derived fight list's incremental update stands on (FightTable.OnDerived → RowPatch). None of them need a
  /// grid: they are about what a row object promises between two derive passes, and each one is a bug if it breaks. Unstable keys mean
  /// the patch refuses every pass and the whole change bought nothing; a content copy that misses a cell means a row whose numbers
  /// moved while its digits stayed; a copy that notifies unchanged cells means the layout work came back.
  ///
  /// Rows are built straight from DerivedFight objects rather than from a log: the pipeline is not the thing under test here, and
  /// this assembly has no harness for it (see <c>DerivedFightRowsTest</c> for the same idiom).
  /// </summary>
  [TestClass]
  public class DerivedFightRowPatchTest
  {
    private const double T0 = 1_000;

    private static DerivedFight Fight(string name, double beginS, double lastS, long damage = 100,
                                      DerivedFightEnd end = DerivedFightEnd.Open) =>
      new()
      {
        Name = name,
        Id = 1,
        BeginTime = T0 + beginS,
        LastTime = T0 + lastS,
        DamageTotal = damage,
        DamageHits = 3,
        DamageToOwner = 10,
        DamageByOwner = damage - 10,
        EndReason = end,
        Dead = end is DerivedFightEnd.Slain or DerivedFightEnd.Charmed,
      };

    // The three rows a pass produces for this fixture: two fights and the inactivity divider between them (the gap is far past the
    // section threshold), so both row kinds are in every snapshot these tests walk.
    private static DerivedSnapshot Snapshot() =>
      DerivedFightRows.Build(
        [Fight("Grul", 0, 20, end: DerivedFightEnd.Slain), Fight("MoK", 4_000, 4_030)],
        new EntityTimeline(), 0, new DamageFactTable(8), new FightFactIndex());

    [TestMethod]
    public void TwoPassesOverTheSameFights_ProduceTheSameRowKeys()
    {
      // The key is what lets pass two find pass one's row. Name and start time are re-read off the same facts every pass, so a row
      // that did not change cannot arrive wearing a different key — if it ever did, patching would stop working in silence.
      var first = Snapshot();
      var second = Snapshot();

      Assert.IsTrue(first.Rows.Any(r => r.IsDivider), "the 4,000 s gap should put an inactivity divider between the two fights");
      Assert.AreEqual(2, first.Rows.Count(r => !r.IsDivider));
      CollectionAssert.AreEqual(first.Rows.Select(r => r.Key).ToList(), second.Rows.Select(r => r.Key).ToList());
    }

    [TestMethod]
    public void EveryRowInTheSameSnapshot_HasItsOwnKey()
    {
      // A duplicate key makes RowPatch refuse the patch outright (it will not guess which of two rows an incoming one is), so a
      // builder that produced one would silently put the pane back on the wholesale path for every pass, forever.
      var keys = Snapshot().Rows.Select(r => r.Key).ToList();

      Assert.AreEqual(keys.Count, keys.Distinct().Count(),
                      "duplicate row keys: " + string.Join(", ", keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key)));

      // Dividers key on where the gap starts, fights on name + start; the prefixes are what keep the two families apart.
      Assert.IsTrue(keys.All(k => k.StartsWith("F:") || k.StartsWith("D:")), string.Join(", ", keys));
    }

    [TestMethod]
    public void TheSameNameTwiceAtDifferentStarts_KeysApart()
    {
      // One name carries many mobs (`A corrupted egg` is slain six times in a row on a real capture), and each life is its own row.
      var snapshot = DerivedFightRows.Build(
        [Fight("Egg", 0, 5, end: DerivedFightEnd.Slain), Fight("Egg", 60, 65, end: DerivedFightEnd.Slain)],
        new EntityTimeline(), 0, new DamageFactTable(8), new FightFactIndex());

      var fights = snapshot.Rows.Where(r => !r.IsDivider).ToList();
      Assert.AreEqual(2, fights.Count);
      Assert.AreEqual(2, fights.Select(r => r.Key).Distinct().Count());
    }

    [TestMethod]
    public void ARebuiltRowForTheSameFight_IsItsEqualDisplay()
    {
      var old = Snapshot();
      var fresh = Snapshot();

      var mismatches = old.Rows.Where((row, i) => !row.SameDisplayAs(fresh.Rows[i])).ToList();
      Assert.AreEqual(0, mismatches.Count,
                      "rebuilt rows differ from the rows they replace: " + string.Join(", ", mismatches.Select(r => r.Name).Take(5)));
    }

    [TestMethod]
    public void CopyingDisplay_MovesEveryCellAndTheFight_ButNotTheSearchMark()
    {
      var target = Snapshot().Rows.First(r => !r.IsDivider);
      var source = Snapshot().Rows.Last(r => !r.IsDivider);
      Assert.AreNotEqual(target.Name, source.Name, "the fixture's two fights must be different rows");

      target.IsSearchResult = true;         // the reader's own mark: a refresh must not be able to erase it
      var targetFight = target.Fight;

      target.CopyDisplayFrom(source);

      Assert.AreEqual(source.Name, target.Name);
      Assert.AreEqual(source.Identity, target.Identity);
      Assert.AreEqual(source.Source, target.Source);
      Assert.AreEqual(source.Begin, target.Begin);
      Assert.AreEqual(source.Last, target.Last);
      Assert.AreEqual(source.Duration, target.Duration);
      Assert.AreEqual(source.Damage, target.Damage);
      Assert.AreEqual(source.Hits, target.Hits);
      Assert.AreEqual(source.Status, target.Status);
      Assert.AreEqual(source.TooltipText, target.TooltipText);

      // The row is the same object, but it points at the pass that just arrived: a selection materializes from here, and leaving the
      // old DerivedFight behind would feed the boards numbers one pass stale while the cells looked current.
      Assert.AreNotSame(targetFight, target.Fight);
      Assert.IsTrue(target.IsSearchResult, "a content copy took the search highlight with it");
    }

    [TestMethod]
    public void ACopyThatChangesNothing_RaisesNoNotifications()
    {
      // The whole point of patching: the ~95 % of rows that did not change do no work and wake no layout.
      var row = Snapshot().Rows.First(r => !r.IsDivider);
      var twin = Snapshot().Rows.First(r => !r.IsDivider && r.Key == row.Key);
      Assert.IsTrue(row.SameDisplayAs(twin));

      var raised = 0;
      ((INotifyPropertyChanged) row).PropertyChanged += (_, __) => raised++;

      row.CopyDisplayFrom(twin);
      Assert.AreEqual(0, raised);
    }

    [TestMethod]
    public void ACopyThatChangesOneCell_RaisesThatCellAlone()
    {
      var row = Snapshot().Rows.First(r => !r.IsDivider);
      var twin = Snapshot().Rows.First(r => !r.IsDivider && r.Key == row.Key);

      row.Damage = row.Damage + 1;                     // differ on exactly one cell
      Assert.IsFalse(row.SameDisplayAs(twin));

      var raised = new List<string>();
      ((INotifyPropertyChanged) row).PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "(none)");

      row.CopyDisplayFrom(twin);
      CollectionAssert.AreEqual(new[] { nameof(DerivedFightRow.Damage) }, raised);
    }

    [TestMethod]
    public void ADividerKeepsItsKeyWhileItsLabelGrows()
    {
      // The gap between two fights is measured against newer facts as the night goes on, so its label grows: "Inactivity > 5 minutes"
      // becoming "> 7 minutes" is the SAME divider. Keyed on the text it would be a row leaving and another arriving every pass.
      var divider = Snapshot().Rows.First(r => r.IsDivider);
      var grown = new DerivedFightRow { IsDivider = true, Key = divider.Key, Name = "Inactivity > 7 minutes" };

      Assert.AreEqual(divider.Key, grown.Key);
      Assert.IsFalse(divider.SameDisplayAs(grown));     // one cell update, not a remove plus an insert

      var raised = new List<string>();
      ((INotifyPropertyChanged) divider).PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "(none)");

      divider.CopyDisplayFrom(grown);
      CollectionAssert.AreEqual(new[] { nameof(DerivedFightRow.Name) }, raised);
    }

    [TestMethod]
    public void APatchedSequenceOfPasses_KeepsEveryRowObjectThatSurvives()
    {
      // The behaviour the pane wants, end to end at the row layer: three passes, a growing list and a row whose damage moves. The
      // objects that stay must be the SAME objects (that is what keeps selection and scroll position), and only the moved cell fires.
      var live = new List<DerivedFightRow>();

      void Pass(List<DerivedFight> fights)
      {
        var next = DerivedFightRows.Build(fights, new EntityTimeline(), 0, new DamageFactTable(8), new FightFactIndex());
        var plan = live.Count == 0 ? null
          : RowPatch.Build(live, next.Rows, static r => r.Key, static (a, b) => a.SameDisplayAs(b), maxChurn: 100);
        if (plan == null)
        {
          live.Clear();
          live.AddRange(next.Rows);
          return;
        }
        RowPatch.Apply(live, plan, static (t, s) => t.CopyDisplayFrom(s));
      }

      Pass([Fight("Grul", 0, 20)]);
      var grul = live.Single();

      Pass([Fight("Grul", 0, 20, damage: 500), Fight("MoK", 4_000, 4_030)]);
      Assert.AreEqual(2, live.Count(r => !r.IsDivider), "two fights, plus whatever divider the gap earns");
      Assert.AreSame(grul, live.First(r => !r.IsDivider && r.Name == "Grul"));
      Assert.AreEqual(500, grul.Damage, "the moved number landed on the object that stayed");

      Pass([Fight("Grul", 0, 20, damage: 500), Fight("MoK", 4_000, 4_030)]);
      Assert.AreSame(grul, live.First(r => !r.IsDivider && r.Name == "Grul"), "a quiet pass must not disturb anything");
    }
  }
}
