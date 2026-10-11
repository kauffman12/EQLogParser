using System;
using System.Collections.Generic;

// Annotations only, no null-flow analysis: the project builds with Nullable=disable, and this API speaks in
// optional rows/fights because a scope legitimately has nothing to show.
#nullable enable annotations
namespace EQLogParser
{
  /*
   * The derived fight list in the shape the existing damage summary already reads: the boss-directed
   * facts of one projected fight, rebuilt as DamageRecord objects inside Fight.DamageBlocks.
   *
   * Why this exists at all: the point of the engine is that the current per-line pipeline and a
   * fact-table projection can be looked at side by side over the same log. The cheapest honest way to
   * compare them is to let the real summary tabs render the derived side — the same builder, the same
   * grids, so any disagreement is a classification difference and not two different pieces of UI
   * disagreeing with each other. Nothing here teaches the summary about the engine; it is handed
   * ordinary Fight objects.
   *
   * What a materialized fight holds:
   *   DamageBlocks  — one ActionGroup per run of facts sharing a second (FightManager.AddAction's own
   *                   grouping rule), filled in fact order. Only facts aimed AT the row's own name land here,
   *                   which is what the legacy manager puts in DamageBlocks too: a mob hitting a raider belongs
   *                   to TankingBlocks, and mixing the directions would credit a boss with its own damage output.
   *                   "Aimed at" is the same comparison the row splits DamageToOwner/DamageByOwner by, not a fresh
   *                   verdict — so an unclassified attacker's hit on a known NPC sits in these blocks (legacy counts
   *                   it there as well) while its per-player credit keeps waiting for a classification pass.
   *   DamageSegments/DamageSubSegments — per-player (and per-spell) activity windows through
   *                   StatsUtil.UpdateTimeSegments, the same call FightManager makes. These drive the
   *                   per-player "activity" seconds, so without them DPS would be computed against the
   *                   whole raid window.
   *   BeginDamageTime/LastDamageTime/DamageTotal/DamageHits — from the facts themselves, not copied from
   *                   the row, so a fight whose blocks and roll-up ever drift apart is visible as a
   *                   mismatch instead of silently agreeing.
   *   TankingBlocks / TankSegments / TankSubSegments / BeginTankingTime / LastTankingTime / TankHits /
   *                   TankTotal — the same row's facts read the other way: the ones where the row's own name
   *                   was the ATTACKER and a raider took the hit. FightManager keeps that half in its own block
   *                   list on the same Fight object and TankingStatsBuilder walks exactly that list, so one
   *                   derived row now feeds the damage board and the tanking board with neither builder knowing
   *                   anything new — which is the point of the whole seam.
   *
   * What makes splitting one row into two boards safe is the partition: FightProjection hands each fact to
   * exactly ONE row together with its direction, rows are keyed on the non-raider name, and OnFact files the
   * ordinal into the damage list or the tanking list by that flag. A selected set of rows therefore neither
   * counts a hit twice nor loses one. Legacy is not partitioned that way — its Get() keys a tanking record on
   * the DEFENDER, so a mob hitting a raider opens a fight row named after the raider. The derived list has no
   * player-named tank rows: damage taken belongs to the encounter that dealt it, and the per-player roll-up is
   * the builder's job off record.Defender, exactly as it is for legacy blocks.
   *
   *   ModifiersMask comes off the fact, which copies it off the record. That byte is why the six modifier
   *   settings (assassinate, headshot, slay-undead, ...) exclude the same damage on both boards: DamageValidator
   *   reads the mask, and a derived record with a mask of 0 would have excluded nothing — every derived total
   *   reading HIGH the moment one of those filters was switched off. It cost no memory: the four bytes it now
   *   occupies were spent on DamageFact.OverTotal, a field damage never wrote (see DamageFact).
   *   A heal's mask is captured the same way (HealFact.ModMask) for when the healing board is fed from facts.
   *   AttackerOwner comes from the line's own ownership word (ClassificationRules.OwnerInName) or from an R9
   *   charm window covering the fact (FightFactIndex.OwnerOf), never from the registry: a pet the manager
   *   never mapped still lands under its owner here, and so does a mob somebody charmed, which is a difference
   *   in what the two boards count, not an error in either. Pet roll-up ("X +Pets") works through the same
   *   field, so every charm of the same mob name across a night folds into one pet entry under its charmer.
   *   PlayerDamageTotals/PlayerTankTotals stay empty — nothing reads them since the legacy overlay engine
   *   (which accumulated its own totals outside any selection) was deleted. Filling them here would mean
   *   running DamageValidator a second time (the damage builder filters with the same call) and two copies of
   *   that six-setting filter drifting apart; they arrive when a consumer worth the duplicate pass exists.
   *   SubType is filled in when the fact has none (RecordFrom's helper): the summary's melee counters look the
   *   subtype up in a ConcurrentDictionary, which throws on a null key — so a null there does not degrade the
   *   board, it empties it, with the exception swallowed inside DamageStatsBuilder's own catch.
   *
   * Label note: LabelTypes.LabelOf hands back the same Labels constants the parsers use, so
   * record.Type is the interned literal as always. "Reverse DS" is the one word the fact table knows and
   * HitLabel does not (it names an attacker, never a type); no damage line produces it as a type, and a
   * stray one would read back null exactly as an unknown word always does.
   */
  internal sealed class FightFactIndex
  {
    /*
     * The classification this index belongs to, for two questions. (1) Was this attacker inside an R9 charm window, i.e.
     * somebody's pet for this span? (2) Is the TARGET of a caster-less spell line one of ours - which decides whether the
     * record names what the line said or says Labels.Unk (RecordFrom). Null (the default) means "nobody is anyone's pet here"
     * and "no target was placed on our side", which keeps every other caller and test behaving exactly as before.
     */
    private readonly EntityTimeline _charmers;

