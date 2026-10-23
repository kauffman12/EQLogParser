using System;
using System.Collections.Generic;

namespace EQLogParser
{
  // The displayed fight list: a pure projection of the immutable fact table over the CURRENT
  // classification. LegacyFightReplay answers "what would the current pipeline have keyed?"; this
  // answers "which NPC-side entity is this exchange with, given everything we now know?".
  // Rows therefore migrate when evidence arrives - an exchange that started as a defender-keyed
  // guess under legacy's tiebreak moves to its true NPC row the moment R4/R9/overrides classify
  // one side - without a single fact changing. Rebuild is cheap and idempotent; DeriveEngine
  // reruns it after every classification pass, which is the whole dynamic-update mechanism.
  //
  // Side rules (per fact, at the fact's own timestamp):
  //   Player/Pet/Merc              -> player side; a Friendly (charm) interval flips it to NPC side,
  //                                   so a mesmerised raider can own her own row and a charmed mob's
  //                                   output counts toward its defender's fight.
  //   Npc                          -> NPC side; Friendly interval makes it player side instead.
  //   Unknown                      -> resolved against the anchored neighbour (whoever attacks a
  //                                   known player is the NPC of that exchange and vice versa);
  //                                   when neither side is classified, the legacy name-heuristic
  //                                   tiebreak keys the row so unclassified bosses still appear.
  //   both players                 -> dropped: friendly fire and spell feedback are not fights.
  internal static class FightProjection
  {
    /*
     * A name's exchange stream splits into one row per engagement: two facts for the same owner separated by
     * more than this gap start a new fight, the way re-engaging a boss hours later is a different fight from
     * the legacy list's point of view. (The legacy list keyed those boundaries off reset/slain events it could
     * see live; a time gap is the classification-free, idempotent equivalent - and never invents a boundary
     * inside one continuous brawl.)
     *
     * It is legacy's own expiry number, and it was 300 until one pull was read off `eqlog_Kizant_xegony.txt`:
     * `Waxwork Abolishion` takes hits from 18:34:08 to 18:34:53, the raid spends 135 s beating adds, and the
     * boss comes back at 18:37:08 for a 162 s second life. At 300 s those two lives were ONE row of 342 s —
     * one entry fewer in a list whose whole job is counting encounters, a duration column reading "5 minutes"
     * for a 46-second fight, and (the quiet one) the fade welded into the DPS clock: `TimeRange.Add` drops
     * silences of 6 s and over from a selection's total, so legacy's three runs summed to 324 s where the
     * merged row's single span reached 343 s for the same click. A gap rule that swallows an encounter is not
     * being conservative about brawls, it is inventing one.
     *
     * Legacy expired at 60 s flat, or 30 s once the fight had landed boss-directed damage (FightManager).
     * One number here rather than two: nothing measured has ever needed the slower one — a row that has not
     * hurt anybody for half a minute is over either way — and two thresholds would decide a row's boundaries
     * from which side the first hit went, which is not a fact about the encounter.
     *
     * This literal is THE source of the number: the legacy manager's own `FightTimeout` reads it back, so the
     * two spellings cannot drift into two constants while both engines still run. When the manager goes, the
     * constant that survives is this one, and app readers (LineChart's segment gaps) ask for it by name.
     */
    public const double EngagementGapS = 30;

    /*
     * How far AFTER a row's last fact an event can still be ABOUT that row: a slain line lands a second after
     * the killing blow, and a charm sighting can trail the raid's last swing by more than silence worth
     * splitting a fight over. These two tail questions used to ride on EngagementGapS's 300, and they are not
     * the same question — tightening them with the split would have silently stopped closing charm rows that
     * are measured real (12 of them on Incogitable), which is why they keep the wider window under a name
     * that says what it is for.
     */
    public const double EventTailWindowS = 300;

    // How far BEFORE a death to look for the charm window that death closed. See DiedWhileCharmed.
    private const double CharmDeathSlackS = 1;

    // Internal rather than private so a real-log census can ask the SAME question the walk asks: "which side is this
    // name on at this second?" A test that re-implements it drifts, and a drifted census is worse than none.
    internal enum Side : byte { Unknown, Player, Npc }

