using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using EQLogParser;

namespace EQLogParserTest
{
  /*
   * The boundary a carried cell has to be taken at (`StatsSnapshot`).
   *
   * Layer 2 of the incremental refresh — carry each name's counted row across passes, fold it into the next build, walk only the
   * records that arrived since — was measured before any of this was written, and cost was never the blocker: a row's 1-146 us
   * publish against a 46 ms recount, 99 %+ saved on four captures. What blocked it was that a published row is not a copyable cell:
   * it still holds its running second (publishing must NOT drain it — `StatResumeTest.APublishedReportLeavesTheCurrentSecondAbleToGrow`),
   * and the display model had no copy path at all (`SubStats` was `{ get; } = []`). A cache of published rows would therefore have
   * been a cache of objects the next build mutates: a fold that writes into what the reader is looking at, in both directions.
   * Numbers and the two blockers: docs/DesignNotes.md → "Fold-at-publish measured: the cost cleared the bar, and the row on screen
   * did not"; the boundary's own laws: "The copy a carried cell needs, and the second it has to be taken at".
   *
   * Every assertion here is therefore one of two things: **detached** (no mutable object on both sides) or **drained** (the copy's
   * running second is closed at every level so `MergeStatsRecursive` accepts it, while the live row keeps its scratch and can keep
   * growing this second).
   */
  [TestClass]
  public sealed class StatsSnapshotTest
  {
    private static DamageRecord Rec(string type, string subType, uint total, bool newFrame = false)
      => new() { Attacker = "Zomk", Defender = "Fen Claw", Type = type, SubType = subType, Total = total };

    private static PlayerStats Raid() => new() { Name = "Raid", Total = 10_000, TotalSeconds = 10 };

    /*
     * A row the way a walk builds one — counters on the name AND child rows under it, made through the model's own funnels and real
     * records — because the claim is that a copy of a BUILDER's output survives (its key rules, its ranges, its raw hit amounts),
     * not that a hand-filled object round-trips.
     */
    private static PlayerStats WalkedRow(params (string Type, string SubType, uint Total)[] hits)
    {
      var row = new PlayerStats { Name = "Zomk" };
      Count(row, hits);
      return row;
    }

    /*
     * `DamageStatsBuilder`'s own shape: the record updates the name AND the sub-stat row, and the AMOUNT rides on the sub-stat (that is
     * where HitFreqChart reads it from). The amount is a separate call in the builder, not part of `UpdateDamageStats`, so a fixture that
     * skips it copies a row with no distribution at all and proves nothing about the field `MergeStatsRecursive` knows nothing about.
     */
    private static void Count(PlayerStats row, (string Type, string SubType, uint Total)[] hits)
    {
      for (var i = 0; i < hits.Length; i++)
      {
        var record = Rec(hits[i].Type, hits[i].SubType, hits[i].Total);
        StatsUtil.UpdateDamageStats(row, record, newFrame: i == 0);

        var sub = row.SubStatOf(record.SubType, record.Type);
        StatsUtil.UpdateDamageStats(sub, record, newFrame: i == 0);
        if (record.Total > 0) sub.RecordHitTotal(record.Total, isCrit: false);
      }
    }

    private static IEnumerable<FieldInfo> InstanceFields(Type type)
    {
      for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
      {
        foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
          // The view's subscription, which DetachViewSubscribers drops on purpose: a snapshot no grid displays must not keep its pane alive.
          if (field.Name.Contains("PropertyChanged", StringComparison.Ordinal)) continue;
          yield return field;
        }
      }
    }