    public FightFactIndex(EntityTimeline charmOwners = null) => _charmers = charmOwners;

    private readonly object _gate = new();

    // fight -> ordinals into DamageFactTable.Facts, in table order (= arrival order), aimed at the owner only.
    private readonly Dictionary<DerivedFight, List<int>> _damageOrdinals = new();

    /*
     * The same rows' facts that landed ON ONE OF OUR PEOPLE — the tanking half, which is what the tank report
     * actually lists (TankingStatsBuilder groups by record.Defender). Not "aimed away from the owner": most of an
     * NPC row's outgoing facts hit a pet or another mob, and the projection's third target says so instead of
     * letting this list quietly mean everything else.
     *
     * Its own list rather than one list with a direction flag re-tested at materializing time, because each board's
     * blocks have to be built from a run that is ascending in time: FightManager.AddAction groups "consecutive
     * actions sharing a timestamp", and interleaving the two directions would split one second's block into two on
     * both boards.
     */
    private readonly Dictionary<DerivedFight, List<int>> _tankingOrdinals = new();

    /*
     * A materialized summary plus a stamp of everything the walk read out of the mutable world. See SummaryFightFor.
     *
     * The two counts do DOUBLE duty: they are the stamp AND the resume point. A cached entry whose DamageCount/TankCount
     * are below the row's current runs is not merely stale, it is a walk that stopped partway through an ordinal list that
     * has since grown - which is exactly what ContinueFight picks up (see the append-only laws there).
     */
    private readonly record struct CachedSummary(Fight Built, int DamageCount, int TankCount, int TauntCount,
      double BeginTime, double LastTime, bool Dead, int GroupId);

    private readonly Dictionary<DerivedFight, CachedSummary> _summaries = new();

    public long DamageFactCount { get; private set; }

    // Facts the tank board carries: hits that landed on one of our people (EntityTimeline.IsRaidVictimAt).
    public long TankingFactCount { get; private set; }

    // Captured, real, and belonging to no board — see OnFact. DamageFactCount + TankingFactCount + this = the
    // facts that reached a row.
    public long UnroutedFactCount { get; private set; }

    // Four bytes of ordinal per captured fact: on a 5.3 M-fact log where roughly three quarters are
    // player-side, that is ~16 MB held for the life of the snapshot. It buys selection-time materializing
    // (no re-scan of the whole table on every click) and it dies with the snapshot it belongs to. Both lists
    // count: a fact lands in at most one of them — the rest are unrouted and cost nothing but a counter.
    public long EstimatedBytes => (DamageFactCount + TankingFactCount) * 4L;

    /*
     * The gate is the index's whole reader/writer boundary. The index outlives the pass that fills it - the cheap lane
     * (DeriveCadence.ProjectionOnly) carries this instance from pass to pass and keeps appending while a meter or a click
     * enumerates it on another thread - so EVERY entry below takes it: an append can never tear an enumeration, and the
     * dictionary lookups never race a rehash. OnFact pays one uncontended lock per fact (measured negligible against the
     * fold it feeds); a reader holding it during BuildFight just serializes the writer for that row's build.
     */
    // Passed to FightProjection.Build as its owner sink — see the delegate's comment for why the answer
    // has to come from the projection rather than be recomputed here.
    internal void OnFact(DamageFact fact, int ordinal, DerivedFight owner, FightProjection.FactTarget target)
    {
      lock (_gate) OnFactLocked(fact, ordinal, owner, target);
    }

    private void OnFactLocked(DamageFact fact, int ordinal, DerivedFight owner, FightProjection.FactTarget target)
    {
      // Neither means "no board wants this": a mob biting another mob, a boss clearing somebody's swarm. It is
      // counted so the routing can be audited (UnroutedFactCount is the only sign of how much of a capture is
      // nobody's damage) and filed nowhere, which is what keeps TankTotal meaning "damage a person took".
      if (target == FightProjection.FactTarget.Neither)
      {
        UnroutedFactCount++;
        return;
      }

      var store = target == FightProjection.FactTarget.AtOwner ? _damageOrdinals : _tankingOrdinals;

      if (!store.TryGetValue(owner, out var ordinals))
      {
        store[owner] = ordinals = [];
      }

      ordinals.Add(ordinal);
      if (target == FightProjection.FactTarget.AtOwner) DamageFactCount++;
      else TankingFactCount++;
    }

    internal bool HasDamage(DerivedFight fight)
    {
      lock (_gate) return _damageOrdinals.ContainsKey(fight);
    }

    /*
     * A row whose facts all point away from it — a charmed raider's own output is the ordinary case, and so is
     * the mob of an exchange nobody could place the other way. Nothing for the damage board, everything for the
     * tanking one, which is why this cannot be answered as !HasDamage: asking that way silently dropped a
     * player's damage taken whenever the raid never landed a hit on whatever was hitting them.
     */
    internal bool HasTanking(DerivedFight fight)
    {
      lock (_gate) return _tankingOrdinals.ContainsKey(fight);
    }

    /*
     * The list handed back is the LIVE one - no copy (a night's row is tens of thousands of ints, and this is called per visible
     * row). That is safe for its callers and dangerous as an API, so the rule is stated where it can be obeyed: enumerate the
     * result only from the derive thread that just filled it, never from a UI or worker thread while a capture is folding - an
     * enumerator over a List<int> being appended to throws. Materialization never walks these; it re-reads through
     * SummaryFightFor/InWindow under the gate.
     *
     * Anything that needs only the NUMBER asks TankingOrdinalCount instead, which hands out nothing mutable.
     */
    internal IReadOnlyList<int> TankingOrdinalsFor(DerivedFight fight)
    {
      lock (_gate) return _tankingOrdinals.TryGetValue(fight, out var ordinals) ? ordinals : [];
    }

