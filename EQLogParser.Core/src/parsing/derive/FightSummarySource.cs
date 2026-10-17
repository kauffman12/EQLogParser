using System;
using System.Collections.Generic;

// Annotations only, no null-flow analysis: the project builds with Nullable=disable, and this API speaks in
// optional rows/fights because a scope legitimately has nothing to show.
#nullable enable annotations
namespace EQLogParser
{
    /*
     * A projected fight rendered as ordinary Fight objects, so the real summary grids show the derived side beside the per-line side: same
     * builder, same validators, therefore any disagreement is a classification difference rather than two UIs.
     *
     * What a materialized fight holds, and why each part must be there:
     *   DamageBlocks — facts aimed AT the row's own name, grouped by FightManager's rule (consecutive facts sharing a second). The direction
     *     test is the same one that splits DamageToOwner/DamageByOwner, not a fresh verdict.
     *   TankingBlocks + tank segments — the same row read the other way (its name attacked, a raider took it), which is the list
     *     TankingStatsBuilder walks; one row feeds both boards with neither builder knowing anything new.
     *   DamageSegments/…SubSegments — per-player (per-spell) activity windows via StatsUtil, otherwise DPS divides by the whole raid window.
     *   Totals and bounds come from the facts themselves rather than the row, so a drift shows as a mismatch instead of a silent agreement.
     *   ModifiersMask comes off the fact: DamageValidator's six modifier settings must exclude the same damage on both boards.
     *   AttackerOwner comes from the line's own ownership word or an R9 charm window (FightFactIndex.OwnerOf), never from the registry.
     *   PlayerDamageTotals/PlayerTankTotals stay empty — no reader since the legacy overlay engine went, and filling them would duplicate
     *     DamageValidator's filter. SubType is never null (RecordFrom).
     *
     * Splitting one row across two boards is safe because FightProjection hands each fact to exactly ONE row with its direction. Legacy is not
     * partitioned that way (its tank rows are named after the defender), so the derived list has no player-named tank rows by design.
     * Label note: LabelTypes.LabelOf returns the same interned Labels constants the parsers use, so record.Type is unchanged.
     */
  internal sealed partial class FightFactIndex
  {
        /*
         * The classification this index was folded with: was this attacker inside an R9 charm window, and is a caster-less line's target one of
         * ours? Null means nobody is anyone's pet here and no target sits on our side.
         */
    private readonly EntityTimeline _charmers;

    public FightFactIndex(EntityTimeline charmOwners = null) => _charmers = charmOwners;

    private readonly object _gate = new();

    // fight -> ordinals into DamageFactTable.Facts, in table order (= arrival order), aimed at the owner only.
    private readonly Dictionary<DerivedFight, List<int>> _damageOrdinals = new();

        /*
         * The tanking half: this row's facts that landed on our players, pets or mercs (grouped by record.Defender). A separate
         * list rather than one with a direction flag because each board's blocks must be ascending in time — interleaving directions would split
         * one second's block on both boards.
         */
    private readonly Dictionary<DerivedFight, List<int>> _tankingOrdinals = new();

        /*
         * A materialization plus a stamp of everything the walk read (see SummaryFightFor). The two counts are both the stamp and the resume
         * point: below the row's current run length means a walk that stopped inside a list that has since grown.
         */
    private readonly record struct CachedSummary(Fight Built, int DamageCount, int TankCount, int TauntCount,
      double BeginTime, double LastTime, bool Dead, int GroupId);

    private readonly Dictionary<DerivedFight, CachedSummary> _summaries = new();

        /*
         * A budgeted accelerator, not storage (docs/DesignNotes.md → "DPS and active-reader retention review at 35420fcf"): unbounded it retained
         * every row of every selection (~129 MiB beside a 19.5 MiB capture). The budget counts materialized records, which is what costs memory.
         * Eviction costs one re-walk of that row's own ordinal run — the walk the first click always paid.
         */
    internal const int SummaryRecordBudget = 250_000;

