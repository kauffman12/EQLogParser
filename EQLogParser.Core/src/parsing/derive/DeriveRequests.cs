using System.Threading;

namespace EQLogParser
{
  /*
   * A re-derive somebody asked for while a pass was already running, kept from evaporating.
   *
   * The session runs one derive at a time (`DeriveEngine` answers "already in flight" and returns), which is right - two
   * passes folding the same capture would interleave writes to the projection cache - but it made the answer to "the operator
   * set a name as Pet while a pass happened to be running" be *nothing at all*: the request arrived, met a busy flag, and was
   * dropped. On an idle log nothing else ever asks again, so the correction the operator can see in the file never reaches the
   * board, and the retry that would have fixed it is the next thing they click. Same shape for any explicit Full request:
   * opening a derived meter forces one, and a second surface doing the same a moment later silently cancels itself.
   *
   * So a request is not a call, it is a fact about the world with an ordering:
   *
   *   ARRIVE       every explicit Full request stamps a monotonically increasing number. A cheap pass asks for nothing (its
   *                verdicts are inherited, and asking would make every 0.5 s fold look like an operator's correction).
   *   START BOUND  a pass remembers, when it begins, how many requests existed. It answers those, because they were made of
   *                the state it read - and only those, which is what makes a request that lands DURING the pass still owed.
   *                (An override applied mid-pass was not in the facts that pass classified, so it must not be marked served.)
   *   SERVE        a completed EXPENSIVE pass marks everything up to its own bound answered. A failed pass marks nothing: a
   *                deterministic poison plus an owed request is the one combination that must not spin, so the retry ladder
   *                (DeriveCadence.RetryDelayS) stays the only thing driving retries after a throw, and the debt drains on the
   *                next pass that actually finished.
   *   OWE          a finished pass asks this once and, if it is true, runs another Full pass. That single hop - not a queue,
   *                not a timer - is what turns "dropped" into "one pass later".
   *
   * Requests are counted, not stored: they carry no payload (a Full pass re-reads the world), so a queue would suggest the
   * twelfth click in a row says something the first one did not.
   *
   * WHO MAY STAMP, and this is not a stylistic rule. Only a GESTURE does - an operator's write, a meter being opened. The derive pump
   * does not, because it already has a memory: `DeriveCadence.Decide` compares the captured count against what each lane last read and
   * asks again on the next tick until that is paid. When the pump came through the same door as a click, its mid-pass asks were counted
   * as requests - and a full pass takes 600-940 ms on a large capture while the pump ticks every 100 ms, so every pass left roughly one
   * fabricated ask per tick of its own duration for the cascade to pay. A field run showed the result on every line: `Derive done: 511
   * fights, 940 ms (rebuilt)` and then, same millisecond and same captured count, another pass reading `70 ms (continued)`. Dropping an
   * un-honoured pump ask costs one tick; dropping an un-honoured gesture is the bug this type was written for. That asymmetry is the whole
   * boundary - see DeriveEngine's RederiveAsync (the gesture door, stamps) and StartPass (everywhere else, does not).
   */
  internal sealed class DeriveRequests
  {
    private int _arrived;
    private int _servedThrough;

    /// <summary>An explicit request. Cheap passes ask for nothing and stamp nothing.</summary>
    public void Arrive() => Interlocked.Increment(ref _arrived);

    /// <summary>Taken when a pass STARTS: everything requested up to here is this pass's to answer.</summary>
    public int StartBound() => Volatile.Read(ref _arrived);

    /// <summary>A completed expensive pass answers the requests that existed when it began - no more.</summary>
    public void ServedThrough(int bound) => Interlocked.Exchange(ref _servedThrough, bound);

    /// <summary>Something was asked after the last served point: a follow-up Full pass is owed.</summary>
    public bool IsOwed => Volatile.Read(ref _arrived) > Volatile.Read(ref _servedThrough);
  }
}