    /// <summary>Count-only answer for row cells (`# Hits To Players`): see TankingOrdinalsFor for why the list is not handed out.</summary>
    internal int TankingOrdinalCount(DerivedFight fight)
    {
      lock (_gate) return _tankingOrdinals.TryGetValue(fight, out var ordinals) ? ordinals.Count : 0;
    }

    // The ordinals behind a row, in table order — for the tests that hold this index to its contract, so
    // they can check a row against the fact table itself instead than against another copy of my bookkeeping.
    internal IReadOnlyList<int> DamageOrdinalsFor(DerivedFight fight)
    {
      lock (_gate) return _damageOrdinals.TryGetValue(fight, out var ordinals) ? ordinals : [];
    }

    /*
     * The Fight the damage summary will be handed for this derived row, built on first request and kept while it is
     * still the answer (re-clicking a finished row is free). Locked because selection events arrive on the dispatcher
     * while materializing happens on a worker task, and two clicks must not both build the same fight.
     *
     * "While it is still the answer" is the whole bug this shape exists to kill. Caching used to key on the row alone,
     * which was sound exactly as long as one index meant one snapshot; it no longer does, because the cheap lane
     * (DeriveCadence.ProjectionOnly) carries THIS index instance from pass to pass and keeps extending the open rows.
     * A mob clicked twice during a live pull therefore answered with the materialization made at its first click -
     * measured: the derived row held 300 damage, its summary still said 100, and it was the identical cached object.
     * The meter never showed this because its windowed path is deliberately never cached, so the two surfaces of one
     * engine disagreed and nothing threw.
     *
     * An entry is therefore reused only while every input BuildFight read still matches its stamp: the two ordinal
     * runs, the taunt stream's length (taunts are walked whole, so a line that arrives after the first click is
     * otherwise invisible to this row), and the row's own bounds and state - Dead flips from a queued death with no
     * new damage fact, and GroupId is re-stamped by Sectionizer every pass. Identity and charm owners need no stamp:
     * an index instance is folded under one timeline state, and a pass that moved a verdict discards this cache along
     * with the rows it belongs to (FightProjectionCache).
     */
    internal Fight SummaryFightFor(DerivedFight fight, DamageFactTable facts)
    {
      lock (_gate)
      {
        var damageCount = _damageOrdinals.TryGetValue(fight, out var run) ? run.Count : 0;
        var tankCount = _tankingOrdinals.TryGetValue(fight, out var tankRun) ? tankRun.Count : 0;

        if (_summaries.TryGetValue(fight, out var cached)
            && cached.DamageCount == damageCount
            && cached.TankCount == tankCount
            && cached.TauntCount == facts.TauntCount
            && cached.BeginTime == fight.BeginTime
            && cached.LastTime == fight.LastTime
            && cached.Dead == fight.Dead
            && cached.GroupId == fight.GroupId)
        {
          return cached.Built;
        }

        /*
         * Something moved. Before layer 1 that meant one answer - re-walk the row from ordinal zero, allocating every block
         * and every record again — which a live pull pays per touched row per pass for records that cannot have changed. Now:
         * continue from the published prefix when the entry's own ordinals still describe the head of this run, rebuild from
         * scratch only when nothing can be trusted (no prior answer, or a run that somehow got SHORTER - append-only is a
         * law of this store, so that branch is a guard against its violation rather than a case that happens).
         */
        var canContinue = cached.Built is not null && damageCount >= cached.DamageCount && tankCount >= cached.TankCount;
        var built = canContinue
          ? ContinueFight(cached, fight, facts)
          : BuildFight(fight, facts, double.NegativeInfinity, double.PositiveInfinity);

        _summaries[fight] = new CachedSummary(built, damageCount, tankCount, facts.TauntCount,
          fight.BeginTime, fight.LastTime, fight.Dead, fight.GroupId);
        return built;
      }
    }

    /*
     * The same row materialized through a TIME window: the damage meter's "since I zeroed it" slice of a fight that
     * ran on either side of that moment. Legacy keeps such a slice as its own state (DamageOverlayStatsBuilder holds
     * per-player totals plus a TimeRange of when each player was active, and zeroes them on reset), which is exactly
     * the bookkeeping this source exists to stop duplicating — so a window asks the SAME materializer for the records
     * whose seconds fall inside it, and the activity segments, hit counts and bounds that come back are the slice's.
     *
     * NOT CACHED, and that is the load-bearing decision. _summaries is keyed by DerivedFight alone, so storing a
     * sliced Fight under the same row would let a meter that was just zeroed serve its truncated numbers to an
     * unwindowed summary click — the one mistake where both surfaces still agree while one of them is wrong.
     * Rebuilding costs one walk over this row's own ordinal run (the runs are per-row, not per-capture), which a
     * once-a-second meter can pay; the cache it saves is correctness.
     */
    internal Fight? SummaryFightInWindow(DerivedFight fight, DamageFactTable facts, double fromT, double toT)
    {
      // Same boundary as SummaryFightFor: a pass appending while the meter slices would tear this walk.
      lock (_gate) return BuildFight(fight, facts, fromT, toT);
    }