    /*
     * THE drift law for a copy boundary: a column added to the model tomorrow has to be answered for here, by name. Scalars ride and are
     * compared value-for-value; every other reference must arrive as a DIFFERENT object, because a shared list or dictionary is not a copy
     * - it is one piece of state with two owners, which is the defect this whole file exists to prevent. Run over a ROW and over a CELL,
     * because layer 2 carries cells, and the private hit-amount lists live on cells rather than on the name.
     */
    [TestMethod]
    public void EveryFieldOfARealRowRidesTheCopyAndNoMutableObjectIsShared()
    {
      var row = WalkedRow(
        (Labels.Melee, "Hits", 100), (Labels.Dd, "Frostbite", 400), (Labels.Dot, "Toxic Bane", 300));
      row.Specials["Discipline"] = "Fist of Sharvalli";
      row.ResistCounts.GetOrAdd("Spell", _ => new ConcurrentDictionary<string, int>())["resisted"] = 3;
      row.Deaths.Add(new DeathEvent { Record = new DeathRecord { Killed = "Zomk", Killer = "Fen Claw" }, BeginTime = 10 });
      row.Ranges.Add(new TimeSegment(100, 105));
      row.AllRanges.Add(new TimeSegment(100, 200));
      row.MoreStats = WalkedRow((Labels.Hot, "Healing Rain", 90));
      row.BestSec = 999;

      AssertNoSharedState(row, StatsSnapshot.Take(row), nameof(PlayerStats));

      if (row is PlayerStats published)
      {
        Assert.AreEqual(1, published.Specials.Count, "setup: a special effect was set for this sweep");
        Assert.AreEqual(1, published.Deaths.Count, "setup: a death was recorded for this sweep");
        Assert.AreEqual(3, published.ResistCounts["Spell"]["resisted"], "setup: a resist count was set for this sweep");
      }

      // The cell is the shape a carried row really is: fewer fields populated, the SAME rule about every one of them.
      var cell = row.SubStats[0];
      Assert.IsTrue(cell.RawHitTotals > 0, "setup: the walked cell carries raw hit amounts (the builder records them on sub-stats)");
      AssertNoSharedState(cell, StatsSnapshot.TakeCell(cell), "sub-stat cell");
    }

    /*
     * Field-by-field equality with no sharing. The "at least one reference field crossed" guard is there because a sweep that inspected
     * nothing would pass as quietly as one that missed a field - and quiet is the failure mode of every copy boundary.
     */
    private static void AssertNoSharedState(PlayerSubStats source, PlayerSubStats copy, string what)
    {
      Assert.AreEqual(source.Total, copy.Total, $"{what}: the damage total did not ride over");
      Assert.AreEqual(source.Hits, copy.Hits, $"{what}: the hit count did not ride over");

      if (source is PlayerStats sourceRow && copy is PlayerStats copyRow)
      {
        Assert.AreEqual(sourceRow.MoreStats is null, copyRow.MoreStats is null, $"{what}: MoreStats crossed inconsistently");
      }

      var references = 0;
      foreach (var field in InstanceFields(copy.GetType()))
      {
        var before = field.GetValue(source);
        var after = field.GetValue(copy);
        var fieldWhat = $"{field.DeclaringType!.Name}.{field.Name} ({field.FieldType.Name})";

        /*
         * The two fields the boundary is allowed to move, and the whole reason a copy is needed at all: they are ONE pair — the running
         * second closes on the COPY (which is what makes it legal fold input for `MergeStatsRecursive`) while the live row keeps writing
         * into its own. Both are named here with their arithmetic rather than skipped, so no third exception to "unchanged" can arrive by
         * this route unannounced.
         */
        if (field.Name.Contains(nameof(PlayerSubStats.BestSecTemp), StringComparison.Ordinal))
        {
          Assert.AreEqual(0L, after, $"{what}: {fieldWhat} crossed un-drained, so the copy is not legal fold input");
          continue;
        }

        if (field.Name.Contains(nameof(PlayerSubStats.BestSec), StringComparison.Ordinal))
        {
          Assert.AreEqual(Math.Max(source.BestSec, source.BestSecTemp), after,
            $"{what}: {fieldWhat} is where the drained running second had to land");
          continue;
        }

        if (field.FieldType.IsValueType || before is string)
        {
          Assert.AreEqual(before, after, $"{what}: {fieldWhat} did not cross the boundary unchanged");
          continue;
        }

        if (before is null)
        {
          Assert.IsNull(after, $"{what}: {fieldWhat} was invented by the copy");
          continue;
        }

        references++;
        Assert.AreNotSame(before, after,
          $"{what}: {fieldWhat} is the SAME object on both sides of StatsSnapshot - whichever side writes next changes the other."
          + " Deep-copy it in the copier, or name it here as deliberately shared and say why.");

        if (before is ICollection originals && after is ICollection replicas)
        {
          Assert.AreEqual(originals.Count, replicas.Count, $"{what}: {fieldWhat} crossed with a different number of entries");
        }
      }

      Assert.IsTrue(references > 0, $"{what}: the sweep found no reference fields at all, so it proves nothing");
    }

