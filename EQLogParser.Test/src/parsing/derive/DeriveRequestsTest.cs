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