    /*
     * Whose pet this attacker's damage belongs to. Two sources, in order of how well they are evidenced:
     *
     *   the ownership word inside the line itself ("Sancus`s pet" -> "Sancus"), which is what makes
     *   DamageStatsBuilder fold a pet's damage under its raider when the registry never learned the pet — the
     *   legacy manager asks the registry instead (and drops records whose attacker it cannot place at all), so
     *   this one field is where the two boards part company: see FightSummarySourceTest's mini-fight parity test;
     *   otherwise the charmer named by an R9 window covering THIS fact, which is how a night of charming
     *   `an imbued whipgrass` arrives as one pet entry under whoever cast the charm instead of as an NPC that hit
     *   things. OwnerOf answers null outside a window (so nothing else changes) and null for a window no cast
     *   was attributed to, which leaves those mobs as plain hostile-side credit rather than picking a raider.
     *
     * Charm owners are never persisted anywhere: the window is time-scoped, so the same mob name charmed by a
     * different necro next pull credits that necro instead.
     */
    private string OwnerOf(DamageFact fact, string attacker)
      => fact.OwnerInLine ? ClassificationRules.OwnerInName(attacker) : _charmers?.OwnerOf(attacker, fact.TimeS);

    /*
     * The accumulation state of one row's materialization, so ONE loop body serves both a row built from zero and a row
     * continued from its published prefix (`ContinueFight`). Everything here is either a cursor into the Fight being
     * built or the running bounds the damage pass keeps out of the published object until the walk closes.
     */
    private sealed class RowWalk
    {
      internal Fight Summary;

      /*
       * The block that a fact sharing its second would join, and that second (NaN = no block yet). This is the whole of
       * FightManager.AddAction's grouping rule ("consecutive actions sharing a timestamp"), which is why it can cross a
       * pass boundary unchanged: "consecutive" only ever compares against the PREVIOUS fact, so a continued walk groups
       * its first new fact exactly as a from-zero walk would have.
       */
      internal ActionGroup DamageBlock;
      internal double DamageBlockTime = double.NaN;
      internal double BeginDamage = double.NaN;
      internal double LastDamage = double.NaN;

      // The same cursor for the tanking half. Taunts have none, on purpose: they are a name-keyed stream walked whole,
      // cheap enough (a few thousand struct compares) that a resume rule for its window would be complexity bought with
      // silently-losable taunts.
      internal ActionGroup TankBlock;
      internal double TankBlockTime = double.NaN;
    }

    /*
     * Cold materialization: every ordinal of the row, from zero. This is what a REBUILT classification pass pays (its
     * `FightFactIndex` is new, so there is nothing to continue), what the meter's windowed slice always pays, and what the
     * first click on a row pays. A live row that merely GREW takes `ContinueFight` instead, and both go through AddDamage
     * and AddTank — "same facts in the same blocks" is structural here rather than argued.
     */
    private Fight? BuildFight(DerivedFight fight, DamageFactTable facts, double fromT, double toT)
    {
      var walk = new RowWalk { Summary = NewSummary(fight, fromT, toT) };

      /*
       * Two passes over one row, in this order: what the raid did to this name (the damage board's half) and
       * what this name did to the raid (the tanking board's). A row can have either, both, or — for a selection
       * that only ever got hit — only the second, so neither pass is allowed to decide whether the Fight exists.
       */

      if (_damageOrdinals.TryGetValue(fight, out var damageRun))
        foreach (var ordinal in damageRun) AddDamage(walk, facts.RefAt(ordinal), facts, fromT, toT);   // ordinals through the door

      if (_tankingOrdinals.TryGetValue(fight, out var tankRun))
        foreach (var ordinal in tankRun) AddTank(walk, facts.RefAt(ordinal), facts, fromT, toT);   // ordinals through the door

      AddTaunts(walk, fight, facts, fromT, toT);
      return FinishFight(walk);
    }

    /*
     * Layer 1 of the materialization cache: a row that grew continues instead of re-walking (docs/DesignNotes.md →
     * "What the cell cache has to buy at two boundaries"). Before this, any movement in a live row's ordinal run sent
     * `BuildFight` over every ordinal it had EVER held, allocating every block and every DamageRecord again — the
     * materialize term, paid per touched row per pass, for records that could not have changed.
     *
     * Appending is sound on three facts about this store, all pinned elsewhere:
     *   - the fact tables are APPEND-ONLY, so ordinal n describes the same line forever (rows keep their indices even
     *     across a slack trim, which is what lets `FightFactIndex` hold ordinals at all);
     *   - a row's ordinal list is only ever appended to (`OnFactLocked`), and a pass that REBUILT rather than continued
     *     threw this index away with the rows it belongs to, so a cache entry can never describe a different walk;
     *   - therefore the proof that an already-built block is still correct is its ORDINAL PREFIX. Never a content hash:
     *     hashing a block's records costs exactly what skipping them was for.
     *
     * What the tail walk must not lose is the accumulated half of every structure the loop touches, so it starts from a
     * copy (`CarryForward`) rather than from nothing: hit counts and totals, activity segments, per-spell aggregates, and
     * the open block each direction ended on. The taunt pass re-runs whole (see RowWalk).
     */
    private Fight? ContinueFight(CachedSummary cached, DerivedFight fight, DamageFactTable facts)
    {
      var walk = CarryForward(cached.Built, fight);

      if (_damageOrdinals.TryGetValue(fight, out var damageRun))
        for (var i = cached.DamageCount; i < damageRun.Count; i++) AddDamage(walk, facts.RefAt(damageRun[i]), facts, double.NegativeInfinity, double.PositiveInfinity);

      if (_tankingOrdinals.TryGetValue(fight, out var tankRun))
        for (var i = cached.TankCount; i < tankRun.Count; i++) AddTank(walk, facts.RefAt(tankRun[i]), facts, double.NegativeInfinity, double.PositiveInfinity);

      AddTaunts(walk, fight, facts, double.NegativeInfinity, double.PositiveInfinity);
      return FinishFight(walk);
    }

