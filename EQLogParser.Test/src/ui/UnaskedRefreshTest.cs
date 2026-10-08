namespace EQLogParser;

/*
 * The policy behind "a click always repaints your boards; a machine's refresh may decline when the answer is expensive".
 *
 * Four surfaces used to rebuild on every derive pass: hiding rows, patching them in place, and the two panes' auto-refresh —
 * all of them reading that as "stay current". Measured on Incogitable (docs/DesignNotes.md → "Where a board build's time actually
 * goes"), a whole-capture selection costs ~1.5 s to produce per pass while the quiet-time cadence
 * runs twice a second, so one person reading a full-night board was scheduling thousands of full builds. Meanwhile a raider
 * hides a row in the middle of a pull — 4 outcomes, 10 ms — and her meter froze for seconds because it was waiting on the same
 * rule as the select-all.
 *
 * The seam is therefore the ASKER, not the size: what an operator did with their hand always lands; only the doors a machine
 * opens for itself may say "not at this size" (the measurements: docs/DesignNotes.md → "Where a board build's time actually goes").
 * These tests hold both halves — an unasked door declines over the line and
 * accepts under it, and no user gesture can be declined at any size (the second law is what stops the first from being
 * re-applied to clicks by someone tidying a condition).
 *
 * The limit is a magnitude, not a tuned number: 100,000 outcomes is one pull's worth of fighting, ~0.6 s at the measured rate
 * (~7.5 outcomes per row per pass on that capture's real grid). A whole night is 1.9 M.
 */
[TestClass]
public class UnaskedRefreshTest
{
    // The owed gesture is process state (the app has one board layer); a test that owes one must not spend a forced build in whichever
    // class runs next, so both ends clear it — same discipline the FCT dials and PerfJournal flag follow.
    [TestInitialize]
    public void Setup() => UnaskedRefresh.ClearGesture();

    [TestCleanup]
    public void Cleanup() => UnaskedRefresh.ClearGesture();

    // Hits are uints on the row (the log writes counts, never negatives); the helper takes plain ints so the tests read as
    // magnitudes rather than casts. `Allows` hands back what it counted, which no test here needs.
    private static DerivedFight Row(long outcomes) => new() { Name = "a mob", DamageHits = (uint)outcomes };

    private static IReadOnlyList<DerivedFight> Rows(int rowCount, long outcomesEach)
    {
        var rows = new List<DerivedFight>(rowCount);
        for (var i = 0; i < rowCount; i++) rows.Add(new DerivedFight { Name = $"mob {i}", Id = i + 1, DamageHits = (uint)outcomesEach });
        return rows;
    }

    private static bool Allows(BoardReason reason, IReadOnlyList<DerivedFight>? rows) => UnaskedRefresh.Allows(reason, rows, out _);

    [TestMethod]
    public void TheUnaskedDoorsAreExactlyThree()
    {
        /*
         * Closed list, asserted from both sides. A new BoardReason has to be classified here — as unasked (a machine's refresh,
         * allowed to decline) or asked (somebody's gesture, never declined) — because the default decides whether a whole-capture
         * selection gets a build it cannot afford, and nobody re-reads this file when a word is added beside it.
         */
        var unasked = new HashSet<BoardReason>();
        var asked = new HashSet<BoardReason>();
        foreach (BoardReason reason in Enum.GetValues<BoardReason>())
        {
            (UnaskedRefresh.IsUnasked(reason) ? unasked : asked).Add(reason);
        }

        CollectionAssert.AreEquivalent(
            new[] { BoardReason.ContentMoved, BoardReason.RowEdited, BoardReason.SnapshotSwap },
            unasked.OrderBy(r => r).ToArray(),
            "the doors that refresh themselves");
        CollectionAssert.AreEquivalent(
            new[] { BoardReason.SelectCommand, BoardReason.MenuClose, BoardReason.SettleTick, BoardReason.Manual },
            asked.OrderBy(r => r).ToArray(),
            "the doors a hand opens");
    }

    [TestMethod]
    public void AUserGestureIsNeverDeclinedOverSize()
    {
        // A select-all is the whole night (1.9 M outcomes measured) and it still builds: the cost of a click you made is the
        // product's problem, not something the pane gets to skip. This is the law that keeps the budget off the click path.
        var night = Rows(4646, 400);
        foreach (var reason in new[] { BoardReason.SelectCommand, BoardReason.MenuClose, BoardReason.SettleTick, BoardReason.Manual })
        {
            Assert.IsTrue(Allows(reason, night), $"{reason} must never be declined");
        }

        // Null and empty are the "nothing to build" states, and they are allowed through by every door rather than counted:
        // an empty selection is a real answer (a Clear that must blank the boards), not an expensive question.
        Assert.IsTrue(Allows(BoardReason.SelectCommand, null));
        Assert.IsTrue(Allows(BoardReason.ContentMoved, null));
        Assert.IsTrue(Allows(BoardReason.SelectCommand, []));
        Assert.IsTrue(Allows(BoardReason.ContentMoved, []));
    }

