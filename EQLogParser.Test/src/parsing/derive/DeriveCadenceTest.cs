using EQLogParser;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser.Test.src.parsing.derive;

/*
 * When the engine is allowed to run another pass (docs/DesignNotes.md → "How often derivation re-runs"). Two separate
 * questions had been conflated into one, and the conflation is what made the derived surfaces go stale: a finished load
 * needs one pass at the end; a live raid tail needs passes while the count keeps moving, because it never offers the
 * silence the old rule waited for.
 *
 * Two constraints on top of that are load-bearing rather than taste. The CEILING has to stay inside
 * `FightManager.FightTimeout` (30 s), which is the quiet rule the damage meter applies to its own board: a snapshot
 * older than that reads as "the raid stopped" and the board blanks itself. And the throttle is derived from the
 * measured cost of the last pass, because derivation runs inside the ingest gate (`CombatCapture.DeriveQuiescent`) — so
 * how often the pump runs decides how much of the load it can hold off.
 */
[TestClass]
public class DeriveCadenceTest
{
    private const long Derived = 100_000L;

    // A 650 ms pass (measured on the largest capture on file) must not come back every few milliseconds.
    [TestMethod]
    public void AFinishedPassSetsThePace()
    {
        Assert.AreEqual(3d, DeriveCadence.LiveIntervalSeconds(0.65), 0.01,
            "a measured pass costs about 650 ms and lands on the floor");
        Assert.AreEqual(4d, DeriveCadence.LiveIntervalSeconds(1.0), 0.01, "cost scales by the multiplier");
        Assert.AreEqual(15d, DeriveCadence.LiveIntervalSeconds(10.0), 0.01,
            "however expensive, a snapshot may not age past the ceiling");

        /*
         * No measurement yet is the FASTEST answer, not the slowest: at the top of a fresh log there is no cost to
         * scale, and asking the player to wait for a pass that has never run would be the old behaviour back.
         */
        foreach (var unmeasured in new[] { 0d, -1d, double.NaN })
        {
            var interval = DeriveCadence.LiveIntervalSeconds(unmeasured);
            Assert.AreEqual(DeriveCadence.FloorSeconds, interval, 0.01,
                $"an unknown cost ({unmeasured}) must not be guessed as a slow pass");
        }
    }

    // The ceiling is the meter's quiet rule, not a taste for round numbers.
    [TestMethod]
    public void TheCadenceStaysInsideTheMetersOwnQuietRule()
    {
        // Read through LiveIntervalSeconds rather than comparing the constants to each other: what matters is that the
        // interval the session actually arms its timer with stays inside the window the meter tolerates.
        var cheap = DeriveCadence.LiveIntervalSeconds(1.0);
        var ruinous = DeriveCadence.LiveIntervalSeconds(1_000d);

        Assert.IsTrue(ruinous < FightProjection.EngagementGapS - 10,
            "a snapshot older than the meter's own expiry makes its board blank itself");
        Assert.IsTrue(cheap < ruinous,
            "a more expensive pass waits proportionally longer — that is the whole mechanism");
    }

    // Idle is free: nothing captured, or nothing the grid does not already show.
    [TestMethod]
    public void AnIdleLogAsksForNothing()
    {
        Assert.AreEqual(DeriveKind.None,
            DeriveCadence.Decide(Derived, Derived, 3600, 0, 3600, 3600, 0),
            "captured == derived must cost nothing, forever — neither lane, however long the log sits");
        Assert.AreEqual(DeriveKind.None,
            DeriveCadence.Decide(0, -1, 3600, 0, double.PositiveInfinity, double.PositiveInfinity, 0),
            "a log with no facts must not spin");
    }

    /*
     * The bug this rule exists for: a raid in progress. Damage arrives every fraction of a second for the whole
     * encounter, so the count NEVER holds still, and a rule that only fires on quiet leaves one snapshot up until
     * somebody presses Re-derive.
     */
    [TestMethod]
    public void ATailingCountRefreshesWithoutSilence()
    {
        const double sinceLastPass = DeriveCadence.FloorSeconds;

        Assert.AreEqual(DeriveKind.Full,
            DeriveCadence.Decide(Derived + 500, Derived, 0.05, 10_000, sinceLastPass, sinceLastPass, 0.65),
            "a live tail must refresh on the cost-aware clock, not on a silence that never comes");

        Assert.AreEqual(DeriveKind.None,
            DeriveCadence.Decide(Derived + 500, Derived, 0.05, 10_000, sinceLastPass / 20, sinceLastPass / 3, 0.65),
            "the same tail must still be throttled — both lanes have a floor, and neither is now");
    }

