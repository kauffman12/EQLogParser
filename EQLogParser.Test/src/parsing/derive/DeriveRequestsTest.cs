using EQLogParser;

namespace EQLogParser;

/*
 * A re-derive requested while a pass is already running (DeriveEngine answers "in flight" and returns, which is correct —
 * two passes folding one capture would interleave writes to the projection cache). What used to happen to that request was
 * nothing: it met the busy flag and evaporated. On an idle log no other event ever asks again, so an operator's "this name is
 * a Pet" could reach identity-overrides.txt and never reach the board, with no retry and nothing in the log to point at.
 *
 * The rules this type exists to keep:
 *
 *   - a request survives a busy session (ARRIVE happens before the busy test, in the engine);
 *   - a pass answers the requests that existed when it STARTED, because those were made of the facts it read — and only those,
 *     so one applied mid-pass is still owed when that pass ends;
 *   - an expensive pass settles its own bound, a cheap one settles nothing (it inherits verdicts rather than computing them);
 *   - requests are counted, not queued: the twelfth click says nothing the first did not, and one hop of "owe → pass" replaces
 *     a queue that could only ever be redundant.
 */
[TestClass]
public class DeriveRequestsTest
{
    [TestMethod]
    public void ANewSessionOwesNothing()
    {
        var requests = new DeriveRequests();

        Assert.IsFalse(requests.IsOwed, "a session nobody has asked anything of must not schedule a pass");
    }

    [TestMethod]
    public void ARequestIsOwedUntilAFullPassAnswersIt()
    {
        var requests = new DeriveRequests();
        requests.Arrive();

        Assert.IsTrue(requests.IsOwed, "an explicit request is a debt, not a suggestion");

        var bound = requests.StartBound();
        requests.ServedThrough(bound);

        Assert.IsFalse(requests.IsOwed, "a pass that started with the request outstanding and finished has answered it");
    }

    /*
     * The heart of it: the operator's click lands DURING a pass. That pass classified facts that predate the override, so it
     * may not settle the request — and the follow-up hop in the engine's finally is what makes the correction visible instead
     * of lost. This is the exact sequence the dropped-request bug shipped with, expressed without a dispatcher.
     */
    [TestMethod]
    public void ARequestArrivingMidPassIsStillOwedWhenThatPassFinishes()
    {
        var requests = new DeriveRequests();

        var bound = requests.StartBound();       // pass begins, nothing asked yet
        requests.Arrive();                       // an override is written while it works
        requests.ServedThrough(bound);           // the pass finishes, answering what existed at its start

        Assert.IsTrue(requests.IsOwed,
            "a request made after this pass read the capture was not answered by it — dropping it here is the old bug");
    }

    [TestMethod]
    public void SeveralRequestsDrainOnOnePass()
    {
        var requests = new DeriveRequests();

        for (var i = 0; i < 5; i++) requests.Arrive();   // a batch of cell edits, each forcing a re-derive
        var bound = requests.StartBound();
        requests.ServedThrough(bound);

        Assert.IsFalse(requests.IsOwed, "one pass over the whole capture answers every request folded into its moment");

        requests.Arrive();
        Assert.IsTrue(requests.IsOwed, "and the sixth one is a new fact about the world");
    }