    // Projection is the sole routing decision; consumers must not infer a direction again from the names.
    // AtOwner is output on the encounter, RaidSide is incoming damage to our players, pets and mercs,
    // and Neither excludes noncombatant effects. Tanking groups by the defender, never by a pet's owner.
    // See docs/DesignNotes.md → "Pet tanking: the Show Pets option loses its input".
    internal enum FactTarget : byte { AtOwner = 0, RaidSide = 1, Neither = 2 }

    internal delegate void FactOwnershipHandler(DamageFact fact, int ordinal, DerivedFight owner, FactTarget target);

    /*
     * What a pass has to remember about the walk it already did, so the NEXT pass can continue instead of re-walking.
     *
     * This is not a cache of ANSWERS but the fold's own accumulators: which row each name is mid-engagement with, which
     * row each name last closed (the charm→pet pairing needs that link), the rows already finished, the deaths queued per
     * name, and how far through each event stream the walk got. `Continue` below resumes with them; `Build` starts from a
     * fresh one, so the full pass and the incremental pass run THE SAME LOOP BODY over the same facts and cannot drift —
     * which is the whole reason the state is carried rather than a second, faster implementation being written beside it.
     *
     * Carrying is only valid while the classification is the one these rows were projected over, and the caller checks
     * that with EntityTimeline.StateStamp (`Stamp` here records what was used). A fact table that shrank under us (a log
     * reopened, a cache cleared) also invalidates it — see Covers.
     */
    internal sealed class ProjectionState
    {
      internal readonly Dictionary<string, DerivedFight> Open = new(StringComparer.Ordinal);
      internal readonly Dictionary<string, DerivedFight> LastClosed = new(StringComparer.Ordinal);
      internal readonly List<DerivedFight> Completed = [];
      internal readonly Dictionary<string, Queue<DeathFact>> DeathsByName = new(StringComparer.Ordinal);

      /*
       * How far each event stream has been folded in. Facts and deaths are append-only within a log-open, but ordinal 500
       * of one table is not ordinal 500 of another, so the watermark records the identity of the fact it stopped on: an
       * unrelated (or rebuilt) table whose prefix happens to be the same length would otherwise be accepted, and its facts
       * would then be routed into rows — and into ordinal lists — that describe a different log.
       */
      internal int ThroughOrdinal;
      internal int ThroughDeath;

      // The answers these rows were routed under (EntityTimeline.AnswerStamp — see FightProjectionCache for why answers, not evidence).
      internal long Stamp = long.MinValue;

      private long _factWatermark;
      private long _deathWatermark;

      internal void MarkWatermark(DamageFactTable facts)
      {
        _factWatermark = ThroughOrdinal > 0 ? FactWatermark(facts, ThroughOrdinal - 1) : 0;
        _deathWatermark = ThroughDeath > 0 ? DeathWatermark(facts, ThroughDeath - 1) : 0;
      }

      internal bool Covers(DamageFactTable facts)
      {
        if (ThroughOrdinal > facts.Facts.Length || ThroughDeath > facts.Deaths.Length) return false;
        if (ThroughOrdinal > 0 && FactWatermark(facts, ThroughOrdinal - 1) != _factWatermark) return false;
        if (ThroughDeath > 0 && DeathWatermark(facts, ThroughDeath - 1) != _deathWatermark) return false;
        return true;
      }

      /*
       * Back to a walk that has not started. A rebuild is not the same thing as a continuation with the watermarks moved
       * back to zero: every row in here was folded under the classification that was current when its facts were walked,
       * and re-walking facts into those same rows would leave per-row work (the player roll-up above all) computed under
       * verdicts this pass no longer holds. Measured on rules-fixture.txt: a raider classified mid-log left her damage in
       * the row totals but not in that row's roll-up, because the fold happened before she was anybody.
       */
      internal void Reset()
      {
        Open.Clear();
        LastClosed.Clear();
        Completed.Clear();
        DeathsByName.Clear();
        ThroughOrdinal = 0;
        ThroughDeath = 0;
        _factWatermark = 0;
        _deathWatermark = 0;
      }

      /*
       * The identity of the fact each stream stopped on. Ordinals and sequence numbers are not enough by themselves: a
       * table numbers its own facts and its name pool numbers its own names, so two unrelated captures can agree on both
       * while being completely different nights (two pulls of the same shape; an R16 reparse that rebuilt the tables from
       * scratch). So the boundary fact is named by what it IS — sequence, time, label, and the two NAMES. The name texts
       * are hashed here, once per pass at the boundary, never per fact.
       *
       * A genuine continuation still passes: the same first N lines of the same log intern the same names in the same
       * order, which is the only reason an ordinal from the previous pass means anything at all.
       */
      private static long NameHash(DamageFactTable facts, int idx)
        => idx >= 0 && idx < facts.InternedNames.Count
             ? StringComparer.Ordinal.GetHashCode(facts.NameOf(idx)) : -1;