    /*
     * The new Fight that a continued walk writes into: fresh containers, holding the SAME closed blocks and records.
     *
     * Nothing published is mutated, which is the reason this returns a new Fight rather than appending in place — the
     * object handed out last pass lives inside a `SummaryInput` that a builder may be walking on a worker thread right
     * now, and appending to a List<T> under somebody else's enumerator is both a torn board and a throw. Closed blocks
     * are shared by reference (immutable-by-discipline once published: only the walk's own tail ever gets an Add); only
     * the LAST block of each direction is cloned, because that is the one a fact sharing its second can still extend.
     *
     * The per-player segment maps and the per-spell aggregates hold mutable VALUE objects (UpdateTimeSegments writes
     * EndTime into a TimeSegment; the spell boards add to Count/Max/Total), so those are cloned key by key — a row's
     * worth of keys is tens, against thousands of records left alone. Copying whole block LISTS per pass is O(blocks) of
     * pointer memcpy (not records); making that sub-linear is the cell-level cache, which is layer 2 and gated on its own
     * measurement.
     */
    private static RowWalk CarryForward(Fight published, DerivedFight fight)
    {
      var summary = NewSummary(fight, double.NegativeInfinity, double.PositiveInfinity);

      // What the walk accumulates continues rather than restarts.
      summary.DamageHits = published.DamageHits;
      summary.DamageTotal = published.DamageTotal;
      summary.TankHits = published.TankHits;
      summary.TankTotal = published.TankTotal;
      summary.BeginTankingTime = published.BeginTankingTime;
      summary.LastTankingTime = published.LastTankingTime;

      CopyBlocks(published.DamageBlocks, summary.DamageBlocks);
      CopyBlocks(published.TankingBlocks, summary.TankingBlocks);
      CopySegments(published.DamageSegments, summary.DamageSegments);
      CopySegments(published.TankSegments, summary.TankSegments);
      CopySubSegments(published.DamageSubSegments, summary.DamageSubSegments);
      CopySubSegments(published.TankSubSegments, summary.TankSubSegments);
      CopySpellStats(published.DdDamage, summary.DdDamage);
      CopySpellStats(published.DoTDamage, summary.DoTDamage);
      CopySpellStats(published.ProcDamage, summary.ProcDamage);

      var walk = new RowWalk
      {
        Summary = summary,
        BeginDamage = published.BeginDamageTime,
        LastDamage = published.LastDamageTime,
      };

      // Re-open the tail: cloned so an extend cannot reach the published block, and its second becomes the cursor, which
      // is what makes the first new fact join or open a block exactly as it would have inside one uninterrupted loop.
      walk.DamageBlock = ReopenTail(summary.DamageBlocks);
      walk.DamageBlockTime = walk.DamageBlock?.BeginTime ?? double.NaN;
      walk.TankBlock = ReopenTail(summary.TankingBlocks);
      walk.TankBlockTime = walk.TankBlock?.BeginTime ?? double.NaN;
      return walk;
    }

    /// <summary>The Fight header both paths start from: the row's identity, bounds and window clamp. See BuildFight/CarryForward.</summary>
    private static Fight NewSummary(DerivedFight fight, double fromT, double toT)
    {
      var summary = new Fight
      {
        Id = fight.Id,
        Name = fight.Name,
        Dead = fight.Dead,
        GroupId = fight.GroupId,
        BeginTime = double.IsPositiveInfinity(fight.BeginTime) ? double.NaN : fight.BeginTime,
        LastTime = double.IsNegativeInfinity(fight.LastTime) ? double.NaN : fight.LastTime,
      };

      /*
       * Clamp the row's own span to the window. These two are the DPS clock (Build hands them to AllRanges, which is
       * also what windows the healing board), so an unclamped BeginTime would divide the slice's damage by seconds
       * the slice never had — legacy's overlay measures against the segments it accumulated since the reset, not
       * against the encounter's birth. An unbounded window (every cached path) leaves them untouched.
       */
      if (!double.IsNaN(summary.BeginTime) && summary.BeginTime < fromT) summary.BeginTime = fromT;
      if (!double.IsNaN(summary.LastTime) && summary.LastTime > toT) summary.LastTime = toT;

      return summary;
    }

    private static void CopyBlocks(List<ActionGroup> from, List<ActionGroup> to)
    {
      foreach (var block in from) to.Add(block);
    }

    /*
     * A TimeSegment is mutable and UpdateTimeSegments writes EndTime into the instance it found, so a continued walk
     * would edit last pass's Fight through its own segment map. Clone the values; the keys are vocabulary strings.
     */
    private static void CopySegments(Dictionary<string, TimeSegment> from, Dictionary<string, TimeSegment> to)
    {
      foreach (var pair in from) to[pair.Key] = new TimeSegment(pair.Value.BeginTime, pair.Value.EndTime);
    }

    private static void CopySubSegments(Dictionary<string, Dictionary<string, TimeSegment>> from,
                                        Dictionary<string, Dictionary<string, TimeSegment>> to)
    {
      foreach (var pair in from)
      {
        var copy = new Dictionary<string, TimeSegment>(pair.Value.Count);
        CopySegments(pair.Value, copy);
        to[pair.Key] = copy;
      }
    }

    /// <summary>SpellDamageStats accumulates Count/Max/Total in place, same rule as the segment maps above.</summary>
    private static void CopySpellStats(Dictionary<string, SpellDamageStats> from, Dictionary<string, SpellDamageStats> to)
    {
      foreach (var pair in from)
        to[pair.Key] = new SpellDamageStats
        {
          Caster = pair.Value.Caster, Spell = pair.Value.Spell,
          Count = pair.Value.Count, Max = pair.Value.Max, Total = pair.Value.Total,
        };
    }

    /*
     * Replace the last block of a copied list with a clone of itself and hand it back as the walk's open block. The
     * published one keeps its own Actions list and every reference in it; nothing above this line can reach it.
     */
    private static ActionGroup ReopenTail(List<ActionGroup> blocks)
    {
      if (blocks.Count == 0) return null;

      var last = blocks[^1];
      var clone = new ActionGroup { BeginTime = last.BeginTime };
      foreach (var action in last.Actions) clone.Actions.Add(action);
      blocks[^1] = clone;
      return clone;
    }

