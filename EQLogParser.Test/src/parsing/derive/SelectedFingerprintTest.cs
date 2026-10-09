using System;
using System.Collections.Generic;
using EQLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * A live tail moves the capture-wide content stamp on EVERY pass — one new fact anywhere is enough — and rebuilding boards on that
 * alone made a selected, already-dead mob clear and reload its damage summary once per pass over figures that never changed (field
 * report 2026-10-08; 157 [ContentMoved] rebuilds of one fight in a few minutes). The rule that stops it lives here, in Core, because
 * the failure is about WHICH facts matter, not about grids.
 *
 * Two directions are pinned. Too eager is the bug that shipped: facts on OTHER rows must not move the fingerprint. Too lazy is the
 * worse one hiding behind the fix — a change inside a selected row that fails to move it leaves stale numbers on screen forever, so
 * every field a board displays gets its own case, and each one says which board would have gone stale.
 */
[TestClass]
public class SelectedFingerprintTest
{
    [TestMethod]
    [Description("Facts landing on rows nobody selected describe the same boards: that is the whole point, and the capture-wide stamp cannot see it.")]
    public void AFactAnywhereElse_LeavesTheSelectedRowsAlone()
    {
        // Pass 1 and pass 2 of the SAME selected mob: identical figures, re-handed as fresh objects (a full projection rebuild does
        // exactly that). Around it, another mob goes from nearly nothing to a million - the raid keeps fighting elsewhere.
        var minePass1 = Row(1, total: 500_000, byOwner: 400_000, toOwner: 100_000, begin: 10, last: 40);
        var minePass2 = Row(1, total: 500_000, byOwner: 400_000, toOwner: 100_000, begin: 10, last: 40);
        var otherPass1 = Row(2, total: 10, byOwner: 10, toOwner: 0, begin: 1, last: 2);
        var otherPass2 = Row(2, total: 9_000_000, byOwner: 8_000_000, toOwner: 1_000_000, begin: 1, last: 99);

        Assert.AreEqual(SelectedFingerprint.Of([minePass1]), SelectedFingerprint.Of([minePass2]),
            "a selection whose own rows did not move must read identical - rebuilding on the capture's fact count is the flicker");
        Assert.AreNotEqual(SelectedFingerprint.Of([otherPass1]), SelectedFingerprint.Of([otherPass2]),
            "the probe itself must be able to see a row grow, or the equality above proves nothing");
        Assert.AreNotEqual(SelectedFingerprint.Of([minePass1, otherPass2]), SelectedFingerprint.Of([minePass1]),
            "widening the SELECTION is a new question, and must rebuild");
    }

    [TestMethod]
    [Description("Each figure the boards print is inside the fingerprint - which column goes stale if it is left out.")]
    public void EveryDisplayedFigureMovesTheFingerprint()
    {
        var baseRow = Row(7, total: 1000, byOwner: 700, toOwner: 300, begin: 5, last: 9);
        var baseline = SelectedFingerprint.Of([baseRow]);

        var cases = new (string What, DerivedFight Row)[]
        {
            ("damage dealt (DamageTotal)", Row(7, total: 1001, byOwner: 700, toOwner: 300, begin: 5, last: 9)),
            ("the row's own side split (DamageByOwner)", Row(7, total: 1000, byOwner: 701, toOwner: 300, begin: 5, last: 9)),
            ("damage taken - the tanking board", Row(7, total: 1000, byOwner: 700, toOwner: 301, begin: 5, last: 9)),
            ("TankTotal", Row(7, total: 1000, byOwner: 700, toOwner: 300, begin: 5, last: 9, tank: 42)),
            ("the fight's span (LastTime - DPS denominator)", Row(7, total: 1000, byOwner: 700, toOwner: 300, begin: 5, last: 10)),
            ("when it last took a swing (BeginTime)", Row(7, total: 1000, byOwner: 700, toOwner: 300, begin: 6, last: 9)),
            ("its newest damage-side second (LastDamageTime)", Row(7, total: 1000, byOwner: 700, toOwner: 300, begin: 5, last: 9, lastDamage: 8)),
            ("its newest tanking second (LastTankingTime)", Row(7, total: 1000, byOwner: 700, toOwner: 300, begin: 5, last: 9, lastTank: 8)),
            ("dead - the outcome a reader colours by", Row(7, total: 1000, byOwner: 700, toOwner: 300, begin: 5, last: 9, dead: true)),
            ("charmed - the same facts re-routed", Row(7, total: 1000, byOwner: 700, toOwner: 300, begin: 5, last: 9, charmed: true)),
        };

        foreach (var (what, row) in cases)
        {
            Assert.AreNotEqual(baseline, SelectedFingerprint.Of([row]),
                $"{what} changed and the boards under this row would have kept showing the old value");
        }
    }

    [TestMethod]
    [Description("A row's identity is part of it: the same numbers on a different fight are a different answer.")]
    public void TheRowIdentityCountsNotJustTheArithmetic()
    {
        Assert.AreNotEqual(
            SelectedFingerprint.Of([Row(1, total: 500, byOwner: 500, toOwner: 0, begin: 1, last: 2)]),
            SelectedFingerprint.Of([Row(2, total: 500, byOwner: 500, toOwner: 0, begin: 1, last: 2)]),
            "two different fights with identical figures must not fingerprint alike");
    }