      private static long FactWatermark(DamageFactTable facts, int ordinal)
      {
        ref readonly var f = ref facts.RefAt(ordinal);   // the ordinal door, not the buffer: see DamageFactTable.RefAt

        /*
         * Bit layout matters here because this is a CHANGE DETECTOR: the old line packed Seq at bit 40, a 16-bit defender id at
         * bit 8 and TypeId in the low byte. With ids now 32 bits (see DamageFactTable.InternName), `id << 8` would drop the high
         * bits of name #70,000 and a fact whose defender changed could watermark identically to one that did not — a projection
         * that continues when it should restart. So: Seq in the high half, the whole id in the low half (nothing shifted, nothing
         * discarded), and TypeId folded by the multiply chain instead of sharing the id's bits.
         */
        var h = ((long)f.Seq << 32) ^ (uint)f.DefIdx;
        h = (h * 31) + f.TimeS;
        h = (h * 7) + f.TypeId;
        return (h * 31) + (NameHash(facts, f.AtkIdx) * 7) + NameHash(facts, f.DefIdx);
      }

      private static long DeathWatermark(DamageFactTable facts, int ordinal)
      {
        ref readonly var d = ref facts.Deaths[ordinal];
        return (((long)d.Seq << 32) ^ d.TimeS) * 31 + NameHash(facts, d.KilledIdx);
      }
    }

    /*
     * The derive pass's memory: it holds one ProjectionState and the damage index built with it, and decides whether the
     * next projection may continue that state or has to start over. DeriveEngine holds one of these per log-open; tests
     * drive it directly, which is why the decision lives here rather than inline in the session's derive lambda.
     *
     * The gate has two halves and both have to hold (see ProjectionState):
     *
     *   The FACTS still line up — the watermarks name the same facts they did last pass, so continuing means appending
     *   rather than re-walking. Rows carry names and totals rather than ordinals, but the index carries ordinals.
     *
     *   The ANSWERS are the same ones these rows were projected under — `EntityTimeline.AnswerStamp()`, not `StateStamp()`. Rows migrate
     *   when evidence arrives that changes a routing: a name that turns out to be a raider loses its row to the mob it was fighting, a charm
     *   window re-attributes an evening, an override moves a name across the board. Provenance that changes nothing of the kind does not.
     *
     *   Why the weaker question is safe HERE and why it was not safe for the boards until today: what this fold may not be blind to is exactly
     *   what the walk reads, and the walk's whole identity surface is seven calls — `IdentityAt`, `IsCharmedAt`, `CharmStartAfter`,
     *   `IsConfirmedRaidPersonAt`, `IsOurPetAt`, `IsRaidVictimAt`, `HasIndependentIdentity` — each a function of (kind, interval bounds, owner)
     *   plus charm-ness, all five of which `AnswerStamp` folds and all of which are pinned individually in `EntityAnswerStampTest`. Strength and
     *   rule names are not read here: `IdentityAt`/`AffiliationAt` resolve those into the winning kind themselves. The audit is in
     *   docs/DesignNotes.md → "The stamp asks two different questions"; a THIRD predicate reading anything else would make this gate wrong, and
     *   wrong the expensive way — plausible rows kept forever — so an answer-relevant read must be added to the fold, not worked around.
     *
     *   What it buys, on the field report's own numbers: a pass whose only new information is what this application remembered used to re-walk
     *   every fact (measured 1,077 ms at 8,014,198 facts) AND throw away the index — which discards the per-row materialization cache
     *   (`FightFactIndex.SummaryFightFor`), so the boards re-materialized 2.6 M records they had just materialized (measured 1.5 s, twice).
     *
     * Being wrong the safe way is cheap (one extra full pass); being wrong the other way would show plausible numbers from
     * stale rows. FightProjectionIncrementTest asserts the two paths agree at every boundary a fixture can produce.
     */
    internal sealed class FightProjectionCache
    {
      private readonly ProjectionState _state = new();
      private FightFactIndex _index;