    /*
     * The exact defect that blocked layer 2, stated as behaviour. A published row keeps its `BestSecTemp` (by law), and
     * `MergeStatsRecursive` refuses it (by design, rather than losing a second quietly) — so a cache could not take the row at publish
     * time. Taking a DRAINED copy is what unblocks it; taking that copy must leave the live row's scratch alone, because the next batch
     * is still writing into this second.
     */
    [TestMethod]
    public void APublishedRowCannotBeFoldedButItsCopyAlwaysCan()
    {
      var row = new PlayerStats { Name = "Zomk" };
      StatsUtil.UpdateDamageStats(row, Rec(Labels.Melee, "Hits", 100), newFrame: true);
      StatsUtil.UpdateDamageStats(row, Rec(Labels.Melee, "Hits", 60));   // same second, still running
      StatsUtil.UpdateCalculations(row, Raid());                        // a board goes out mid-second

      Assert.AreEqual(160L, row.BestSecTemp, "setup: publishing left the running second whole (that is StatResumeTest's law)");
      Assert.Throws<InvalidOperationException>(() => StatsUtil.MergeStatsRecursive(new PlayerStats { Name = "Zomk" }, row),
        "setup: the published row is refused as fold input — that refusal is what blocked a cache of published rows");

      var cell = StatsSnapshot.Take(row);

      Assert.AreEqual(0L, cell.BestSecTemp, "the copy still carries an open second");
      Assert.AreEqual(row.BestSec, cell.BestSec, "closing the second on the copy must not move the answer");
      Assert.AreEqual(160L, cell.BestSec);
      Assert.AreEqual(160L, row.BestSecTemp, "taking a copy drained the LIVE row: damage still arriving in this second would be lost");

      var folded = new PlayerStats { Name = "Zomk" };
      StatsUtil.MergeStatsRecursive(folded, cell);
      Assert.AreEqual(160L, folded.Total);
      Assert.AreEqual(160L, folded.BestSec, "the folded row says what one uninterrupted pass says");
    }

    /*
     * Draining is not a top-level affair — the same guard fires on every cell of the tree, so each carried cell closes its own second.
     * Asserted recursively: a miss halfway down the tree reads as a correct fold until the day that particular cell is the one moved.
     */
    [TestMethod]
    public void EveryLevelOfTheTreeClosesItsOwnRunningSecond()
    {
      var row = WalkedRow(
        (Labels.Melee, "Hits", 100), (Labels.Dd, "Frostbite", 400), (Labels.Dd, "Frostbite", 50),
        (Labels.Dot, "Toxic Bane", 60));
      Assert.IsTrue(Temps(row).Any(static t => t > 0), "setup: a walked row carries open seconds below the name as well");

      var copy = StatsSnapshot.Take(row);
      AssertDrainedAgainst(row, copy);

      // Idempotence: a copy of a copy is the same answer, and nothing re-opens a second.
      var again = StatsSnapshot.TakeCell(copy);
      Assert.AreEqual(copy.Total, again.Total);
      AssertDrainedAgainst(copy, again);
    }