    /*
     * A cheap fold inherits the verdicts the last full pass produced, so it cannot discharge a debt the rules owe — were it to
     * settle requests, a live raid's half-second refreshes would quietly cancel every override requested during one, which is
     * the same failure with a busier clock. The engine simply never calls ServedThrough for ProjectionOnly; this pins that the
     * debt is still standing after a fold's worth of traffic.
     */
    /*
     * Paying a debt may not take out a new one. The engine's follow-up hop runs a pass for the owed request; if that hop stamped the
     * ledger on its way in, the ledger would look owed again while it worked, and - because the pump re-asks from its own watermarks -
     * the pair would repeat for as long as each pass outran one tick. Measured in a field run: every `rebuilt 608-940 ms` line was
     * followed in the same millisecond by `continued 46-72 ms` over an unchanged capture, and that extra pass is precisely this debt,
     * manufactured by an asker that had nothing new to say (docs/DesignNotes.md → "A derive pass that starts where the last one stopped").
     */
    [TestMethod]
    public void ACascadePaysItsDebtWithoutTakingOutAnother()
    {
        var requests = new DeriveRequests();

        var boundA = requests.StartBound();   // a pass is running; nobody has asked for this one's answer
        requests.Arrive();                    // the operator's write lands mid-pass - a real debt
        requests.ServedThrough(boundA);       // that pass answers only what existed at its start
        Assert.IsTrue(requests.IsOwed, "a gesture mid-pass is owed its own pass");

        var boundB = requests.StartBound();   // the cascade: no Arrive, it is paying, not asking
        requests.ServedThrough(boundB);

        Assert.IsFalse(requests.IsOwed,
            "the pass that paid the debt leaves nothing behind — a cascade that stamps here would leave every pass owing its successor");
    }

    /*
     * The doubling, stated as arithmetic because the engine itself cannot run outside WPF. A full pass costs 600-940 ms on a large
     * capture and the pump ticks every 100 ms, so SEVEN ticks find the rule book still owed while one pass is running (its watermarks are
     * written when it finishes). When such a tick stamps the ledger - which is what the pump did, coming through the same door an
     * operator's click uses - the finished pass settles only its own start bound and then finds itself "owed", so it runs a SECOND
     * expensive lane over facts nobody added to. Requests collapse into one another (counted, not queued), so this is exactly one extra
     * pass per pass rather than one per tick - which is why the field log shows tidy pairs: `940 ms (rebuilt)` then `70 ms (continued)`,
     * same millisecond, same captured count, on every single line including load-end passes with no operator action anywhere near them.
     *
     * What matters is not the count but what the second pass ANSWERED: nothing. Not one gesture was outstanding.
     */
    [TestMethod]
    public void AStampedPumpAskDoublesEveryExpensivePass()
    {
        const int passMs = 900;      // a measured full pass over a 6.9 M-fact capture
        const int tickMs = 100;      // DeriveEngine.TimerIntervalMs
        var passes = 0;

        var old = new DeriveRequests();
        var bound = old.StartBound();                  // the pump's expensive pass begins (it stamped, then met "not busy")
        passes++;
        for (var tick = 0; tick < passMs / tickMs; tick++) old.Arrive();   // the pump re-asks while it runs; no click among them
        old.ServedThrough(bound);
        if (old.IsOwed) { var cascadeBound = old.StartBound(); passes++; old.ServedThrough(cascadeBound); }

        Assert.AreEqual(2, passes,
            "a pump that stamps turns every expensive pass into two — the measured pair, one re-walk of the night for nothing");
        // There is no gesture in this ledger to answer: the ONLY stamps above are pump ticks. That is the whole finding — the
        // extra re-walk of the night settled a debt that nobody had.

        // The fixed shape: the pump's un-honoured ask costs one tick. Its memory is the two watermarks, so the next tick re-asks
        // and nothing is lost; between ticks a hundred asks cost a hundred comparisons.
        var now = new DeriveRequests();
        var passesNow = 0;
        for (var tick = 0; tick < 100; tick++)
        {
            if (!now.IsOwed) continue;
            var b = now.StartBound();
            passesNow++;
            now.ServedThrough(b);
        }

        Assert.AreEqual(0, passesNow, "a pump that re-asks from the watermarks leaves no debt behind — 100 ticks cost zero passes");
    }

    [TestMethod]
    public void ACheapPassSettlesNothing()
    {
        var requests = new DeriveRequests();
        requests.Arrive();

        // A cheap pass runs: it takes a bound (like every pass does) and settles nothing.
        _ = requests.StartBound();

        Assert.IsTrue(requests.IsOwed, "folding facts without running the rule book does not answer an override");
    }
}