      // Diagnostics for the derive report: what the last pass did, and why.
      public bool LastPassContinued { get; private set; }

      public FightFactIndex Index => _index;
      public long Stamp => _state.Stamp;

      public IReadOnlyList<DerivedFight> Project(DamageFactTable facts, EntityTimeline timeline)
      {
        var stamp = timeline.AnswerStamp();
        var canContinue = _index is not null && _state.Stamp == stamp && _state.Covers(facts);

        _state.Stamp = stamp;
        if (!canContinue)
        {
          // Both halves of the rebuild: a fresh index (its ordinal lists would otherwise describe the old walk) and a
          // state with nothing in it, because rows are the accumulated work of the verdicts they were folded under.
          _state.Reset();
          _index = new FightFactIndex(timeline);
        }

        var rows = FightProjection.Continue(_state, facts, timeline, _index.OnFact);
        _state.MarkWatermark(facts);
        LastPassContinued = canContinue;
        return rows;
      }
    }

    public static List<DerivedFight> Build(DamageFactTable facts, EntityTimeline timeline, FactOwnershipHandler ownerSink = null)
      => Continue(new ProjectionState(), facts, timeline, ownerSink);

    /*
     * Fold every fact from state.ThroughOrdinal onward into the carried state and publish the row list. Returns the same
     * list Build would have returned for these facts and this classification — see the class comment on ProjectionState
     * for when that is true, and FightProjectionIncrementTest for the property being asserted at split points rather
     * than argued.
     */
    public static List<DerivedFight> Continue(ProjectionState state, DamageFactTable facts, EntityTimeline timeline,
                                              FactOwnershipHandler ownerSink = null)
    {
      // Open row per name; a gap (or a slain line) closes it and the next exchange opens a fresh
      // row under the same key. Rows keep arrival order until the final sort.
      Dictionary<string, DerivedFight> open = state.Open;

      // The last CLOSED row per name, so a pet row can point at the encounter its charm closed. CharmPetRows needs
      // that link: the pet's facts begin after the encounter ends, so "does it overlap the selection?" alone can
      // never bring a hidden pet row back into a stats build — and hiding must not delete damage.
      Dictionary<string, DerivedFight> lastClosed = state.LastClosed;
      List<DerivedFight> rows = state.Completed;

      Dictionary<string, Queue<DeathFact>> deathsByName = state.DeathsByName;
      var allDeaths = facts.Deaths;
      for (var d = state.ThroughDeath; d < allDeaths.Length; d++)
      {
        var death = allDeaths[d];
        var killed = facts.NameOf(death.KilledIdx);

        /*
         * A name that dies WHILE charmed does not die to the raid. Its NPC row was already closed at the
         * charm itself (DerivedFightEnd.Charmed — the raid got that mob by taking it), so letting this death
         * mark a row as slain would hand out a second kill for one corpse, and would do so on whichever row
         * of that name happened to be open — possibly a different instance three pulls away. It is skipped
         * here exactly as a party member's death is not a raid kill. Nothing is lost: the charm window itself
         * records this death as the reason it closed (CharmEndReason.Death on ClassificationOutcome.Charms).
         */
        if (DiedWhileCharmed(timeline, killed, death.TimeS)) continue;

        if (!deathsByName.TryGetValue(killed, out var q)) deathsByName[killed] = q = new Queue<DeathFact>();
        q.Enqueue(death);
      }

      state.ThroughDeath = allDeaths.Length;

      var allFacts = facts.Facts;
      for (var ordinal = state.ThroughOrdinal; ordinal < allFacts.Length; ordinal++)
      {
        var fact = allFacts[ordinal];
        if (fact.AtkIdx == fact.DefIdx) continue;                    // self damage
        var atkName = facts.NameOf(fact.AtkIdx);

        var defName = facts.NameOf(fact.DefIdx);
        var t = fact.TimeS;

        /*
         * Spell feedback, and the ORDER this is asked in matters more than it looks. "You have taken N damage from X."
         * leaves a SPELL in the attacker field, and when the DB says that spell only hits its caster the fact is nobody
         * swinging at a mob — side evidence from it would fabricate an NPC out of the operator's own cast, and as a row
         * it would be a fight against a verb. But the test is on the NAME, and names collide with spells: on
         * eqlog_Kizant_xegony-03-01-2024.txt every fact of `Darkside` (44,553 hits, 5,644,042,554 damage, the raid's own
         * registered pet per the seed) vanished here before a single row could be opened, because the spell DB carries a
         * self-target damaging spell by that name and the name is what got looked up. A meter reading rows would have
         * dropped that player's whole evening — silently, and identically in the fight list and the summary, so nothing
         * downstream could see it.
         *
         * So the question is asked of the COMBATANT, not just the string: a name that already reads as one of ours at
         * this second is fighting, and its damage counts. Unknown names (the feedback case, where nothing else ever
         * claimed the name) stay dropped exactly as before.
         */
        if (!TryCombatSides(fact, timeline, atkName, defName, out var atkSide, out var defSide)) continue;

        var key = CombatAnchor(timeline, atkName, defName, t, atkSide, defSide);
        // Only a player-side attacker earns roll-up credit; Unknown remains uncredited.
        var creditAttacker = atkSide == Side.Player;

        open.TryGetValue(key, out var row);

        // A slain line ends the engagement only once a STRICTLY LATER timestamp arrives - the
        // same boundary the legacy engine's deferred slain flush drew (currentTime > _slainTime), so
        // same-second damage (the killing blow and "was slain" share a second-resolution stamp,
        // e.g. Waxwork Lancer @ 18:36:52) still lands in the old fight. "dt <= t" split that
        // combat mid-second: the first same-second fact consumed the death, closed the row at
        // the previous second, and every remaining hit of the kill - including the killing blow
        // itself - opened a zero-length live row behind the dead one.
        // A charm sighting closes this name's NPC engagement the way a slain line does — as a death, with
        // the reason kept so the list can say "charmed" instead of implying a killing blow. Checked before
        // the death boundary on purpose: when both happened, the charm came first.
        if (row is not null && ClosesForCharm(timeline, key, row.LastTime, t))
        {
          row.Dead = true;
          row.EndReason = DerivedFightEnd.Charmed;
          rows.Add(row);
          open.Remove(key);
          lastClosed[key] = row;
          row = null;
        }

        /*
         * A death may only close a row that was OPEN when it happened. The queue is keyed by NAME, and one name
         * carries many mobs in a night: on the Egg pull of `eqlog_Kizant_xegony.txt` `A corrupted egg` is slain at
         * 18:52:52, then 18:52:56, then again 18:52:56 while the raid is already swinging at the next egg that
         * answers to that name. A death sitting in the queue can therefore PREDATE the row created after it, and
         * applying it anyway killed that newborn row at its own first second - the grid showed two `A corrupted
         * egg` rows both beginning 18:52:58, one of them living 0 seconds. That is not a fight, and the list has
         * no honest word for it. A death older than the row's own beginning describes an earlier holder of the
         * name (whose facts already went to an earlier row, or to none at all), so it is dropped and the next one
         * is asked.
         */
        if (row is not null && deathsByName.TryGetValue(key, out var deaths))
        {
          while (deaths.TryPeek(out var stale) && stale.TimeS < row.BeginTime) deaths.Dequeue();

          if (deaths.TryPeek(out var death) && death.TimeS < t)
          {
            deaths.Dequeue();
            row.Dead = true;
            row.DeathTime = death.TimeS;
            row.DeathSequence = death.Seq;
            row.EndReason = DerivedFightEnd.Slain;
            rows.Add(row);
            open.Remove(key);
            lastClosed[key] = row;
            row = null;   // any further deaths wait for a later fact of this name
          }
        }

        if (row is not null && t - row.LastTime > EngagementGapS)
        {
          row.EndReason = DerivedFightEnd.Gap;
          rows.Add(row);
          open.Remove(key);
          lastClosed[key] = row;
          row = null;
        }
        if (row is null)
        {
          row = new DerivedFight
          {
            Name = key,
            BeginTime = t,
            LastTime = t,
          };
          open[key] = row;
        }
        if (IsFlipped(timeline, key, t))
        {
          row.CharmedOwned = true;

          /*
           * Two different things are inside that condition, and only one of them is a pet: a charmed RAID MEMBER is
           * an encounter the raid has to fight (she stays in the list, badge and all), while a charmed MOB is ours —
           * a pet, and pets have no fight row (CharmPetRows).
           *
           * The answer is "does this name have an NPC reason of its own", and it has to be asked that way: the
           * charm confirm registers its OWN target as an NPC at R9-charm's strength, which outranks most real ones,
           * so the winning assignment says "NPC" for a raid member too. Hiding her would delete an encounter the
           * raid fought and took back; a charmed mob has npc.txt, its article shape or its own violence behind it.
           */
          if (timeline.HasIndependentIdentity(key, IdentityKind.Npc, t))
          {
            row.RaidPet = true;
            if (lastClosed.TryGetValue(key, out var prev))
            {
              // Chain forward: a second pet row of the same pull (the raid stopped swinging at it for ten minutes
              // and it reopened) belongs to the same encounter as the first, not to itself.
              row.EncounterRow = prev.RaidPet ? prev.EncounterRow
                                 : prev.EndReason == DerivedFightEnd.Charmed ? prev
                                 : null;
            }
          }
        }

        // A row keyed on one of OUR pets (the raid's stray swings landed on it) is not an encounter: same
        // display rule as a charmed mob's half-row - off the list, its damage still reachable by the stats
        // builds it belongs to (CharmPetRows.WithHiddenPets brings these back on span overlap; they have no
        // EncounterRow because there was never an encounter to pair with).
        else if (timeline.IsOurPetAt(key, t)) row.RaidPet = true;

        // Reported after the boundary checks and row creation, so every fact is announced exactly
        // once and to the row that really carries it in its totals. The direction is the same comparison
        // DamageToOwner is built from, one expression, so an index filled here cannot disagree with the row.
        var aimedAtAnchor = string.Equals(key, defName, StringComparison.Ordinal);
        var target = CombatTarget(timeline, defName, t, defSide, aimedAtAnchor);

        row.FactEnd = ordinal + 1;
        row.LastSequence = Math.Max(row.LastSequence, fact.Seq);
        ownerSink?.Invoke(fact, ordinal, row, target);

        var isHit = LabelTypes.IsHit(fact.TypeId);
        if (isHit)
        {
          row.DamageHits++;
          row.DamageTotal += fact.Total;
          // Engagement totals in both directions land on DamageTotal; the split keeps the
          // legacy-comparable number (damage dealt TO the owner) readable next to it.
          if (aimedAtAnchor) row.DamageToOwner += fact.Total;
          else row.DamageByOwner += fact.Total;
        }
        if (t < row.BeginTime) row.BeginTime = t;
        if (t > row.LastTime) row.LastTime = t;

        /*
         * The per-direction windows, in legacy's shape: BeginDamageTime is written by the FIRST record aimed at
         * this row's name and LastDamageTime by every one (FightManager does exactly this on its defender
         * branch), and a row that was never hit stays NaN instead of claiming its own birth second as damage.
         * Sectionizer walks LastDamageTime for the non-tanking divider list, so a zero-length window is not
         * cosmetic - it decides where "Fight N" boundaries land for every row of the derived grid. The direction
         * is the same `aimedAtAnchor` comparison that splits DamageToOwner from DamageByOwner and files the
         * index, so no two seams can disagree about which way a fact pointed.
         */
        if (aimedAtAnchor)
        {
          row.BeginDamageTime = double.IsNaN(row.BeginDamageTime) ? t : row.BeginDamageTime;
          row.LastDamageTime = t;
        }
        else
        {
          row.BeginTankingTime = double.IsNaN(row.BeginTankingTime) ? t : row.BeginTankingTime;
          row.LastTankingTime = t;
        }

        // Player roll-up: a player-side attacker gets credit under its raw name - stronger than
        // the legacy registry-gated rollup, which silently dropped unregistered players. A
        // player-side DEFENDER is a victim, never credited.
        if (creditAttacker)
        {
          if (!row.PlayerRollup.TryGetValue(atkName, out var agg))
          {
            agg = new NameAgg();
            row.PlayerRollup[atkName] = agg;
          }
          agg.Add(fact.Total, isHit, null, t);
        }
      }

      /*
       * Publish. A row still open here is NOT finished - the next fact of that name continues it - so it stays out of
       * state.Completed, and the death queues are READ rather than drained. Draining here would steal a boundary from the
       * in-loop check: `A corrupted egg` slain at :52 and swung at again at :58 must be two rows (the law the loop's
       * stale-death handling exists for), and a row already dead-marked with its death consumed would weld them into one.
       * Marking Dead/EndReason on the live row is safe: it says what a full pass over these facts would say at this
       * moment, and if it turns out premature the loop re-decides the boundary when the next fact of that name arrives.
       */
      state.ThroughOrdinal = allFacts.Length;

      var published = new List<DerivedFight>(rows.Count + open.Count);
      published.AddRange(rows);
      foreach (var row in open.Values)
      {
        // Deaths after the last exchange still mark the engagement they follow.
        // A slain line lands shortly after the final exchange - inside one engagement's tail.
        if (!row.Dead && DeathWithinTail(deathsByName, row, out var death))
        {
          row.Dead = true;
          row.DeathTime = death.TimeS;
          row.DeathSequence = death.Seq;
          row.EndReason = DerivedFightEnd.Slain;
        }

        // The usual fate of a charmed mob's row: the raid charms it and never swings again, so there is no
        // later fact to notice the boundary in. Same rule as the in-loop check, at the end of the list.
        if (!row.Dead && ClosesForCharm(timeline, row.Name, row.LastTime, double.PositiveInfinity))
        {
          row.Dead = true;
          row.EndReason = DerivedFightEnd.Charmed;
        }
        published.Add(row);
      }

      // Engagement order is the display order; Ids are 1-based positions after sorting.
      published.Sort(static (a, b) => a.BeginTime.CompareTo(b.BeginTime));
      for (var i = 0; i < published.Count; i++) published[i].Id = i + 1;
      return published;
    }