    /*
     * One damage-side fact: its block, its record, its activity windows, the hit count and total, the running bounds, and
     * the per-spell boards' aggregates. Every rule in here is the rule the from-zero walk always had — this is the same
     * code, moved so the continued walk cannot drift from it.
     */
    private void AddDamage(RowWalk walk, DamageFact fact, DamageFactTable facts, double fromT, double toT)
    {
      var summary = walk.Summary;
      var time = (double)fact.TimeS;

      // Outside the meter's window: not carried, not counted, not a second of activity. Bounds and segments are computed
      // from what survives, which is why no other line here needs to know about the window.
      if (time < fromT || time > toT) return;

      // Same rule as FightManager.AddAction: consecutive actions sharing a timestamp share a block.
      if (walk.DamageBlock is null || !walk.DamageBlockTime.Equals(time))
      {
        walk.DamageBlock = new ActionGroup { BeginTime = time };
        summary.DamageBlocks.Add(walk.DamageBlock);
        walk.DamageBlockTime = time;
      }

      var record = RecordFrom(fact, facts);
      walk.DamageBlock.Actions.Add(record);

      // The per-spell activity key comes from the same helper FightManager uses. It cannot be null here:
      // SubTypeOf guarantees a subtype, and CreateRecordKey returns at least that.
      StatsUtil.UpdateTimeSegments(summary.DamageSegments, summary.DamageSubSegments,
        StatsUtil.CreateRecordKey(record.Type, record.SubType), record.Attacker, time);

      if (LabelTypes.IsHit(fact.TypeId))
      {
        summary.DamageHits++;
        summary.DamageTotal += fact.Total;
      }

      /*
       * Both bounds need their own NaN test rather than a bare comparison: `time > NaN` is false, so seeding
       * lastDamage with NaN and comparing left every fight ending at NaN. TimeRange.Add drops a segment whose
       * bounds do not compare, and DamageStatsBuilder divides by an activity window of nothing — a derived
       * selection that produces no numbers at all, with no exception anywhere to say so.
       */
      if (double.IsNaN(walk.BeginDamage) || time < walk.BeginDamage) walk.BeginDamage = time;
      if (double.IsNaN(walk.LastDamage) || time > walk.LastDamage) walk.LastDamage = time;

      /*
       * The per-spell boards read these dictionaries off the same Fight (SpellDamageStatsViewer): fill them with
       * FightManager's own live-parse shape - same key, same switch, same Count/Max/Total arithmetic. The kind
       * words are Labels' vocabulary (RecordFrom hands back LabelTypes.LabelOf), so Dd/Dot/Proc arrive without a
       * re-map, and a miss never reaches the switch because a miss is not any of these three words.
       *
       * The key is built INSIDE the branches that use it. A melee record cannot appear in a spell board, and this
       * loop runs on every record of the selection: hoisting the concatenation cost one string per swing for the
       * two words that need it (on a group night, 12k rows of mostly melee = 293k strings per build).
       */
      SpellDamageStats stats = null;
      switch (record.Type)
      {
        case Labels.Dd:
          var ddKey = record.Attacker + "++" + record.SubType;
          if (!summary.DdDamage.TryGetValue(ddKey, out stats))
          {
            stats = new SpellDamageStats { Caster = record.Attacker, Spell = record.SubType };
            summary.DdDamage[ddKey] = stats;
          }
          break;
        case Labels.Dot:
          var dotKey = record.Attacker + "++" + record.SubType;
          if (!summary.DoTDamage.TryGetValue(dotKey, out stats))
          {
            stats = new SpellDamageStats { Caster = record.Attacker, Spell = record.SubType };
            summary.DoTDamage[dotKey] = stats;
          }
          break;
        case Labels.Proc:
          var procKey = record.Attacker + "++" + record.SubType;
          if (!summary.ProcDamage.TryGetValue(procKey, out stats))
          {
            stats = new SpellDamageStats { Caster = record.Attacker, Spell = record.SubType };
            summary.ProcDamage[procKey] = stats;
          }
          break;
      }

      if (stats != null)
      {
        stats.Count += 1;
        stats.Max = Math.Max(record.Total, stats.Max);
        stats.Total += record.Total;
      }
    }

    /*
     * One tanking-side fact, built by the same rules as its damage twin with two deliberate differences, both inherited
     * from FightManager's own tank branch: the activity windows are attributed to record.Defender (the raider who took
     * it), and hits/total are counted UNCONDITIONALLY — a resisted spell that dealt zero is still a hit a player took.
     * Copying that asymmetry is the point; "tidying" it would move the tank board's # Hits column away from legacy for
     * no reason.
     */
    private void AddTank(RowWalk walk, DamageFact fact, DamageFactTable facts, double fromT, double toT)
    {
      var summary = walk.Summary;
      var time = (double)fact.TimeS;

      if (time < fromT || time > toT) return;

      if (walk.TankBlock is null || !walk.TankBlockTime.Equals(time))
      {
        walk.TankBlock = new ActionGroup { BeginTime = time };
        summary.TankingBlocks.Add(walk.TankBlock);
        walk.TankBlockTime = time;
      }

      var record = RecordFrom(fact, facts);
      walk.TankBlock.Actions.Add(record);

      StatsUtil.UpdateTimeSegments(summary.TankSegments, summary.TankSubSegments,
        StatsUtil.CreateRecordKey(record.Type, record.SubType), record.Defender, time);

      if (double.IsNaN(summary.BeginTankingTime)) summary.BeginTankingTime = time;
      summary.LastTankingTime = time;
      summary.TankHits++;
      summary.TankTotal += fact.Total;
    }