    /*
     * THE point of a copy: what either side does afterwards stays on its side. Both directions are asserted because the failure modes
     * are one-directional — a shared list lets the builder's next records leak into the carried cell (numbers read high), and handing
     * the grid the same object the cache holds lets a fold leak into what the reader is looking at.
     */
    [TestMethod]
    public void WhatEitherSideWritesAfterwardsStaysOnItsSide()
    {
      var row = WalkedRow((Labels.Dd, "Frostbite", 100), (Labels.Dd, "Frostbite", 200));
      row.Ranges.Add(new TimeSegment(100, 110));
      var cell = StatsSnapshot.Take(row);
      var totalBefore = cell.Total;
      var cellsBefore = cell.SubStats.Count;

      row.Ranges.Add(new TimeSegment(500, 520));
      Count(row, [(Labels.Dd, "Frostbite", 300), (Labels.Dd, "Chrono-Contusion", 400)]);

      Assert.AreEqual(totalBefore, cell.Total, "damage counted after the copy reached the carried cell");
      Assert.AreEqual(cellsBefore, cell.SubStats.Count, "a spell row opened after the copy appeared inside it");
      Assert.AreEqual(1, cell.Ranges.TimeSegments.Count, "the cell's seconds are the live row's own list");

      // And back the other way: the cell is a private object now.
      Count(cell, [(Labels.Dd, "Frostbite", 9000)]);
      Assert.AreEqual(totalBefore + 300 + 400, row.Total, "writing into the copy reached the live row");
    }

    /*
     * The trap the design notes call out about the builder's own rule: `SubStatLookup` NEVER scans a list, so a copied row whose child
     * index was not rebuilt mints a SECOND row for a key the board already has — and every magnitude still adds up, so nothing else sees it.
     */
    [TestMethod]
    public void TheCopiedCellKeepsItsOwnSubStatRowsAndRefusesToDuplicateOne()
    {
      var row = WalkedRow(
        (Labels.Dd, "Frostbite", 100), (Labels.Dot, "Toxic Bane", 20), (Labels.Dd, "Frostbite", 30));

      var copy = StatsSnapshot.Take(row);

      Assert.AreEqual(2, copy.SubStats.Count, "setup: two sub-stat rows walked");
      var existing = copy.SubStats[0];
      Assert.AreSame(existing, copy.SubStatOf(existing.Name, existing.Type),
        "asking the copy for a key it already holds opened a second row — its child index was not rebuilt over the copies");

      // The index answers with the copy's OWN instances: an index maps keys to row objects, and those are different objects now.
      Assert.AreNotSame(row.SubStats[0], copy.SubStats[0]);
      Assert.AreEqual(row.SubStats[0].Total, copy.SubStats[0].Total);

      // A key the row does not know still opens exactly one new row on the copy, without touching the original's count.
      copy.SubStatOf("Brand of Shadows", Labels.Dd).Total = 1;
      Assert.AreEqual(3, copy.SubStats.Count);
      Assert.AreEqual(2, row.SubStats.Count, "the copy's child list is the original's");
    }

    /*
     * `TimeRange.Add(List<TimeSegment>)` ADOPTS the segments it is handed and welds them later — which is why summing one row's seconds
     * rewrites spans another reader holds (docs/DesignNotes.md → "Three boards, three workers": a build may never add to a TimeRange it
     * adopted). So a copied range must hold its OWN segment objects; asserted through the adoption door itself.
     */
    [TestMethod]
    public void ASecondAdoptedAfterTheCopyCannotWeldItselfIntoTheCell()
    {
      var row = WalkedRow((Labels.Dd, "Frostbite", 100));
      row.Ranges.Add(new TimeSegment(100, 110));
      row.AllRanges.Add(new TimeSegment(100, 400));

      var copy = StatsSnapshot.Take(row);
      var end = copy.Ranges.TimeSegments[^1].EndTime;

      row.Ranges.Add([new TimeSegment(end + 40, end + 80)]);
      row.AllRanges.TimeSegments[0].EndTime = 999;   // welding: the live row's segment object is rewritten in place

      Assert.AreEqual(1, copy.Ranges.TimeSegments.Count, "the cell's span list is the row's own");
      Assert.AreEqual(end, copy.Ranges.TimeSegments[^1].EndTime);
      Assert.AreEqual(400d, copy.AllRanges.TimeSegments[0].EndTime, "a welded segment moved the carried cell's window");
      Assert.AreNotSame(row.Ranges.TimeSegments[0], copy.Ranges.TimeSegments[0], "segments were adopted rather than copied");
    }

