namespace EQLogParser.Mirror
{
  /*
   * When the mirror is allowed to run another pass, decided apart from the session so the rule can be tested without a
   * dispatcher. The question is genuinely two questions, and conflating them is what made the derived surfaces go stale:
   *
   *   "The load finished."      The count stopped moving between two ticks. This is what a bulk file load needs — one
   *                             pass at the end, no passes wasted mid-file — and it is also all a CLOSED log ever needs.
   *   "Somebody is watching."    A live tail never goes quiet for two whole ticks during a raid; a line arrives every
   *                             fraction of a second all fight long. Waiting for silence there means the fight list, the
   *                             summaries and the damage meter hold one snapshot for the whole encounter and only move
   *                             when somebody forced a pass by hand. Measured on `eqlog_Kizant_xegony.txt` (2,931,939 facts):
   *                             one pass costs about 650 ms, so refreshing while data is dirty is affordable — and
   *                             therefore necessary, not a luxury.
   *
   * WHICH pass runs is decided here too (`DeriveKind`), because one cadence cannot be both prompt and affordable: classification is most of
   * a pass (measured 186-261 ms, against 0-5 ms to fold the facts that arrived since), so the expensive lane keeps the cadence described
   * below while a cheap fold of new facts over the last expensive pass's verdicts rides in between on `FastFloorSeconds`.
   *
   * The throttle on the second question is derived from the cost of the last pass rather than fixed, because a pass
   * costs what the capture costs: 4x the measured duration, clamped. And the ceiling is not arbitrary — it has to stay
   * inside `FightManager.FightTimeout` (30 s), which is the quiet rule the damage meter applies to its own board. A
   * snapshot older than that reads as "the raid stopped", so a cadence slower than the meter's expiry would have the
   * board blank itself every time it merely needed a refresh.
   *
   * Bulk loads stay untouched: growth big enough to be a file being read (measured ~170,000 facts/second on the same
   * capture against tens per second while tailing live) is left to the quiescence trigger, so re-deriving does not park
   * ingest at the gate several times during the one moment when throughput matters.
   */
  public static class DeriveCadence
  {
    // Never pass more often than this, however cheap the capture: each pass parks ingest at the gate while it runs.
    public const double FloorSeconds = 3d;

    // Never wait longer than this for a fresh pass while data is dirty, or the surfaces downstream start treating a
    // stale snapshot as an idle raid (FightManager.FightTimeout is 30 s; this stays under it with room for two ticks).
    public const double CeilingSeconds = 15d;

    // A pass costs roughly what the last one cost, so leave that much room per unit of work before asking again.
    public const double CostMultiplier = 4d;

    /*
     * How still the captured count has to sit before a load counts as finished. Measured in SECONDS rather than in timer
     * ticks because the timer's granularity is a plumbing detail (see DeriveEngine.TimerIntervalMs): counted in ticks,
     * a finer timer would decide "finished" after a shorter silence, so the same log would get its end-of-load pass at
     * different moments depending on how often somebody happens to check.
     */
    public const double QuietSeconds = 1d;

    /*
     * Ingest this fast is a file being read, not a raid fighting (measured ~170,000 facts/second while chewing
     * `eqlog_Kizant_xegony.txt` against tens per second live): leave it to quiescence. A RATE rather than a growth-per-
     * check, for the same reason as above — a per-check allowance would stop seeing bulk load purely by checking more
     * often, which is the one moment re-deriving would park ingest.
     */
    public const double BulkFactsPerSecond = 25_000d;

    /*
     * How long a live tail waits between passes, given what the last pass cost. Measured pass of 650 ms (the largest
     * capture on file) lands on the floor: 3 s. A 10 s pass — several nights' worth of facts — lands on the ceiling.
     */
    public static double LiveIntervalSeconds(double lastPassSeconds)
    {
      // A negative or NaN measurement means "no pass has finished yet", which is the fastest answer, not a slow one.
      if (!(lastPassSeconds > 0d)) return FloorSeconds;

      var wanted = lastPassSeconds * CostMultiplier;
      return wanted < FloorSeconds ? FloorSeconds : (wanted > CeilingSeconds ? CeilingSeconds : wanted);
    }