    /*
     * The taunt board reads TauntBlocks off these same fights (TauntStatsViewer): legacy attached every taunt to
     * GetFight(npc) ?? Create(npc, t), so a fact belongs to the ONE open row that name has at its second - never
     * more than one, because one name carries one open row. That is the routing rule this pass implements: the
     * fact names the row AND sits inside the row's OWN lifetime, intersected with the requested window.
     *
     * The lifetime clamp is load-bearing and easy to lose: the damage and tank passes cannot have this bug
     * because they walk this row's own ordinals, but taunts are a name-keyed stream walked whole. With an
     * unbounded selection every row named "An echo" would receive every taunt that ever named one - measured on
     * Incogitable, Useless's 4,364 taunt lines read back as multiples of thousands per add name. Same-name rows
     * never overlap in time (one life each), so the clamp is exact rather than approximate.
     *
     * What the clamp gives up, by design: a taunt that hits no row's lifetime at all sits on a fight legacy would
     * have INVENTED for it (`?? Create`). The derived list has no such rows - a row is one life, and a life with
     * no facts is not a life - so those taunts are the derived board's known residue rather than fabricated rows.
     *
     * Same one-second block rule as the damage and tank passes - the builder merges same-BeginTime blocks across
     * fights, and a split run would double-count an instant. The outcome words come back off the fact's bits
     * exactly as the parser split them; nothing is re-inferred.
     *
     * Always walked whole, including on a continued row: the stream is small and its window depends on the row's
     * lifetime, which advances — a resume rule here would trade a cheap scan for taunts that silently go missing.
     */
    private static void AddTaunts(RowWalk walk, DerivedFight fight, DamageFactTable facts, double fromT, double toT)
    {
      var summary = walk.Summary;
      ActionGroup tauntBlock = null;
      var lastTauntBlockTime = double.NaN;

      // The row's own span, intersected with the window a slice asks for. NaN bounds mean "the row never fought",
      // which cannot happen on a materialized row - but if it ever does, fall back to the plain window rather than
      // let Math.Max hand out NaN and route nothing.
      var tauntFrom = fromT;
      var tauntTo = toT;
      if (!double.IsNaN(fight.BeginTime) && fight.BeginTime > tauntFrom) tauntFrom = fight.BeginTime;
      if (!double.IsNaN(fight.LastTime) && fight.LastTime < tauntTo) tauntTo = fight.LastTime;

      foreach (var taunt in facts.Taunts)
      {
        var time = (double)taunt.TimeS;

        if (time < tauntFrom || time > tauntTo) continue;
        // Names are interned case-insensitively, so this is the same comparison the identity tables make.
        if (!string.Equals(facts.NameOf(taunt.NpcIdx), fight.Name, StringComparison.OrdinalIgnoreCase)) continue;

        if (tauntBlock is null || !lastTauntBlockTime.Equals(time))
        {
          tauntBlock = new ActionGroup { BeginTime = time };
          summary.TauntBlocks.Add(tauntBlock);
          lastTauntBlockTime = time;
        }

        tauntBlock.Actions.Add(new TauntRecord
        {
          Player = taunt.AttackerIdx >= 0 ? facts.NameOf(taunt.AttackerIdx) : string.Empty,
          Npc = fight.Name,
          Success = (taunt.Flags & TauntFact.TauntSuccess) != 0,
          IsImproved = (taunt.Flags & TauntFact.TauntImproved) != 0,
        });
      }
    }

    /*
     * Close a walk: the bounds it kept in locals, the tooltip text, and the one rule that decides whether this row is
     * part of the selection at all.
     */
    private static Fight? FinishFight(RowWalk walk)
    {
      var summary = walk.Summary;

      summary.BeginDamageTime = walk.BeginDamage;
      summary.LastDamageTime = walk.LastDamage;

      // FightManager rebuilds this string on every record; the last write is what the tooltip shows, so one
      // write at the end of materializing gives the identical text for the identical fight.
      if (!double.IsNaN(summary.BeginTime) && !double.IsNaN(summary.LastTime))
      {
        summary.TooltipText = $"#Hits To Players: {summary.TankHits}, #Hits From Players: {summary.DamageHits}, "
                              + $"Time Alive: {(long)(summary.LastTime - summary.BeginTime + 1)}s";
      }

      /*
       * Nothing inside the window is not "zero damage", it is "this row is not part of this scope". Handing back an
       * empty Fight would widen AllRanges (so the DPS clock runs on seconds nobody fought in) and, on the healing
       * side, pull heals that belong to no contributed row into the board. Null, and Build counts it aside.
       */
      if (summary.DamageBlocks.Count == 0 && summary.TankingBlocks.Count == 0) return null;

      return summary;
    }