    /*
     * The hit distribution is the state `MergeStatsRecursive` has no term for, and it lives in private fields that hold either raw
     * amounts or materialized dictionaries depending on whether anybody opened HitFreqChart. Both shapes must cross, because a cache
     * that dropped them would silently empty that chart for every carried name.
     */
    [TestMethod]
    public void TheHitDistributionCrossesInWhicheverShapeItIsLeft()
    {
      var row = WalkedRow(
        (Labels.Dd, "Frostbite", 100), (Labels.Dd, "Frostbite", 200), (Labels.Dd, "Frostbite", 300));

      // The amounts live on the SUB-STAT cell (`DamageStatsBuilder` records them there, next to `UpdateDamageStats`), which is exactly
      // where a carried cell has to carry them from - and exactly where `MergeStatsRecursive` has nothing to say about them.
      var cell = row.SubStats[0];
      Assert.AreEqual(3, cell.RawHitTotals, "setup: nobody asked for a histogram, so the amounts ride as lists");

      var unasked = StatsSnapshot.Take(row).SubStats[0];
      Assert.AreEqual(3, unasked.RawHitTotals, "raw amounts did not cross");
      Assert.IsFalse(unasked.HitFreqMaterialized, "taking a copy materialized a histogram nobody asked for (and released the list)");

      // Somebody opens the chart on the original cell: it answers with dictionaries and drops its lists. A copy taken AFTER that has to
      // carry dictionaries - arriving "unmaterialized" here would mean arriving EMPTY, since the amounts are already gone.
      _ = cell.CritFreqValues;
      _ = cell.NonCritFreqValues;
      Assert.IsTrue(cell.HitFreqMaterialized);
      Assert.AreEqual(0, cell.RawHitTotals, "setup: materializing released the raw list (Attempt's own law)");

      var asked = StatsSnapshot.TakeCell(cell);
      Assert.IsTrue(asked.HitFreqMaterialized, "a materialized distribution arrived unmaterialized");
      Assert.AreEqual(3L, asked.NonCritFreqValues.Values.Sum());
      Assert.AreEqual(0, asked.RawHitTotals, "both halves were carried, which costs more than keeping either");

      // Neither copy notices the row going on counting.
      Count(row, [(Labels.Dd, "Frostbite", 4242)]);
      Assert.AreEqual(3, unasked.RawHitTotals);
      Assert.AreEqual(3L, asked.NonCritFreqValues.Values.Sum());
    }