    /*
     * Does this row have a slain line inside its tail? A death from before the row began belongs to an earlier holder of
     * the name and is SKIPPED rather than consumed (Publish above is why consumption belongs to the loop alone); any
     * later one within EventTailWindowS says this engagement ended in death.
     */
    private static bool DeathWithinTail(Dictionary<string, Queue<DeathFact>> deathsByName, DerivedFight row,
      out DeathFact death)
    {
      death = default;
      if (!deathsByName.TryGetValue(row.Name, out var deaths)) return false;

      foreach (var candidate in deaths)
      {
        if (candidate.TimeS < row.BeginTime) continue;                 // an earlier holder of this name
        if (candidate.TimeS > row.LastTime + EventTailWindowS) return false;
        death = candidate;
        return true;
      }

      return false;
    }

    /*
     * Which side was this name fighting on at time t - identity, then CHARM reversal, then OWNERSHIP.
     *
     * A Friendly interval written by an ownership rule is a static statement of allegiance, not a flip:
     * only R9-charm windows reverse a name's side for their duration. Source names are the rule tags stamped
     * by ClassificationRules; "R9-charm" prefixes both charm variants.
     *
     * Ownership (EntityTimeline.IsOurPetAt) is neither of those. The target frame's `Targeted (NPC)` verdict on
     * `Useless` or `Dangle` is correct - it is not a player - and it is Certain, so inference may not relabel it;
     * but "not a player" is not "the enemy's", and the raid healing it from fifteen directions says whose side it
     * is on. So its identity stays NPC and the interval moves its DAMAGE: player-side, into whatever mob row it
     * swings at, instead of keying a monster row in the enemy column (R18, design doc §"Targeted (NPC) means not
     * a player"). A name whose identity is already raid-side needs no interval to be ours, which is why this is
     * asked only of NPC-side names.
     */
    internal static Side SideAt(EntityTimeline timeline, string name, double t)
    {
      var kind = timeline.IdentityAt(name, t);
      if (kind is IdentityKind.Unknown) return Side.Unknown;

      /*
       * A Spell name keeps the NPC arm of this decision exactly as it behaved before the kind existed: R21's rows WERE NPC, and
       * every fight row, +Pets fold and tanking column was measured under that reading, so a vocabulary change must not move one
       * number. Whether the raid's own DoT should count as the raid's OUTPUT is a separate measurable question and would arrive
       * with its own board diff (docs/DesignNotes.md → "A name that equals a spell is not a spell row").
       */
      if (IsFlipped(timeline, name, t))
        return kind is IdentityKind.Npc or IdentityKind.Spell ? Side.Player : Side.Npc;
      if (kind is not IdentityKind.Npc and not IdentityKind.Spell)
        return Side.Player;
      return timeline.IsOurPetAt(name, t) ? Side.Player : Side.Npc;
    }