    /*
     * The cheap lane, which exists because classification (186-261 ms measured) is the whole cost of a pass while projection of
     * the facts that arrived since costs 0-5 ms. Between expensive passes this is what moves the numbers: the meter gets rows over
     * the verdicts it already has rather than waiting out the cadence that pays for rule re-evaluation.
     */
    [TestMethod]
    public void TheCheapLaneRunsBetweenExpensivePasses()
    {
        Assert.AreEqual(DeriveKind.ProjectionOnly,
            DeriveCadence.Decide(Derived + 500, Derived, 0.05, 10_000, DeriveCadence.FastFloorSeconds,
                                       DeriveCadence.FloorSeconds * 0.9, 0.65),
            "past the cheap floor but before the expensive one is due: rows yes, rules no");

        Assert.AreEqual(DeriveKind.None,
            DeriveCadence.Decide(Derived + 500, Derived, 0.05, 10_000, DeriveCadence.FastFloorSeconds * 0.5,
                                       DeriveCadence.FloorSeconds * 0.9, 0.65),
            "and the cheap lane still has a floor of its own — it holds the ingest gate too");
    }

    /*
     * The law the session's bookkeeping has to keep honest: the expensive lane is paced by the clock that measures EXPENSIVE
     * passes, so a stream of cheap ones can never postpone re-classification. If this ever returns ProjectionOnly, a busy raid
     * would run forever on the verdicts it happened to have at minute one, and pets/charms would quietly stop folding.
     */
    [TestMethod]
    public void ACheapPassNeverPushesTheExpensiveOneAway()
    {
        Assert.AreEqual(DeriveKind.Full,
            DeriveCadence.Decide(Derived + 500, Derived, 0.05, 10_000, sinceAnyPassS: 0.01,
                                       sinceFullPassS: DeriveCadence.FloorSeconds, lastFullPassSeconds: 0.65),
            "a cheap pass a hundred milliseconds ago must not buy the rule book another interval");
    }

    // Both lanes hold off while a file is being read: whichever one runs, it parks ingest while it runs.
    [TestMethod]
    public void BothLanesParkWhileAFileIsBeingRead()
    {
        const double loading = 60_000d;

        Assert.AreEqual(DeriveKind.None,
            DeriveCadence.Decide(Derived + (long)loading, Derived, 0.05, loading,
                                       DeriveCadence.FastFloorSeconds * 10, DeriveCadence.CeilingSeconds * 2, 0.65),
            "bulk parks the cheap lane even though it would be nearly free — the loader owns the gate");
    }

    // The end of a file load: the count stopped moving, so one pass finishes the job.
    [TestMethod]
    public void AQuietLoadFiresImmediately()
    {
        Assert.AreEqual(DeriveKind.Full,
            DeriveCadence.Decide(50_000, -1, DeriveCadence.QuietSeconds, 200_000, double.PositiveInfinity,
                                       double.PositiveInfinity, 0),
            "growth that stops is the classic trigger, even at bulk rate — and it asks for the EXPENSIVE lane: at the end of a load "
            + "the rules have the whole capture in front of them, which is when they learn what a cheap refresh cannot");
    }

    /*
     * Bulk load must not re-derive. Every pass runs inside the ingest gate, so passing repeatedly while chewing a file
     * would slow the very load whose progress is being watched; the pass at the end (quiescence) is the one that counts.
     */
    [TestMethod]
    public void ABulkLoadWaitsForQuiet()
    {
        const double loading = 60_000d; // facts/second: a file being read

        Assert.AreEqual(DeriveKind.None,
            DeriveCadence.Decide(Derived + (long)loading, Derived, 0.2, loading, double.PositiveInfinity,
                                       double.PositiveInfinity, 0.65),
            "mid-bulk must park, not pass");
        Assert.AreEqual(DeriveKind.Full,
            DeriveCadence.Decide(Derived + (long)loading, Derived, DeriveCadence.QuietSeconds, loading,
                                       double.PositiveInfinity, double.PositiveInfinity, 0.65),
            "and must fire the moment the load stops moving");

        // A raid's worth of lines is orders of magnitude slower than this and must not be mistaken for it.
        Assert.AreEqual(DeriveKind.Full,
            DeriveCadence.Decide(Derived + 40, Derived, 0.2, 400, DeriveCadence.FloorSeconds,
                                       DeriveCadence.FloorSeconds, 0.65));
    }