    [TestMethod]
    [Description("One row's +N must not cancel another's -N into 'nothing happened', so rows hash before they combine.")]
    public void OneRowsGainNeverCancelsAnotherRowsLoss()
    {
        var before = SelectedFingerprint.Of([
            Row(1, total: 1000, byOwner: 1000, toOwner: 0, begin: 1, last: 5),
            Row(2, total: 2000, byOwner: 2000, toOwner: 0, begin: 1, last: 5)]);

        var after = SelectedFingerprint.Of([
            Row(1, total: 1500, byOwner: 1500, toOwner: 0, begin: 1, last: 5),
            Row(2, total: 1500, byOwner: 1500, toOwner: 0, begin: 1, last: 5)]);

        Assert.AreNotEqual(before, after, "the SUM is unchanged here; only who did it moved - and the board lists them per row");
    }

    [TestMethod]
    [Description("Selection order is not a question changed: two rows trading places describe identical boards (a rebuild for that is this class's own flicker bug).")]
    public void TheFingerprintIgnoresTheOrderRowsWerePickedIn()
    {
        var a = Row(1, total: 100, byOwner: 60, toOwner: 40, begin: 1, last: 2);
        var b = Row(2, total: 200, byOwner: 150, toOwner: 50, begin: 3, last: 4);

        Assert.AreEqual(SelectedFingerprint.Of([a, b]), SelectedFingerprint.Of([b, a]));
    }

    [TestMethod]
    [Description("NaN means 'this direction never happened' - it must read the same on every pass, and differ from a real second.")]
    public void ADirectionThatNeverHappenedIsStableAcrossPasses()
    {
        Assert.AreEqual(
            SelectedFingerprint.Of([Row(3, total: 10, byOwner: 10, toOwner: 0, begin: 1, last: 2)]),
            SelectedFingerprint.Of([Row(3, total: 10, byOwner: 10, toOwner: 0, begin: 1, last: 2)]),
            "both rows carry NaN direction windows; a NaN payload hashing twice would rebuild forever");

        Assert.AreNotEqual(
            SelectedFingerprint.Of([Row(3, total: 10, byOwner: 10, toOwner: 0, begin: 1, last: 2)]),
            SelectedFingerprint.Of([Row(3, total: 10, byOwner: 10, toOwner: 0, begin: 1, last: 2, lastTank: 7)]));
    }

    [TestMethod]
    [Description("Empty and missing selections answer stably - the pane announces an empty selection to CLEAR the boards, and that must not be 'changed'.")]
    public void AnEmptySelectionIsStableAndDistinctFromNothingAtAll()
    {
        Assert.AreEqual(0L, SelectedFingerprint.Of(null));
        Assert.AreEqual(SelectedFingerprint.Of([]), SelectedFingerprint.Of([]));
    }

    [TestMethod]
    [Description("The decide rule: a moved verdict rebuilds even over identical rows; nothing moved answers no - and 'no' is what stops the flicker.")]
    public void AVerdictMoveRebuildsOverIdenticalRows()
    {
        var fp = SelectedFingerprint.Of([Row(1, total: 5, byOwner: 5, toOwner: 0, begin: 1, last: 2)]);

        Assert.IsTrue(SelectedFingerprint.WorthRebuild(verdictsMoved: true, fp, fp),
            "the same facts re-routing between a mob and X +Pets changes the board without touching one figure on the row");
        Assert.IsFalse(SelectedFingerprint.WorthRebuild(verdictsMoved: false, fp, fp),
            "capture-wide facts moved but nothing this selection reads did - rebuilding here is the reported flicker");
        Assert.IsTrue(SelectedFingerprint.WorthRebuild(verdictsMoved: false, fp + 1, fp));
    }

    [TestMethod]
    [Description("A select-all sized selection stays cheap enough to ask every pass - the guard must not become its own cost.")]
    public void AWholeCaptureSelectionIsStillLinearAndFast()
    {
        var rows = new List<DerivedFight>(4000);
        for (var i = 1; i <= 4000; i++) rows.Add(Row(i, total: i * 37L, byOwner: i * 20L, toOwner: i * 17L, begin: i, last: i + 3));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = SelectedFingerprint.Of(rows);
        var second = SelectedFingerprint.Of(rows);
        sw.Stop();

        Assert.AreEqual(first, second);
        rows[1234].DamageTotal += 1;
        Assert.AreNotEqual(first, SelectedFingerprint.Of(rows), "one row out of 4,000 moving must still be seen");
        // Generous on purpose: this catches an accidental O(n²) (a nested loop over the selection), not a slow CI box.
        Assert.IsTrue(sw.ElapsedMilliseconds < 200, $"asking cost {sw.ElapsedMilliseconds} ms for two passes over 4,000 rows");
    }

    private static DerivedFight Row(int id, long total, long byOwner, long toOwner, double begin, double last,
                                    long tank = 0, double lastDamage = double.NaN, double lastTank = double.NaN,
                                    bool dead = false, bool charmed = false)
        => new()
        {
            Id = id,
            Name = $"mob {id}",
            DamageTotal = total,
            DamageByOwner = byOwner,
            DamageToOwner = toOwner,
            TankTotal = tank,
            BeginTime = begin,
            LastTime = last,
            LastDamageTime = lastDamage,
            LastTankingTime = lastTank,
            Dead = dead,
            CharmedOwned = charmed,
        };
}
