using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * The rule for when the mirror runs another pass, tested apart from the session that feeds it (a DispatcherTimer is not
 * a test harness). Two triggers live here because one was not enough: quiescence answers "the load finished", which is
 * all a closed log ever needs, but a live tail during a raid never offers two silent ticks — and only quiescence was
 * what the surfaces reading the snapshot had, so the fight list, a click's summary and the damage meter held one
 * snapshot for a whole encounter and moved only when Re-derive was pressed.
 */
[TestClass]
public class MirrorDeriveCadenceTest
{
    private const long Derived = 100_000;

    [TestMethod]
    public void ALoadThatStoppedMovingIsDerivedOnTheNextTick()
    {
        Assert.IsTrue(MirrorDeriveCadence.ShouldDerive(Derived + 500, Derived + 500, Derived, 0, 0),
                      "the count held still between two ticks: that is the classic end-of-load trigger");
    }

    [TestMethod]
    public void AnIdleCaptureCostsNothing()
    {
        Assert.IsFalse(MirrorDeriveCadence.ShouldDerive(Derived, Derived, Derived, 3600, 0),
                       "nothing new since the last pass: an open log with a raid having gone home must cost zero");
        Assert.IsFalse(MirrorDeriveCadence.ShouldDerive(0, -1, -1, double.PositiveInfinity, 0),
                       "nothing captured at all (a city log) is not a reason to spin");
    }

    /*
     * The bug this exists for. Facts arriving every tick used to mean "not quiet, wait" forever, so the board froze;
     * growth below the bulk-load threshold now refreshes on the cost-aware clock instead.
     */
    [TestMethod]
    public void ALiveTailRefreshesWithoutWaitingForSilence()
    {
        var sinceLastPass = MirrorDeriveCadence.FloorSeconds;

        Assert.IsTrue(MirrorDeriveCadence.ShouldDerive(Derived + 4_000, Derived + 3_900, Derived, sinceLastPass, 0.65),
                      "a raid in progress: a few facts per tick, no silence, and the board still has to move");

        Assert.IsFalse(MirrorDeriveCadence.ShouldDerive(Derived + 4_000, Derived + 3_900, Derived,
                                                        MirrorDeriveCadence.FloorSeconds - 0.5, 0.65),
                       "but not faster than the floor: every pass parks ingest at the gate while it runs");
    }

    [TestMethod]
    public void AFirstPassArrivesPromptlyRatherThanOnSilence()
    {
        // No pass has ever finished, so the wait is infinity and any tailing tick may start one: this is what stops a
        // freshly opened derived meter from showing an empty board until the log happens to pause.
        Assert.IsTrue(MirrorDeriveCadence.ShouldDerive(50_000, 49_000, -1, double.PositiveInfinity, 0));
    }

    [TestMethod]
    public void ABulkLoadIsLeftToQuiescence()
    {
        // Measured: reading a file through the pipeline runs at roughly 170,000 facts a second, tailing live at tens.
        var loading = MirrorDeriveCadence.BulkFactsPerTick;

        Assert.IsFalse(MirrorDeriveCadence.ShouldDerive(Derived + loading, Derived, Derived, 3600, 0.65),
                       "re-deriving mid-file would park ingest at the gate several times during the one moment throughput matters");

        // And the load's own ending is still caught, the first tick that sees the count stop.
        Assert.IsTrue(MirrorDeriveCadence.ShouldDerive(Derived + loading, Derived + loading, Derived, 3600, 0.65));
    }

    [TestMethod]
    public void TheWaitScalesWithWhatAPassActuallyCosts()
    {
        // 650 ms is the measured cost of one pass over the largest capture on file (2,931,939 facts): the floor.
        Assert.AreEqual(MirrorDeriveCadence.FloorSeconds, MirrorDeriveCadence.LiveIntervalSeconds(0.65), 0.001);

        // A pass in the middle of the range pays for itself four times over.
        Assert.AreEqual(8, MirrorDeriveCadence.LiveIntervalSeconds(2), 0.001);

        // And an expensive pass is spaced out rather than allowed to run continuously.
        Assert.AreEqual(MirrorDeriveCadence.CeilingSeconds, MirrorDeriveCadence.LiveIntervalSeconds(10), 0.001);

        Assert.AreEqual(MirrorDeriveCadence.FloorSeconds, MirrorDeriveCadence.LiveIntervalSeconds(double.NaN),
                        "an unusable measurement is 'no pass finished yet', which is the fast answer, not a slow one");
    }

    /*
     * Why there is a ceiling at all. The damage meter zeroes its board after FightManager.FightTimeout seconds of quiet
     * (BuildMirrorUpdate compares the clock against the last fact the snapshot knows about), so a cadence that let a
     * snapshot age past that would have the meter blank itself on a raid that was fighting the whole time — the refresh
     * rule and the expiry rule would be arguing, with the expiry winning.
     */
    [TestMethod]
    public void TheCadenceStaysInsideTheMetersOwnQuietRule()
    {
        Assert.IsTrue(MirrorDeriveCadence.CeilingSeconds < FightManager.FightTimeout,
                      "a snapshot older than the meter's expiry reads to it as 'the raid stopped'");
    }
}
