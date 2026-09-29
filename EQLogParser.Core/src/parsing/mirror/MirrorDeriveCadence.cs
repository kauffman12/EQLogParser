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
   *                             when a human presses Re-derive. Measured on `eqlog_Kizant_xegony.txt` (2,931,939 facts):
   *                             one pass costs about 650 ms, so refreshing while data is dirty is affordable — and
   *                             therefore necessary, not a luxury.
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
  public static class MirrorDeriveCadence
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
     * ticks because the timer's granularity is a plumbing detail (see MirrorSession.TimerIntervalMs): counted in ticks,
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
     * The decision itself. `count` is what the capture holds now, `lastDerivedCount` what the last finished pass
     * covered, `quietSeconds` how long the count has held still, `factsPerSecond` how fast it was growing over the
     * observation window, and `sinceLastPassS` how long ago that pass ended (callers measure all of these
     * monotonically; positive infinity means "no pass has ever run", so the first board arrives promptly).
     *
     * No argument is a tick count, on purpose: the whole rule has to read the same however often it is asked.
     */
    public static bool ShouldDerive(long count, long lastDerivedCount, double quietSeconds,
                                    double factsPerSecond, double sinceLastPassS, double lastPassSeconds)
    {
      // Nothing captured, or nothing the grid does not already show: an idle log must cost exactly nothing.
      if (count <= 0 || count == lastDerivedCount) return false;

      // The load stopped moving, which is the classic trigger and what a finished file needs.
      if (quietSeconds >= QuietSeconds) return true;

      // A file being read. Bulk load ends in quiet, and the rule above catches it there.
      if (factsPerSecond >= BulkFactsPerSecond) return false;

      // A live tail: refresh on the cost-aware clock rather than waiting for a silence that a raid never offers.
      return sinceLastPassS >= LiveIntervalSeconds(lastPassSeconds);
    }
  }
}
