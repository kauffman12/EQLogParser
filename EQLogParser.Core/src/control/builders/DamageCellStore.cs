#nullable enable annotations

namespace EQLogParser;

/*
 * What the damage walk has already counted, kept for the next build of the same capture.
 *
 * WHY. Whole-capture damage builds cost 89 % of their time in ONE loop: counting materialized records into `PlayerStats`
 * (measured on two captures with every row selected — `eqlog_Kizant_xegony-2.txt`: build 3,390 ms of which `walk` is
 * 3,054 ms; `eqlog_Incogitable_xegony.txt`: 763 ms of which `walk` is 613 ms — and a live raid pays that per rebuild,
 * twice a second at the derive cadence). Layer 1 made the RECORDS cheap to produce (a grown row continues instead of
 * re-materializing); this makes them cheap to COUNT, by remembering what the last build counted and walking only what
 * arrived since. The measurement that licensed it is docs/DesignNotes.md -> "Fold-at-publish measured".
 *
 * WHY A BLOCK AND NOT AN ORDINAL WINDOW. The walk does not see facts, it sees `ActionGroup` blocks handed over by
 * materialization, and layer 1 made those blocks REFERENCE-STABLE: an unchanged row is served the same `Fight` object,
 * and a grown one gets a new `Fight` whose leading blocks are the same objects with records appended at the tail (that is
 * `FightFactIndex.ContinueFight`'s rule, and it is the reason a resumed walk groups its first new fact exactly as a
 * from-zero walk would). So the honest resume point is `(block identity, how many of its actions were counted)` — one
 * dictionary probe per block, no hashing (hashing a block's records costs exactly what skipping them was for, which is the
 * same law `FightFactIndex` writes above its own ordinal-prefix proof).
 *
 * WHY A CELL IS NOT A PUBLISHED ROW. Cells are taken with `StatsSnapshot.TakeCarry`: detached (the grid must never hold
 * the object a later build accumulates into) and NOT drained — the running second stays open, because continuing a
 * night's counting means continuing that second rather than splitting it in two maxima. See StatsSnapshot.
 *
 * WHAT INVALIDATES A SET (all four are cheap, all fail CLOSED to a full recount):
 *   - a different capture (`DeriveEngine.SessionId`);
 *   - a classification pass that MOVED a verdict (`EntityTimeline.StateStamp()`) — ownership decides whose row a record
 *     lands in, so cells built under the old answer describe a board nobody would get by clicking;
 *   - any of the six `DamageValidator` settings (they filter INSIDE the walk, so cells holding assassinate damage are
 *     wrong the moment that box is unchecked);
 *   - the pet map changing size (`_petToPlayer` is rebuilt per build from every record, so a mapping learned late would
 *     fold this capture's OLD records onto a different owner than the one their cell was counted under). Pet mappings
 *     churn in a capture's first minutes and then stop; one cold rebuild each time they move is the whole cost.
 * And one proof measured on the build itself, in `DamageStatsBuilder`: every block this set counted must still be present
 * in the selection being built. A selection that dropped a row therefore cannot keep that row's damage, and a set used for
 * anything but the whole-capture window is refused outright (a slice is a different question, not a bigger one).
 */
internal sealed class DamageCellSet
{
  /// <summary>One counted row per name, detached from whatever the grid was handed. View fields (`Rank`, `Percent`,
  /// `Ranges`, `ClassName`, `Special`) are pristine because a set is captured between the walk and the present phase,
  /// which re-derives all of them for every row on the board.</summary>
  internal readonly Dictionary<string, PlayerStats> Rows = new();

  /// <summary>`prevPlayerTimes` as the last build left it: `StatsUtil.CheckNewFrame` decides an activity segment from the
  /// previous time this NAME was seen, so a resumed walk needs that answer rather than starting every name fresh.</summary>
  internal readonly Dictionary<string, double> FrameTime = [];

  /// <summary>Block identity -> actions already counted into `Rows`. The resume point AND the proof; see the class header.</summary>
  internal Dictionary<ActionGroup, int> Cursor = new(ReferenceEqualityComparer.Instance);

  /// <summary>The walk's alias maps (`topLevelStats` / `childrenStats`) as name keys: a top-level row is the individual row
  /// itself, and an owner's children are the pet rows hung under it. Rebuilt from names before the resumed walk runs, so
  /// names with nothing new still appear on the board.</summary>
  internal readonly List<string> TopLevel = [];
  internal readonly Dictionary<string, List<string>> Children = [];

  /// <summary>`_raidTotals.Total` as far as it has been counted (the raid line sums records, not rows, so pets would be
  /// double-counted by adding the rows back together).</summary>
  internal long RaidTotal;

  /// <summary>The newest block time the walk reached, kept for the class lookup at present time.</summary>
  internal double LastBlockTime = double.NaN;

  // Proof terms (see the class header). Written when the set is stored, compared when it is opened.
  internal int SessionId;
  internal long ClassificationStamp;
  internal int ValidatorFingerprint;
  internal int PetMapCount;

  /// <summary>Records counted into this set so far, and how many of them were skipped rather than walked on the build that
  /// stored it — the numbers behind a `cells` trace word.</summary>
  internal long RecordsCounted;