    /*
     * One fact, one record — shared by both boards so a damage record and the tanking record of the same line
     * cannot disagree about who hit whom, how much, or which modifier filters apply.
     */
    private DamageRecord RecordFrom(DamageFact fact, DamageFactTable facts)
    {
      var attacker = facts.NameOf(fact.AtkIdx);

      /*
       * A line with no caster in it names its SPELL where the attacker belongs (`… damage from Slicing Energy by .`), and a
       * board must not turn that noun into a raid member: the damage grid puts one row per Attacker, so an unreplaced name
       * would give "Slicing Energy" a column, a DPS number and a spot beside Illuminai. Legacy answered this the same way -
       * FightManager's `record.AttackerIsSpell && defender` re-decision sets record.Attacker = Labels.Unk for exactly the
       * non-player target case - so the damage still counts toward the raid and the fight while nobody is invented to own it.
       *
       * The target test is legacy's own predicate (IsPetOrPlayerOrMerc), not "the attacker is a spell": a spell bouncing off
       * one of OUR people stays as it was, because that route is the tanking side of a fight against the spell itself, which
       * is what legacy keyed the row on and what the operator sees there today.
       */
      /*
       * Asked of the classification this index was built with, not of IdentityLookup: the seam there needs the engine's
       * LiveVerdict hook wired, and a summary can be materialized by anything (a test, a background board) that has this
       * timeline and no session. No timeline at all reads as "the target is not one of ours", which is the common case for a
       * caster-less line anyway - its targets are mobs.
       */
      if (fact.AttackerIsSpell)
      {
        var target = _charmers?.IdentityAt(facts.NameOf(fact.DefIdx), fact.TimeS);
        if (target is not IdentityKind.Player and not IdentityKind.Pet and not IdentityKind.Merc) attacker = Labels.Unk;
      }

      return new DamageRecord
      {
        Attacker = attacker,
        /*
         * The owner a line-owned name carries inside itself ("Sancus`s pet" -> "Sancus"), which is what makes
         * DamageStatsBuilder fold a pet's damage under its raider when the registry never learned the pet. The
         * legacy manager asks the registry instead (and drops records whose attacker it cannot place at all), so
         * this one field is where the two boards part company: see FightSummarySourceTest's mini-fight parity test.
         */
        AttackerOwner = OwnerOf(fact, attacker),
        Defender = facts.NameOf(fact.DefIdx),
        AttackerIsSpell = fact.AttackerIsSpell,
        Total = fact.Total,

        // Straight off the fact. Without it DamageValidator excludes nothing and the board disagrees with
        // legacy on every modifier-filtered run; the mask is also what makes a derived record comparable in
        // HitLogViewer, which shows the same column for stored records.
        ModifiersMask = fact.ModMask,
        Type = LabelTypes.LabelOf(fact.TypeId),
        SubType = SubTypeOf(fact, facts),
      };
    }

    /*
     * A derived record's SubType is never null. The fact table stores "this line carried no modifier text" as
     * NoSubtype (the sentinel top value of a ushort id) and plain melee is the ordinary case of that, but StatsUtil.UpdateDamageStats looks the
     * subtype up in a ConcurrentDictionary — which throws on a null key, inside DamageStatsBuilder's catch that
     * logs and carries on. So a null here is an empty board, not a rougher one. The type word stands in: the
     * activity window is identical either way, only the breakdown gets one sub-row per kind instead of one per
     * modifier message (and the same key reaches CreateRecordKey).
     */
    private static string SubTypeOf(DamageFact fact, DamageFactTable table)
      => table.SubtypeOf(fact.SubIdx) ?? LabelTypes.LabelOf(fact.TypeId) ?? Labels.OtherDmg;
  }

  // A derived selection turned into stats input: the materialized fights plus the AllRanges window the
  // summary expects (FightTable builds its own from the selected rows' BeginTime..LastTime, so the engine
  // list has to hand over the same thing or DPS would be measured against a different clock).
  internal sealed record SummaryInput(IReadOnlyList<Fight> Fights, TimeRange AllRanges)
  {
    public int WithoutDamage { get; init; }

    /*
     * The healing board's input for the same click, deliberately NOT filled by FightSummarySource.Build: heals
     * carry no fight id (a heal opens no encounter — that is why they are their own table), so the honest derived
     * heal input is "every heal in this selection's time window", which only a caller holding the heal table can
     * make. Null here means "this input says nothing about healing" and leaves the board on the record store;
     * DeriveEngine.BuildSummaryInput fills it, including with an empty list.
     */
    public List<(double, HealRecord)> Heals { get; init; }
  }

  internal static class FightSummarySource
  {
    public static SummaryInput Build(IReadOnlyList<DerivedFight> selected, FightFactIndex index, DamageFactTable facts)
      => Build(selected, index, facts, double.NegativeInfinity, double.PositiveInfinity);

    /*
     * The same selection through a time window — "these rows, from the moment the meter was zeroed until now". The
     * three-argument overload above is this one unbounded, so the click-a-row path keeps its cached materialization
     * untouched and cannot be affected by a meter that was reset mid-pull.
     */
    public static SummaryInput Build(IReadOnlyList<DerivedFight> selected, FightFactIndex index,
      DamageFactTable facts, double fromT, double toT)
    {
      var result = new List<Fight>(selected.Count);
      var bounded = fromT > double.NegativeInfinity || toT < double.PositiveInfinity;
      var allRanges = new TimeRange();
      var withoutDamage = 0;

      foreach (var fight in selected)
      {
        if (!index.HasDamage(fight) && !index.HasTanking(fight))
        {
          /*
           * A row with no facts in EITHER direction has nothing to show on either board, and counting it keeps
           * the status line honest instead of the selected list silently shrinking. Rows that only get hit are
           * NOT in this branch any more: they carry the damage-taken half of the selection, and legacy feeds its
           * own tank-only rows to both builders too — which is also why their span now reaches AllRanges, the way
           * legacy's does, so the DPS clock matches instead of quietly running narrower.
           */
          withoutDamage++;
          continue;
        }

        var built = bounded ? index.SummaryFightInWindow(fight, facts, fromT, toT) : index.SummaryFightFor(fight, facts);

        // A row with nothing inside the window contributes no numbers, so it must not contribute a clock or a heal
        // span either; counted the same way as a row with no facts at all.
        if (built is null)
        {
          withoutDamage++;
          continue;
        }

        // Legacy's _allRanges: the wall-clock span of each selected fight, inactivity included.
        if (!double.IsNaN(built.BeginTime) && !double.IsNaN(built.LastTime))
        {
          allRanges.Add(new TimeSegment(built.BeginTime, built.LastTime));
        }

        result.Add(built);
      }

      return new SummaryInput(result, allRanges) { WithoutDamage = withoutDamage };
    }
  }
}