    /*
     * The law layer 2 stands on, pinned before the cache exists: a cell taken from a PUBLISHED row (so with an open running second,
     * drained by the copy rather than by hand) plus a freshly counted remainder folds into the same row as one uninterrupted count. If
     * this ever fails, carrying cells is not a fast path — it is a different answer, and every magnitude in the grid would be quietly wrong.
     * The reference closes its own second at the same place, because that boundary is what the split IS (`RecursiveFoldEquivalenceTest`
     * establishes the fold's arithmetic; this adds the snapshot as the thing that closes it).
     */
    [TestMethod]
    public void ACarriedCellPlusTheNewRecordsFoldsIntoWhatOneCountWouldSay()
    {
      var before = new (string, string, uint)[] { (Labels.Melee, "Hits", 100), (Labels.Dd, "Frostbite", 400), (Labels.Dot, "Toxic Bane", 70) };
      var after = new (string, string, uint)[] { (Labels.Dd, "Frostbite", 900), (Labels.Melee, "Hits", 25), (Labels.Dd, "Brand of Shadows", 500) };

      var reference = new PlayerStats { Name = "Zomk" };
      Count(reference, before);
      CloseAll(reference);
      Count(reference, after);
      CloseAll(reference);   // both sides are read at the end of their own last second, which is the only fair comparison

      var walked = new PlayerStats { Name = "Zomk" };
      Count(walked, before);
      StatsUtil.UpdateCalculations(walked, Raid());          // a board goes out: the running second stays open, by law
      var cell = StatsSnapshot.Take(walked);                 // the cache's moment: detached AND drained, no hand-closing
      Assert.AreEqual(0L, cell.BestSecTemp);
      Assert.IsTrue(walked.BestSecTemp > 0, "taking the copy drained the live row");

      var news = new PlayerStats { Name = "Zomk" };
      Count(news, after);
      CloseAll(news);

      var folded = new PlayerStats { Name = "Zomk" };
      StatsUtil.MergeStatsRecursive(folded, cell);
      StatsUtil.MergeStatsRecursive(folded, news);

      Assert.AreEqual(reference.Total, folded.Total, "carried cell + new records is not what one count says");
      Assert.AreEqual(reference.Hits, folded.Hits);
      Assert.AreEqual(reference.BestSec, folded.BestSec, "the best second survived the copy boundary differently than the walk's own");
      CollectionAssert.AreEqual(
        reference.SubStats.Select(static s => $"{s.Key}={s.Total}/{s.Hits}").ToArray(),
        folded.SubStats.Select(static s => $"{s.Key}={s.Total}/{s.Hits}").ToArray(),
        "the carried cell expanded into different rows than the counted one would have");
    }

    private static void CloseAll(PlayerStats row)
    {
      StatsUtil.CloseBestSecond(row);
      foreach (var child in row.SubStats) StatsUtil.CloseBestSecond(child);
      foreach (var child in row.SubStats2) StatsUtil.CloseBestSecond(child);
    }

    /*
     * A group row is a PlayerStats with two lists of its own; a shallow replica would share both. Shared members are exactly the quiet
     * half-copy this boundary refuses, so it gets its own assertion rather than being a footnote.
     */
    [TestMethod]
    public void AGroupRowKeepsItsOwnMembers()
    {
      var group = new GroupEntry { Name = "Group", GroupId = 7 };
      group.Members.Add(WalkedRow((Labels.Melee, "Hits", 100)));
      group.Children.Add(WalkedRow((Labels.Dd, "Frostbite", 20)));

      var copy = (GroupEntry)StatsSnapshot.Take(group);

      Assert.AreEqual(7, copy.GroupId);
      Assert.AreEqual(1, copy.Members.Count);
      Assert.AreNotSame(group.Members, copy.Members, "the member list is shared");
      Assert.AreNotSame(group.Members[0], copy.Members[0], "a member row is shared");
      Assert.AreEqual(group.Members[0].Total, copy.Members[0].Total);
      Assert.AreEqual(1, copy.Children.Count);
      Assert.AreNotSame(group.Children[0], copy.Children[0]);
    }

