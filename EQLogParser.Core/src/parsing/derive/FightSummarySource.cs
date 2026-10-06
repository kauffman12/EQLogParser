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
     * A materialized summary plus a stamp of everything BuildFight read out of the mutable world. See SummaryFightFor.
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
    public int FightsWithDamage => _damageOrdinals.Count;
    public int FightsWithTanking => _tankingOrdinals.Count;


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

    /// <summary>Count-only answer; see TankingOrdinalCount for why a count has its own door.</summary>
    internal int DamageOrdinalCount(DerivedFight fight)
    {
      lock (_gate) return _damageOrdinals.TryGetValue(fight, out var ordinals) ? ordinals.Count : 0;
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

        var built = BuildFight(fight, facts, double.NegativeInfinity, double.PositiveInfinity);
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

    private Fight? BuildFight(DerivedFight fight, DamageFactTable facts, double fromT, double toT)
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
       * against the encounter's birth.
       */
      if (!double.IsNaN(summary.BeginTime) && summary.BeginTime < fromT) summary.BeginTime = fromT;
      if (!double.IsNaN(summary.LastTime) && summary.LastTime > toT) summary.LastTime = toT;

      /*
       * Two passes over one row, in this order: what the raid did to this name (the damage board's half) and
       * what this name did to the raid (the tanking board's). A row can have either, both, or — for a selection
       * that only ever got hit — only the second, so neither pass is allowed to decide whether the Fight exists.
       */
      var allFacts = facts.Facts;
      List<int> ordinals = _damageOrdinals.TryGetValue(fight, out var damageRun) ? damageRun : [];
      ActionGroup block = null;
      var lastBlockTime = double.NaN;
      var beginDamage = double.NaN;
      var lastDamage = double.NaN;

      foreach (var ordinal in ordinals)
      {
        var fact = allFacts[ordinal];
        var time = (double)fact.TimeS;

        // Outside the meter's window: not carried, not counted, not a second of activity. Bounds and segments below
        // are computed from what survives, which is why no other line here needs to know about the window.
        if (time < fromT || time > toT) continue;

        // Same rule as FightManager.AddAction: consecutive actions sharing a timestamp share a block.
        if (block is null || !lastBlockTime.Equals(time))
        {
          block = new ActionGroup { BeginTime = time };
          summary.DamageBlocks.Add(block);
          lastBlockTime = time;
        }

        var record = RecordFrom(fact, facts);
        block.Actions.Add(record);

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
        if (double.IsNaN(beginDamage) || time < beginDamage) beginDamage = time;
        if (double.IsNaN(lastDamage) || time > lastDamage) lastDamage = time;

        /*
         * The per-spell boards read these dictionaries off the same Fight (SpellDamageStatsViewer): fill them with
         * FightManager's own live-parse shape - same key, same switch, same Count/Max/Total arithmetic. The kind
         * words are Labels' vocabulary (RecordFrom hands back LabelTypes.LabelOf), so Dd/Dot/Proc arrive without a
         * re-map, and a miss never reaches the switch because a miss is not any of these three words.
         */
        var spellKey = record.Attacker + "++" + record.SubType;
        SpellDamageStats stats = null;
        switch (record.Type)
        {
          case Labels.Dd:
            if (!summary.DdDamage.TryGetValue(spellKey, out stats))
            {
              stats = new SpellDamageStats { Caster = record.Attacker, Spell = record.SubType };
              summary.DdDamage[spellKey] = stats;
            }
            break;
          case Labels.Dot:
            if (!summary.DoTDamage.TryGetValue(spellKey, out stats))
            {
              stats = new SpellDamageStats { Caster = record.Attacker, Spell = record.SubType };
              summary.DoTDamage[spellKey] = stats;
            }
            break;
          case Labels.Proc:
            if (!summary.ProcDamage.TryGetValue(spellKey, out stats))
            {
              stats = new SpellDamageStats { Caster = record.Attacker, Spell = record.SubType };
              summary.ProcDamage[spellKey] = stats;
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

      summary.BeginDamageTime = beginDamage;
      summary.LastDamageTime = lastDamage;

      /*
       * The tanking half, built the same way off the away-facing ordinals. FightManager's own tank branch is the
       * spec: block per run of one second, activity windows keyed by the SAME record-key helper and attributed to
       * record.Defender (the raider who took it — the damage pass attributes them to record.Attacker), then
       * Begin/LastTankingTime and TankHits/TankTotal.
       *
       * Note what is NOT gated here. The damage pass counts hits and total only for LabelTypes.IsHit facts,
       * because legacy gates them on StatsUtil.IsHitType; the tank branch in FightManager is unconditional, so
       * a resisted spell that dealt zero still counts as a hit a player took. Copying that asymmetry is the
       * point — "tidying" it would move the tank board's # Hits column away from legacy for no reason.
       */
      if (_tankingOrdinals.TryGetValue(fight, out var tankRun) && tankRun.Count > 0)
      {
        ActionGroup tankBlock = null;
        var lastTankBlockTime = double.NaN;

        foreach (var ordinal in tankRun)
        {
          var fact = allFacts[ordinal];
          var time = (double)fact.TimeS;

          if (time < fromT || time > toT) continue;

          if (tankBlock is null || !lastTankBlockTime.Equals(time))
          {
            tankBlock = new ActionGroup { BeginTime = time };
            summary.TankingBlocks.Add(tankBlock);
            lastTankBlockTime = time;
          }

          var record = RecordFrom(fact, facts);
          tankBlock.Actions.Add(record);

          StatsUtil.UpdateTimeSegments(summary.TankSegments, summary.TankSubSegments,
            StatsUtil.CreateRecordKey(record.Type, record.SubType), record.Defender, time);

          if (double.IsNaN(summary.BeginTankingTime)) summary.BeginTankingTime = time;
          summary.LastTankingTime = time;
          summary.TankHits++;
          summary.TankTotal += fact.Total;
        }
      }

      // FightManager rebuilds this string on every record; the last write is what the tooltip shows, so one
      // write at the end of materializing gives the identical text for the identical fight.
      if (!double.IsNaN(summary.BeginTime) && !double.IsNaN(summary.LastTime))
      {
        summary.TooltipText = $"#Hits To Players: {summary.TankHits}, #Hits From Players: {summary.DamageHits}, "
                              + $"Time Alive: {(long)(summary.LastTime - summary.BeginTime + 1)}s";
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
       */
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