    /*
     * The property that lets the session poll faster than it refreshes: the rule reads SECONDS and facts-per-second, so
     * the same log gets the same answer whether it is asked every 250 ms or every 4 s — no threshold may be a per-ask
     * allowance. A growth-per-check threshold would stop seeing bulk load purely by checking more often, which is the
     * one moment re-deriving would park ingest.
     */
    [TestMethod]
    public void TheRuleDoesNotDependOnHowOftenItIsAsked()
    {
        const long capturedNow = Derived + 50_000;

        // Identical ingest (25,000 facts/second) observed over a fast and a slow window, both mid-bulk.
        foreach (var pollSeconds in new[] { 0.25, 1.0, 4.0 })
        {
            var factsPerSecond = 25_000d;
            Assert.AreEqual(DeriveKind.None,
                DeriveCadence.Decide(capturedNow, Derived, 0.1, factsPerSecond, double.PositiveInfinity,
                                           double.PositiveInfinity, 0.65),
                $"bulk ingest at a {pollSeconds}s poll must still read as bulk");
        }

        // Same for the quiet verdict: silence of one second is one second however often it was checked.
        Assert.AreEqual(DeriveKind.Full,
            DeriveCadence.Decide(capturedNow, Derived, DeriveCadence.QuietSeconds, 0, double.PositiveInfinity,
                                       double.PositiveInfinity, 0.65));
        Assert.AreEqual(DeriveKind.None,
            DeriveCadence.Decide(capturedNow, Derived, DeriveCadence.QuietSeconds * 0.5, 0, 0, 0, 0.65),
            "half the quiet window is not the quiet window, whatever the poll rate");

        /*
         * And the two verdicts stay independent in the way only a time-based rule can be: BULK parks whatever the clock
         * says (a file being read is left to quiescence, even immediately after a pass), while QUIET goes whatever the
         * rate is. A tick-counting version of either one flips when the poll rate changes, which is the bug this shape
         * exists to make impossible.
         */
        Assert.AreEqual(DeriveKind.None,
            DeriveCadence.Decide(capturedNow, Derived, 0.001, DeriveCadence.BulkFactsPerSecond, 0, 0, 0),
            "bulk parks even one millisecond after the previous pass — a load ends in quiet, not on the interval");
        Assert.AreEqual(DeriveKind.Full,
            DeriveCadence.Decide(Derived + 1, Derived, DeriveCadence.QuietSeconds, 0, double.PositiveInfinity,
                                       double.PositiveInfinity, 0),
            "a single new fact after the quiet window goes now, with no rate to compare against");
    }

    // Live-tail floor: refreshes fast enough to feel live, but every pass parks ingest while it runs.
    [TestMethod]
    public void TheFloorLimitsHowOftenIngestIsParked()
    {
        /*
         * At reference cost (650 ms measured) a pass is a fifth of the floor: below that ratio the derive pump stops
         * being background work and becomes the workload, since ingest waits at the gate for the whole pass. Asserted
         * through the interval a session would arm, so the number has to move if either side changes.
         */
        var interval = DeriveCadence.LiveIntervalSeconds(0.65);
        Assert.IsTrue(interval >= 3 * 0.65,
            $"a {interval}s cadence against a measured 0.65 s pass leaves too little of the cycle for parsing");
    }

    // A session's counters include heals, deaths and identity events — see DeriveEngine.CapturedTotal.
    [TestMethod]
    public void HealingCountsTowardsQuiescence()
    {
        var captured = 1_200_000L;      // damage facts
        var healed = 40_000L;           // heal events, no damage alongside them

        Assert.AreEqual(DeriveKind.None,
            DeriveCadence.Decide(captured + healed, Derived, 0.1, 30_000, 0.2, double.PositiveInfinity, 0),
            "a healing-only stretch is still moving and must read as busy, not quiet — 30k events/second is bulk, and bulk parks until the count stops");
    }

    /*
     * The failure ladder replaced "auto-derive off forever" after one exception. A transient fault (locked file, an AV
     * scan) must retry inside a second; deterministic poison must degrade to a slow LOGGED drip, never to silence. The
     * shape is pinned as numbers because the bug shape is "the wait grew untoward and the meter stopped moving".
     */
    [TestMethod]
    public void ARetryLadderStartsAtOneSecondAndCapsAtAMinute()
    {
      Assert.AreEqual(0, DeriveCadence.RetryDelayS(0), "no failures outstanding - full cadence speed");
      Assert.AreEqual(1, DeriveCadence.RetryDelayS(1), "the first retry is quick: most faults are transient");
      Assert.AreEqual(2, DeriveCadence.RetryDelayS(2));
      Assert.AreEqual(4, DeriveCadence.RetryDelayS(3));
      Assert.AreEqual(32, DeriveCadence.RetryDelayS(6));
      Assert.AreEqual(60, DeriveCadence.RetryDelayS(7), "capped: a poison capture drips once a minute, visibly");
      Assert.AreEqual(60, DeriveCadence.RetryDelayS(500));

      var previous = 0d;
      for (var failures = 1; failures <= 40; failures++)
      {
        var delay = DeriveCadence.RetryDelayS(failures);
        Assert.IsTrue(delay >= previous, $"the ladder never shrinks while failures pile up (rung {failures})");
        Assert.IsTrue(delay > 0 && delay <= 60, "always trying, never slower than a minute");
        previous = delay;
      }
    }
}