  /// <summary>Rough retention for the trace line: one row object per name plus one cursor entry per block. Deliberately
  /// crude (it cannot see sub-stat fan-out) and labelled rough where it prints, because its job is to show the set is a
  /// few MB next to a several-hundred-MB build, not to be an allocator.</summary>
  internal long RoughBytes => Rows.Count * 512L + Cursor.Count * 32L;
}

/*
 * The one slot a capture's counted cells live in.
 *
 * One, not a dictionary of selections: a set is only usable when its blocks are all still on the board, so a set carried
 * for another selection fails its own proof and is replaced. Keeping several would cost memory to hold sets that cannot be
 * used, which is what this class exists to stop. The store is process state like the boards themselves, so a closed or
 * replaced capture drops it (`CombatEvents.ActiveDataCleared`), and any thread may ask while another is building — the
 * builder holds its own lock around the work, this gate only keeps the slot itself consistent.
 */
internal static class DamageCellStore
{
  private static readonly object Gate = new();
  private static DamageCellSet _held;

  static DamageCellStore()
  {
    CombatEvents.ActiveDataCleared += _ => Clear();
  }

  /// <summary>What is held right now, for tests and the trace line. May be a set this build will refuse.</summary>
  internal static DamageCellSet Held
  {
    get { lock (Gate) return _held; }
  }

  /// <summary>Forget everything (log closed, capture replaced, boards cleared).</summary>
  internal static void Clear()
  {
    lock (Gate) _held = null;
  }

  /// <summary>Hand over the held set for this exact question, or null with the reason it was refused. The caller decides
  /// the rest of the proof (which needs the build's own blocks); this answers only the questions that can be asked before
  /// looking at them.</summary>
  internal static DamageCellSet TryOpen(in DamageBoardProof proof, int validatorFingerprint, int petMapCount, out string refuse)
  {
    lock (Gate)
    {
      var held = _held;
      if (held is null) { refuse = "empty"; return null; }
      if (held.SessionId != proof.SessionId) { refuse = "other capture"; return null; }
      if (held.ClassificationStamp != proof.ClassificationStamp) { refuse = "verdicts moved"; return null; }
      if (held.ValidatorFingerprint != validatorFingerprint) { refuse = "damage filters changed"; return null; }
      if (held.PetMapCount != petMapCount) { refuse = "pet mapping grew"; return null; }

      refuse = null!;
      return held;
    }
  }

  /*
   * What the LAST damage walk did, whether it used cells or not. Two numbers rather than a flag because a board can be
   * correct and still never have tried (every refusal is correct), and a suite that only checks correctness cannot tell a
   * working cache from a dead one: `LastReusedActions == 0` on a test that expected reuse is the cache doing nothing while
   * every assertion passes green. The same pair is what the trace line prints.
   */
  private static long _lastReused;
  private static long _lastCounted;

  /// <summary>Actions this build skipped because a carried cell had already counted them.</summary>
  internal static long LastReusedActions => Volatile.Read(ref _lastReused);

  /// <summary>Actions this build actually counted.</summary>
  internal static long LastCountedActions => Volatile.Read(ref _lastCounted);

  /*
   * Why the LAST build did not use cells, or null when it did. Refusals are all correct by design (each one is a question the
   * stored set cannot answer), so "the board is slow again" is only diagnosable if the reason survives the build: the trace line
   * carries it into the log, and this hands it to a test's failure message instead of a bare "reused 0".
   */
  private static string? _lastDecision;

  /// <summary>The refusal the last damage build hit, or null when it resumed from cells.</summary>
  internal static string? LastDecision => Volatile.Read(ref _lastDecision);

  /// <summary>Called by the damage builder at each refusal (and with null on a resume).</summary>
  internal static void RecordDecision(string? text) => Volatile.Write(ref _lastDecision, text);

  /// <summary>Called by the damage builder once per walk. Test-visible proof that a cached board really was cached.</summary>
  internal static void RecordWalk(long reusedActions, long countedActions)
  {
    Volatile.Write(ref _lastReused, reusedActions);
    Volatile.Write(ref _lastCounted, countedActions);
  }

  /// <summary>Publish a set as the answer for its question, replacing whatever was there.</summary>
  internal static void Store(DamageCellSet set)
  {
    lock (Gate) _held = set;
  }
}

/*
 * The identity of the question a damage build is asking, as far as carried cells care about it.
 *
 * `SessionId` is `DeriveEngine.SessionId` and `ClassificationStamp` is `EntityTimeline.StateStamp()` — both already
 * computed by the engine for its own digest gates, so naming them here costs no new state anywhere. The stamp moves with
 * evidence, so a pass that classified moves it and buys a recount; the cheap lane (which folds rows without running the
 * rule book) leaves it alone, which is exactly the asymmetry cells want: growth is what they can absorb, re-decided
 * ownership is not.
 */
internal readonly struct DamageBoardProof
{
  internal readonly int SessionId;
  internal readonly long ClassificationStamp;

  internal DamageBoardProof(int sessionId, long classificationStamp)
  {
    SessionId = sessionId;
    ClassificationStamp = classificationStamp;
  }
}