    /*
     * How often the CHEAP lane may run: rows recomputed from the facts already captured, with the verdicts the last full pass
     * produced. Measured cost of that on a live increment is **0-5 ms** of projection (docs/DesignNotes.md -> "How long a meter
     * update takes"), against 186-261 ms for a pass that re-runs the classification rule book — and since a pass parks ingest at
     * `CombatCapture._gate` while it runs, the price of a cadence is not CPU but how much of the cycle the parse thread spends
     * waiting. Half a second costs about 1 % of that gate; lowering the FULL floor to half a second would cost a quarter of it.
     *
     * What the cheap lane buys and what it costs: numbers move in ~0.6-0.8 s instead of ~3.4 s, and an identity verdict learned
     * from facts that arrived after the last full pass (a pet folding onto its raiders, a charm flipping a name) lands one full
     * cadence later rather than on the same pass. That delay already exists today every time a pass is skipped; this makes it
     * bounded and predictable instead of "whenever the pump next felt like it".
     */
    public const double FastFloorSeconds = 0.5d;

    /*
     * The decision itself, and it answers two questions rather than one: WHETHER to pass, and WHICH pass.
     *
     * `count` is what the capture holds now, `lastDerivedCount` what the last finished pass of either lane covered,
     * `quietSeconds` how long the count has held still, `factsPerSecond` how fast it was growing over the observation window,
     * `sinceAnyPassS`/`sinceFullPassS` how long ago the last pass of either lane / of the expensive lane ended, and
     * `lastFullPassSeconds` what that expensive pass cost (callers measure all of these monotonically; positive infinity means
     * "never happened", so the first board arrives promptly).
     *
     * No argument is a tick count, on purpose: the whole rule has to read the same however often it is asked.
     */
    public static DeriveKind Decide(long count, long lastDerivedCount, double quietSeconds, double factsPerSecond,
                                    double sinceAnyPassS, double sinceFullPassS, double lastFullPassSeconds)
    {
      // Nothing captured, or nothing the surfaces do not already show: an idle log must cost exactly nothing.
      if (count <= 0 || count == lastDerivedCount) return DeriveKind.None;

      /*
       * The load stopped moving. This is the classic trigger and what a finished file needs, and it asks for the EXPENSIVE lane
       * on purpose: at the end of a load the rules have the whole capture in front of them, which is when they learn the things
       * a cheap refresh cannot (a name nobody owns yet, a pet whose owner spoke once at minute forty).
       */
      if (quietSeconds >= QuietSeconds) return DeriveKind.Full;

      // A file being read. Bulk load ends in quiet and the rule above catches it there; BOTH lanes park, because the cheap one
      // still holds the gate that the loader needs.
      if (factsPerSecond >= BulkFactsPerSecond) return DeriveKind.None;

      /*
       * A live tail. The expensive lane keeps the cadence it has always had — its cost is what sets the wait, and that wait is
       * what keeps ingest affordable — and anything cheaper in between is the cheap lane.
       */
      if (sinceFullPassS >= LiveIntervalSeconds(lastFullPassSeconds)) return DeriveKind.Full;
      return sinceAnyPassS >= FastFloorSeconds ? DeriveKind.ProjectionOnly : DeriveKind.None;
    }

    /*
     * The failure ladder, in seconds: after a pass THREW, the next attempt comes RetryDelayS(consecutiveFailures) later —
     * 1 s, doubling, capped at a minute, and 0 for "no failures outstanding" (any completed pass, either lane, clears the
     * streak).
     *
     * This replaced killing auto-derive forever on the first exception. That design predates the two-lane cadence: when a
     * derive only ran at load-end, "stop the loop" cost one or two future passes; under a 0.5-3 s refresh it froze every
     * derived surface for the rest of the night over a single hiccup — a file locked mid-write, an AV scan, a race in
     * someone's new rule — and recovery needed a human with a button. A transient fault now disappears inside a second;
     * deterministic poison degrades to a slow, LOGGED drip that a reopened log ends.
     */
    public static double RetryDelayS(int consecutiveFailures)
      => consecutiveFailures <= 0 ? 0 : Math.Min(60d, Math.Pow(2, consecutiveFailures - 1));
  }

  /*
   * Which pass the cadence is asking for. `Full` classifies and projects; `ProjectionOnly` reuses the verdicts the last full
   * pass produced and folds the facts that arrived since, which is what makes a fast refresh affordable at all (Core measures:
   * 0-5 ms against 186-261 ms). `None` means the surfaces keep the snapshot they have.
   */
  public enum DeriveKind
  {
    None,
    ProjectionOnly,
    Full,
  }
}