    // Insertion/recency order for _summaries: oldest entry first, most recently touched last. Touched means "asked
    // and still the answer" - a stamp-stale read re-writes its own entry at the tail as part of answering.
    private readonly List<DerivedFight> _summaryOrder = [];

    // Materialized records held by _summaries (per entry: DamageCount + TankCount). The eviction trigger.
    private long _cachedRecords;

    public long DamageFactCount { get; private set; }

    // Facts the tank board carries: incoming hits on our players, pets and mercs, plus unplaced victims.
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
     * The doubling slack a per-row List<int> carries (up to its own count), for the derive report and tests. Kizant xegony-2
     * measured 3,500,000 slots against 2,268,615 ordinals - 4.7 MiB of address space a compact can hand back.
     */
    internal long OrdinalSlots
    {
      get
      {
        lock (_gate)
        {
          var slots = 0L;
          foreach (var list in _damageOrdinals.Values) slots += list.Capacity;
          foreach (var list in _tankingOrdinals.Values) slots += list.Capacity;
          return slots;
        }
      }
    }

    /* Cached summary bookkeeping for the derive report and tests. */
    internal int CachedSummaryCount { get { lock (_gate) return _summaries.Count; } }
    internal long CachedSummaryRecords { get { lock (_gate) return _cachedRecords; } }

        /*
         * Give back the ordinal lists' doubling slack, full lane only, beside the fact tables' own CompactRows. Dead rows only (an open row would
         * spend the growth again), and the list is swapped under _gate rather than resized in place so a holder keeps its intact array.
         */
    internal void CompactOrdinalSlack()
    {
      lock (_gate)
      {
        ReclaimOrdinalSlack(_damageOrdinals);
        ReclaimOrdinalSlack(_tankingOrdinals);
      }
    }

    private static void ReclaimOrdinalSlack(Dictionary<DerivedFight, List<int>> store)
    {
      // Gather first: replacing an entry inside the enumeration would be a throw, and the victim set is what gets copied.
      var victims = new List<(DerivedFight Fight, int Capacity)>();
      foreach (var pair in store)
      {
        if (pair.Key.Dead && pair.Value.Capacity - pair.Value.Count >= 64)
        {
          victims.Add((pair.Key, pair.Value.Capacity));
        }
      }

      foreach (var (fight, _) in victims)
      {
        var list = store[fight];
        store[fight] = new List<int>(list);
      }
    }

        /*
         * The index's whole reader/writer boundary: it outlives the pass that fills it (the cheap lane carries this instance and keeps appending
         * while a board enumerates it), so every entry takes it. OnFact pays one uncontended lock per fact — measured negligible.
         */
    // Passed to FightProjection.Build as its owner sink — see the delegate's comment for why the answer
    // has to come from the projection rather than be recomputed here.
    internal void OnFact(DamageFact fact, int ordinal, DerivedFight owner, FightProjection.FactTarget target)
    {
      lock (_gate) OnFactLocked(fact, ordinal, owner, target);
    }

    private void OnFactLocked(DamageFact fact, int ordinal, DerivedFight owner, FightProjection.FactTarget target)
    {
      // Neither keeps noncombatant effects out of both boards; retain the count for routing audits.
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
         * A row whose facts all point away from it. Nothing for the damage board, everything for the tanking one — which is why this is not
         * !HasDamage: asked that way, a player's damage taken vanished whenever the raid never hit whatever was hitting them.
         */
    internal bool HasTanking(DerivedFight fight)
    {
      lock (_gate) return _tankingOrdinals.ContainsKey(fight);
    }

        /*
         * The LIVE list is handed back, no copy (a night's row is tens of thousands of ints). Enumerate it only from the derive thread that just
         * filled it — an enumerator over a List<int> being appended to throws. Anything needing only the number asks TankingOrdinalCount.
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
         * The Fight the damage summary gets for this row, built on first request and kept only while it is still the answer (SummaryStamp). Keying
         * the cache on the row alone was the bug: the cheap lane carries this index across passes and extends open rows, so a twice-clicked live
         * mob served its first click's numbers. Every input BuildFight read must still match; identity and charm owners need no stamp because an
         * index instance belongs to one timeline state (FightProjectionCache).
         */
    /// <summary>Copies selected fact membership once; later projection growth cannot alter an export.</summary>
    internal int[] CaptureOrdinals(IReadOnlyList<DerivedFight> selected)
    {
      lock (_gate)
      {
        var ordinals = new List<int>();
        foreach (var fight in selected.Distinct())
        {
          if (_damageOrdinals.TryGetValue(fight, out var damage)) ordinals.AddRange(damage);
          if (_tankingOrdinals.TryGetValue(fight, out var tanking)) ordinals.AddRange(tanking);
        }
        // Projection assigns each ordinal to exactly one life/direction. Distinct fights cannot duplicate a row.
        ordinals.Sort();
        return ordinals.ToArray();
      }
    }

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
          // Still the answer after an ask: it is the entry eviction must meet last, so move it to the tail.
          TouchSummaryOrder(fight);
          return cached.Built;
        }