    // For admitted combat only: projection and receipts must choose the same encounter anchor.
    // A charmed raider owns its NPC-side row; source-less spell damage instead stays on its defender.
    internal static string CombatAnchor(EntityTimeline timeline, string attacker, string defender, double t,
      Side attackerSide, Side defenderSide)
    {
      if (attackerSide == Side.Player) return defender;
      if (defenderSide == Side.Player) return attacker;
      if (attackerSide == Side.Npc)
        return defenderSide == Side.Unknown || IsFlipped(timeline, attacker, t) ? attacker : defender;
      if (defenderSide == Side.Npc) return defender;
      return UnknownCombatTargetsDefender(attacker, defender) ? defender : attacker;
    }

    // Positive person proof precedes the anchor, and the anchor precedes the Unknown-victim fallback.
    // A charmed NPC at the anchor stays output; an off-anchor NPC victim can earn neither board.
    internal static FactTarget CombatTarget(EntityTimeline timeline, string defender, double t, Side defenderSide,
      bool aimedAtAnchor)
    {
      if (timeline.IsConfirmedRaidPersonAt(defender, t) || (!aimedAtAnchor && defenderSide == Side.Player))
        return FactTarget.RaidSide;
      if (aimedAtAnchor) return FactTarget.AtOwner;
      return timeline.IsRaidVictimAt(defender, t) ? FactTarget.RaidSide : FactTarget.Neither;
    }