    [TestMethod]
    public void ALargeSelectionIsDeclinedForAnUnaskedPass()
    {
        // The case this exists for: the full-night grid refreshed by itself.
        Assert.IsFalse(Allows(BoardReason.ContentMoved, Rows(4646, 400)));
        Assert.IsFalse(Allows(BoardReason.RowEdited, Rows(4646, 400)));
        Assert.IsFalse(Allows(BoardReason.SnapshotSwap, Rows(4646, 400)));

        // A pull's worth of rows, which is what a live raid actually has selected: the auto-refresh keeps working there, or the
        // boards behind the meter would be the only thing that stays stale.
        Assert.IsTrue(Allows(BoardReason.ContentMoved, Rows(12, 800)));
    }

    [TestMethod]
    public void TheBoundaryIsAllowedAndOneMoreIsNot()
    {
        // Pinned as behaviour at both sides of the line, because `>` and `>=` read the same until somebody changes the constant.
        var atLimit = (int)(UnaskedRefresh.MaxOutcomes / 2);
        Assert.IsTrue(Allows(BoardReason.ContentMoved, Rows(2, atLimit)),
            "exactly the limit is affordable by definition");

        var over = (int)(UnaskedRefresh.MaxOutcomes / 2) + 1;
        Assert.IsFalse(Allows(BoardReason.ContentMoved, Rows(2, over)),
            "one outcome past the limit is a decline — that is what the comparison says");
    }

    [TestMethod]
    public void ACostCountsBothDirectionsOfEveryRow()
    {
        /*
         * Damage taken is on the same facts as damage dealt (FightProjection sums both into one row), so a board's work scales
         * with OUTCOMES, not with the damage column a reader sees. A row of pure tanking still costs a walk: 60 rows × 2,000
         * taken = 120,000 outcomes, over the line even though its DamageHits are zero.
         */
        var takenOnly = Rows(60, 0);
        foreach (var row in takenOnly) row.TankHits = 2000;
        Assert.IsFalse(Allows(BoardReason.ContentMoved, takenOnly));

        // Same rows, half the outcomes: under the line.
        var halved = Rows(60, 0);
        foreach (var row in halved) row.TankHits = 999;
        Assert.IsTrue(Allows(BoardReason.ContentMoved, halved));
    }

    [TestMethod]
    public void AnOperatorWriteOwnsThePassItCaused()
    {
        /*
         * A pet claim or a verdict is a click whose result only exists AFTER the next pass, and that pass announces ContentMoved/
         * RowEdited. Without the debt, a whole-capture selection would decline the operator's own act — the budget written to keep
         * traffic off the reading surface would swallow the one thing that deserves a build. So the write lends its gesture, and the
         * promotion is what the announce applies (Manual is never declined) rather than a size that got lucky.
         */
        var night = Rows(4646, 400);
        Assert.IsFalse(Allows(BoardReason.ContentMoved, night), "the same announce without a write behind it IS declined");

        UnaskedRefresh.OweGesture("pet claim Picklepaw -> Beorun");
        Assert.IsTrue(UnaskedRefresh.TryConsumeGesture(out var gesture));
        Assert.AreEqual("pet claim Picklepaw -> Beorun", gesture, "the log has to name which act was owed");

        // Consumed: the next pass is traffic again, and the phrase came back so the caller can announce Manual with a reason.
        Assert.IsFalse(UnaskedRefresh.TryConsumeGesture(out _), "one write owes one announce, not every pass forever");
    }

    [TestMethod]
    public void ADebtorThatIsNeverPaidDoesNotLeakIntoTheNextSession()
    {
        UnaskedRefresh.OweGesture("verdict Picklepaw = Pet");

        // ClearForNewCapture calls this: rows from the log that just closed cannot be rebuilt by this capture's pass, and a debt
        // carried across would spend a new capture's first announce on the previous night's click.
        UnaskedRefresh.ClearGesture();

        Assert.IsFalse(UnaskedRefresh.TryConsumeGesture(out _));
    }

    [TestMethod]
    public void ARowEditIsNeverDeclined()
    {
        /*
         * A cheap edit is cheap whatever the selection's size would have been: an announce over a handful of outcomes never reaches
         * the limit, so no policy text is consulted in practice — which is why a live pull's boards keep updating on their own while
         * a night-long grid does not.
         */
        var small = Rows(10, 1);
        Assert.IsTrue(Allows(BoardReason.RowEdited, small));
        Assert.IsTrue(Allows(BoardReason.ContentMoved, small));

        // And an edit to one row inside a huge selection is cheap too: the cost is the number of outcomes in the rows being
        // (re)presented, which is why HideFight hands over the touched row rather than the whole selection.
        Assert.IsTrue(Allows(BoardReason.RowEdited, new[] { Row(4) }));
    }
}