                /*
                 * Something moved: continue from the published prefix when the entry's own ordinals still describe the head of this run, and rebuild from
                 * scratch only when nothing can be trusted (no prior answer, or a run that got shorter — append-only is a law, so that arm guards it).
                 */
        var canContinue = cached.Built is not null && damageCount >= cached.DamageCount && tankCount >= cached.TankCount;
        var built = canContinue
          ? ContinueFight(cached, fight, facts)
          : BuildFight(fight, facts, double.NegativeInfinity, double.PositiveInfinity);

        var previous = _summaries.TryGetValue(fight, out var prior) ? prior.DamageCount + prior.TankCount : 0;
        _summaries[fight] = new CachedSummary(built, damageCount, tankCount, facts.TauntCount,
          fight.BeginTime, fight.LastTime, fight.Dead, fight.GroupId);

        if (!_summaryOrder.Contains(fight))
        {
          _summaryOrder.Add(fight);
        }
        else
        {
          TouchSummaryOrder(fight);
        }

        _cachedRecords += damageCount + tankCount - previous;
        EvictSummariesToBudget();
        return built;
      }
    }

    /* O(n) over the cached rows (hundreds, not millions): a click-path cost, and the list only ever holds entries the budget has already kept. */
    private void TouchSummaryOrder(DerivedFight fight)
    {
      _summaryOrder.Remove(fight);
      _summaryOrder.Add(fight);
    }

    /* Oldest first until the held records fit again. Removing from _summaries is the whole of eviction: a published Fight is never
       mutated (the layer-1 law), and a consumer already walking one keeps its own reference - it just stops being REUSABLE, which
       is what an accelerator losing to the budget means. */
    private void EvictSummariesToBudget()
    {
      while (_cachedRecords > SummaryRecordBudget && _summaryOrder.Count > 0)
      {
        var oldest = _summaryOrder[0];
        if (!_summaries.Remove(oldest, out var evicted))
        {
          _summaryOrder.RemoveAt(0);
          continue;
        }

        _cachedRecords -= evicted.DamageCount + evicted.TankCount;
        _summaryOrder.RemoveAt(0);
      }
    }

        /*
         * The same row through a TIME window — the meter's "since I zeroed it" slice. NOT CACHED, deliberately: _summaries is keyed by row alone, so
         * storing a sliced Fight there would let a zeroed meter serve truncated numbers to a plain row click. Rebuilding costs one walk of this row's
         * own ordinal run, which a once-a-second meter can pay; what the cache would save is correctness.
         */
    internal Fight? SummaryFightInWindow(DerivedFight fight, DamageFactTable facts, double fromT, double toT)
    {
      // Same boundary as SummaryFightFor: a pass appending while the meter slices would tear this walk.
      lock (_gate) return BuildFight(fight, facts, fromT, toT);
    }

        /*
         * Whose pet this attacker's damage belongs to: the ownership word inside the line ("Sancus`s pet" → "Sancus") first — that is where the two
         * boards part company, since legacy asks the registry and drops what it cannot place — otherwise the charmer named by an R9 window covering
         * this fact. Charm owners are never persisted: a different necro's pull credits that necro.
         */
    private string OwnerOf(DamageFact fact, string attacker)
      => fact.OwnerInLine ? ClassificationRules.OwnerInName(attacker) : _charmers?.OwnerOf(attacker, fact.TimeS);

        /*
         * One row's accumulation state, so one loop body serves a walk from zero and a continued walk (ContinueFight). Every field is either a cursor
         * into the Fight being built or running bounds kept out of the published object until the walk closes.
         */
    private sealed class RowWalk
    {
      internal Fight Summary;

            /*
             * The open block and its second (NaN = none yet). This is all of FightManager.AddAction's grouping rule — "consecutive" only compares with
             * the previous fact, so a continued walk groups its first new fact exactly as a from-zero walk would.
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
         * Cold materialization: every ordinal from zero — what a rebuilt pass, the meter's windowed slice and a row's first click pay. A live row that
         * merely grew takes ContinueFight instead; both share AddDamage/AddTank, so "same facts in same blocks" is structural.
         */
    private Fight? BuildFight(DerivedFight fight, DamageFactTable facts, double fromT, double toT)
    {
      var walk = new RowWalk { Summary = NewSummary(fight, fromT, toT) };

            /*
             * Two passes in this order: what the raid did to this name, then what this name did to the raid. Either, both or only the second may exist,
             * so neither pass decides whether the Fight exists.
             */

      if (_damageOrdinals.TryGetValue(fight, out var damageRun))
        foreach (var ordinal in damageRun) AddDamage(walk, facts.RefAt(ordinal), facts, fromT, toT);   // ordinals through the door

      if (_tankingOrdinals.TryGetValue(fight, out var tankRun))
        foreach (var ordinal in tankRun) AddTank(walk, facts.RefAt(ordinal), facts, fromT, toT);   // ordinals through the door

      AddTaunts(walk, fight, facts, fromT, toT);
      return FinishFight(walk);
    }

        /*
         * Layer 1 of the cache: a row that grew continues instead of re-walking (docs/DesignNotes.md → "What the cell cache has to buy at two
         * boundaries"). Sound on three facts pinned elsewhere — the fact tables are append-only, a row's ordinal list is only ever appended to, and a
         * rebuilt pass discards this index — so an already-built block's proof is its ORDINAL PREFIX, never a content hash. The tail walk starts from
         * CarryForward so nothing accumulated is lost; the taunt pass re-runs whole (RowWalk).
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
         * The Fight a continued walk writes into: fresh containers holding the SAME closed blocks and records. Nothing published is mutated — the last
         * one may be under a worker thread's enumerator — so only each direction's LAST block is cloned, and the mutable value objects (segments,
         * per-spell aggregates) are cloned key by key.
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
             * Clamp the row's span to the window: these two are the DPS clock, so an unclamped BeginTime divides the slice's damage by seconds the slice
             * never had. An unbounded window (every cached path) leaves them untouched.
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
         * One damage-side fact: block, record, activity windows, hit count, total, running bounds and the per-spell aggregates. Same rules as the
         * from-zero walk, moved so the continued walk cannot drift from them.
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
             * Both bounds need their own NaN test: `time > NaN` is false, and a NaN end leaves a zero-length activity window that the board divides by —
             * an empty selection with no exception anywhere to say so.
             */
      if (double.IsNaN(walk.BeginDamage) || time < walk.BeginDamage) walk.BeginDamage = time;
      if (double.IsNaN(walk.LastDamage) || time > walk.LastDamage) walk.LastDamage = time;

            /*
             * The per-spell dictionaries, filled in FightManager's live-parse shape (same key, same Count/Max/Total arithmetic) using Labels' words.
             * The key is built INSIDE the branches that use it: hoisting it cost one string per swing for the two words that need it.
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
         * One tanking-side fact, same rules as its damage twin with FightManager's two inherited differences: activity windows are attributed to
         * record.Defender, and hits/total count unconditionally — a zero-damage resist is still a hit a player took. Copying that asymmetry is the
         * point; tidying it would move legacy's # Hits column for no reason.
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
         * The taunt board's pass (TauntStatsViewer reads TauntBlocks). A fact belongs to the ONE open row its name has at that second, intersected
         * with the requested window — and the lifetime clamp is load-bearing because taunts are a name-keyed stream walked whole: unclamped, every row
         * named "An echo" received every taunt that ever named one. A taunt hitting no row's lifetime is known residue, not a fabricated row (legacy
         * would have invented one). Same one-second block rule as the other passes; always walked whole, since the row's lifetime advances.
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
             * Nothing inside the window means "not part of this scope", not "zero": an empty Fight would widen AllRanges (a DPS clock running on seconds
             * nobody fought in) and pull unrelated heals into the healing side. Null, and Build counts it aside.
             */
      if (summary.DamageBlocks.Count == 0 && summary.TankingBlocks.Count == 0) return null;

      return summary;
    }

    /*
     * One fact, one record — shared by both boards so a damage record and the tanking record of the same line
     * cannot disagree about who hit whom, how much, or which modifier filters apply.
     */
    private string AttackerOf(DamageFact fact, DamageFactTable facts)
    {
      var attacker = facts.NameOf(fact.AtkIdx);

            /*
             * A line with no caster names its SPELL where the attacker belongs ("… damage from Slicing Energy by ."), and a board must not turn that noun
             * into a raid member: legacy answers with Labels.Unk too, so the damage still counts and nobody is invented to own it. The target test is
             * legacy's own predicate (IsPetOrPlayerOrMerc), not "the attacker is a spell" — a spell bouncing off one of ours stays on the tanking side.
             */
            /*
             * Asked of the classification this index carries rather than IdentityLookup, so a summary can be materialized by anything holding a timeline.
             * No timeline reads as "the target is not one of ours" — the common case for a caster-less line anyway.
             */
      if (fact.AttackerIsSpell)
      {
        var target = _charmers?.IdentityAt(facts.NameOf(fact.DefIdx), fact.TimeS);
        if (target is not IdentityKind.Player and not IdentityKind.Pet and not IdentityKind.Merc) attacker = Labels.Unk;
      }

      return attacker;
    }

    private DamageRecord RecordFrom(DamageFact fact, DamageFactTable facts)
    {
      var attacker = AttackerOf(fact, facts);
      return new DamageRecord
      {
        Attacker = attacker,
                /*
                 * The owner a line-owned name carries inside itself ("Sancus`s pet" → "Sancus"), so a pet's damage folds under its raider even when the
                 * registry never learned the pet. Legacy asks the registry instead and drops what it cannot place — see the parity test.
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
         * A derived record's SubType is never null: StatsUtil looks it up in a ConcurrentDictionary, which throws on a null key inside a catch that
         * carries on — so null means an empty board, not a rougher one. The type word stands in; only the breakdown's grouping changes.
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
         * The healing board's input for this click, deliberately NOT filled here: heals carry no fight id, so the honest derived heal input is "every
         * heal in this selection's window", which only a caller holding the heal table can build. Null means "says nothing about healing";
         * DeriveEngine.BuildSummaryInput fills it, including with an empty list.
         */
    public List<(double, HealRecord)> Heals { get; init; }
  }

  internal static class FightSummarySource
  {
    public static SummaryInput Build(IReadOnlyList<DerivedFight> selected, FightFactIndex index, DamageFactTable facts)
      => Build(selected, index, facts, double.NegativeInfinity, double.PositiveInfinity);

        /*
         * The same selection through a time window ("these rows since the meter was zeroed"). The unbounded overload is this one without bounds, so the
         * click-a-row path keeps its cached materialization untouched.
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
                     * A row with no facts in either direction contributes nothing but keeps the status line honest — a selection must not silently shrink.
                     * Rows that only get hit are NOT here: they carry the damage-taken half, and their span reaches AllRanges so the DPS clock matches legacy.
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