    // Projection and receipts share the Unknown/Unknown anchor decision; neither name gains an identity from it.
    internal static bool UnknownCombatTargetsDefender(string attacker, string defender) =>
      !PlayerRegistry.IsPossiblePlayerName(defender) || PlayerRegistry.IsPossiblePlayerName(attacker);

    // The same admission rule serves projection and the overlay's pending-activity hint. Unknown names remain
    // eligible: a new opponent must not lose its first hits just because classification has not caught up.
    internal static bool TryCombatSides(DamageFact fact, EntityTimeline timeline, string attacker, string defender,
      out Side attackerSide, out Side defenderSide)
    {
      var t = fact.TimeS;
      attackerSide = SideAt(timeline, attacker, t);
      defenderSide = SideAt(timeline, defender, t);
      if (fact.AtkIdx == fact.DefIdx) return false;
      if (attackerSide != Side.Player && ClassificationRules.IsSelfTargetDamageSpell(attacker)) return false;
      if (attackerSide == Side.Player && defenderSide == Side.Player)
        return IsFlipped(timeline, defender, t) && !IsFlipped(timeline, attacker, t);
      return attackerSide != Side.Npc || defenderSide != Side.Npc
        || IsFlipped(timeline, attacker, t) || fact.AttackerIsSpell;
    }