    /*
     * `MoreStats` hangs one report off another and nothing guarantees the pointers are one-way. A copier that recursed blindly would
     * overflow the stack on a cycle; this one answers with the clone it already made.
     */
    [TestMethod]
    public void MoreStatsCrossesAsACopyAndACycleMeetsItsOwnClone()
    {
      var damage = WalkedRow((Labels.Dd, "Frostbite", 100));
      damage.MoreStats = WalkedRow((Labels.Hot, "Healing Rain", 90));

      var copy = StatsSnapshot.Take(damage);
      Assert.AreEqual("Zomk", copy.MoreStats!.Name);
      Assert.AreNotSame(damage.MoreStats, copy.MoreStats);

      var a = new PlayerStats { Name = "a" };
      var b = new PlayerStats { Name = "b" };
      a.MoreStats = b;
      b.MoreStats = a;

      var copiedA = StatsSnapshot.Take(a);
      Assert.AreSame(copiedA.MoreStats!.MoreStats, copiedA, "the cycle came out as two objects instead of one graph");
    }

    /*
     * A row shape nobody listed is refused BY NAME rather than scalar-copied into sharing its fields: `MemberwiseClone` preserves the
     * runtime type and copies references, so a new subclass with a list of its own would look like a correct copy until somebody
     * appended to it. The message is asserted because the message is the fix instructions.
     */
    [TestMethod]
    public void ARowShapeNobodyListedRefusesRatherThanSharingHalfOfItself()
    {
      var odd = new OddRow { Name = "Zomk" };
      odd.Scratch.Add("a field a shallow replica would share");

      var error = Assert.Throws<InvalidOperationException>(() => StatsSnapshot.Take(odd));
      StringAssert.Contains(error.Message, nameof(OddRow));
      StringAssert.Contains(error.Message, nameof(StatsSnapshot));
    }

    /*
     * The drain law, cell by cell: the copy's running second is closed (`BestSec == max(before, scratch)`, `BestSecTemp == 0`) at EVERY
     * level, and the tree it describes is still the same tree. Written as a recursive pair-walk rather than two flat lists so a mismatch
     * names the cell that moved.
     */
    private static void AssertDrainedAgainst(PlayerSubStats row, PlayerSubStats copy, string path = "row")
    {
      Assert.AreEqual(0L, copy.BestSecTemp, $"{path}: a carried cell still holds an open second, so folding it throws");
      Assert.AreEqual(Math.Max(row.BestSec, row.BestSecTemp), copy.BestSec,
        $"{path}: closing the running second changed this cell's answer");

      Assert.AreEqual(row.SubSubStats.Count, copy.SubSubStats.Count, $"{path}: SubSubStats count");
      for (var i = 0; i < copy.SubSubStats.Count; i++)
      {
        AssertDrainedAgainst(row.SubSubStats[i], copy.SubSubStats[i], $"{path}/{copy.SubSubStats[i].Name}");
      }

      if (row is not PlayerStats sourceRow || copy is not PlayerStats copyRow) return;

      Assert.AreEqual(sourceRow.SubStats.Count, copyRow.SubStats.Count, $"{path}: SubStats count");
      for (var i = 0; i < copyRow.SubStats.Count; i++)
      {
        AssertDrainedAgainst(sourceRow.SubStats[i], copyRow.SubStats[i], $"{path}/{copyRow.SubStats[i].Name}");
      }

      Assert.AreEqual(sourceRow.SubStats2.Count, copyRow.SubStats2.Count, $"{path}: SubStats2 count");
      for (var i = 0; i < copyRow.SubStats2.Count; i++)
      {
        AssertDrainedAgainst(sourceRow.SubStats2[i], copyRow.SubStats2[i], $"{path}/{copyRow.SubStats2[i].Name}");
      }
    }

    private static IEnumerable<long> Temps(PlayerSubStats node)
    {
      if (node is null) yield break;
      yield return node.BestSecTemp;
      foreach (var child in node.SubSubStats) foreach (var temp in Temps(child)) yield return temp;
      if (node is not PlayerStats row) yield break;
      foreach (var child in row.SubStats) foreach (var temp in Temps(child)) yield return temp;
      foreach (var child in row.SubStats2) foreach (var temp in Temps(child)) yield return temp;
    }

    private sealed class OddRow : PlayerStats
    {
      internal List<string> Scratch { get; } = [];
    }
  }
}