    // True when a charm interval has this name on the opposite of its identity side at t.
    private static bool IsFlipped(EntityTimeline timeline, string name, double t)
      => timeline.IsCharmedAt(name, t);

    /*
     * Was this death ours rather than the raid's? A charm that ends AT a death writes its interval up to that
     * instant, and interval bounds are exclusive — so the killing moment itself reads as "not charmed" unless
     * the question is also asked a moment earlier. The slack costs one thing worth naming: a real kill of a
     * DIFFERENT mob with the same name in the second after a window closes is skipped too, which is the
     * same-name ambiguity this feature already accepts (CharmWindowPolicy reports that share instead of
     * pretending to resolve it).
     */
    private static bool DiedWhileCharmed(EntityTimeline timeline, string name, double t)
      => timeline.IsCharmedAt(name, t) || timeline.IsCharmedAt(name, t - CharmDeathSlackS);

    /*
     * Does a charm sighting end this row? It has to be a sighting of THIS engagement: the charm begins after
     * the row's last fact and within the event tail window (EventTailWindowS), so a raid charming a mob of the
     * same name three pulls from now cannot retroactively kill this row. The tail window is deliberately wider
     * than the 30 s that splits rows: the raid's LAST swing at a mob and the mesmerist's spell on it are two
     * acts by two people, and a mob charmed a minute after anybody touched it is still the mob that row is
     * about — while a minute of nothing in its own exchange stream is a fight that stopped. That also means the
     * ambiguity is inherited, not invented — two live mobs called `a skeleton` are one name to this log, and
     * CharmWindowPolicy reports that share (SameNameFactCount) rather than pretending otherwise.
     */
    private static bool ClosesForCharm(EntityTimeline timeline, string key, double lastTimeS, double nowS)
    {
      var start = timeline.CharmStartAfter(key, lastTimeS);
      return !double.IsNaN(start) && start <= nowS && start - lastTimeS <= EventTailWindowS;
    }

  }
}
