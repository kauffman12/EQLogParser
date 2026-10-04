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

    /*
     * Optional reporter: which row a fact ended up owning. The projection is the single place that
     * decides sides, so anything that needs "the facts of this fight" (the damage summary fed from the
     * derived list, FightSummarySource) has to be told here rather than re-deciding somewhere else —
     * a second copy of these rules would drift the moment a rule changes, and the drift would show up
     * as a summary that disagrees with the row it was opened from.
     *
     * The three targets are described next to FactTarget. Two of them are directions inside the row, and the third
     * exists because "not aimed at the row" is NOT the same question as "landed on one of us": it used to be one
     * bool, which quietly made every away-from-owner fact a tanking-report fact, and most of what an NPC row aims
     * away from itself lands on a pet or another mob. Measured on Incogitable against the classification the app runs:
     * legacy's unfiltered "damage taken" is 7,114,675,399, of which 4,823,236,582 sits on names classified as NPC and
     * 428,146,449 on pets; what the three targets leave is 1,850,853,404 — 91,036 facts with a Player behind them and
     * 9,480 a Merc (`RealLogBoardsTest` prints that census, `HitByNpcCensusTest` the residue around it).
     *
     * AtOwner is the same test that splits DamageToOwner from DamageByOwner — the split legacy draws too, with
     * FightManager putting everything aimed at the npc in DamageBlocks and the mob's own output in TankingBlocks.
     * Note it is not "the attacker was player-side": an unclassified name hitting a known NPC aims at the owner
     * too, and belongs in those blocks even while its per-player credit waits for a classification pass. Consumers
     * cannot re-derive any of this later, because which side a name was on depends on the timeline at the fact's
     * own second.
     */
    /*
     * Which half of the row's record set a fact belongs to, decided once here because the two boards must be
     * fillable from ONE answer:
     *
     *   AtOwner  — damage aimed at the entity the row is keyed on, i.e. the raid's output on that fight.
     *              Same comparison DamageToOwner is built from, so an index filled here cannot drift from the row.
     *   RaidSide — a hit that landed on one of OUR people: the tanking half, "damage the raid received".
     *              Asked as identity rather than as "everything else", because most facts that point away from an NPC
     *              row land on something that is not a person at all (see EntityTimeline.IsRaidVictimAt). AtOwner is
     *              tested before this one, and that order is load-bearing: an unclassified mob being hit by a raider
     *              satisfies "not known to be a pet or mob" and must not become somebody's damage taken.
     *   Neither  — everything else: a mob biting another mob, a boss hitting somebody's pet. Real events kept in the
     *              capture, but they are nobody's damage and nobody's damage-taken, so filing them anywhere would
     *              inflate a number no grid can account for.
     */
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
      internal readonly Dictionary<string, Queue<long>> DeathsByName = new(StringComparer.Ordinal);

      /*
       * How far each event stream has been folded in. Facts and deaths are append-only within a log-open, but ordinal 500
       * of one table is not ordinal 500 of another, so the watermark records the identity of the fact it stopped on: an
       * unrelated (or rebuilt) table whose prefix happens to be the same length would otherwise be accepted, and its facts
       * would then be routed into rows — and into ordinal lists — that describe a different log.
       */
      internal int ThroughOrdinal;
      internal int ThroughDeath;

      // The classification these rows were projected over (EntityTimeline.StateStamp).
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
      private static long NameHash(DamageFactTable facts, short idx)
        => idx >= 0 && idx < facts.InternedNames.Count
             ? StringComparer.Ordinal.GetHashCode(facts.NameOf(idx)) : -1;

      private static long FactWatermark(DamageFactTable facts, int ordinal)
      {
        ref readonly var f = ref facts.Facts[ordinal];
        var h = (long)f.Seq << 40 ^ (uint)f.DefIdx << 8 ^ f.TypeId;
        h = h * 31 + f.TimeS;
        return h * 31 + NameHash(facts, f.AtkIdx) * 7 + NameHash(facts, f.DefIdx);
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
     *   The CLASSIFICATION is the same one these rows were projected under. Rows migrate when evidence arrives: a name
     *   that turns out to be a raider loses its row to the mob it was fighting, a charm window re-attributes an evening,
     *   an override moves a name across the board. So any change in the timeline stamps differently and buys a full
     *   rebuild — which is precisely what every pass used to be, and remains the answer whenever anything about identity
     *   moved.
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
        var stamp = timeline.StateStamp();
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

      Dictionary<string, Queue<long>> deathsByName = state.DeathsByName;
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

        if (!deathsByName.TryGetValue(killed, out var q)) deathsByName[killed] = q = new Queue<long>();
        q.Enqueue(death.TimeS);
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
        var atkSide = SideAt(timeline, atkName, t);
        var defSide = SideAt(timeline, defName, t);

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
        if (atkSide != Side.Player && ClassificationRules.IsSelfTargetDamageSpell(atkName)) continue;

        string key;
        bool creditAttacker;  // attacker was player-side: it gets damage credit in the owner's roll-up
        bool charmed = false;

        if (atkSide == Side.Player && defSide == Side.Player)
        {
          /*
           * Friendly fire is dropped — EXCEPT when the defender is a charmed mob. A mob under charm reads
           * player-side, but it is not a raider: swings that land on it come from the raid's own mistake (AoE
           * splash, or an add the charm did not take out of the fight) and they are real damage a meter has to
           * keep. Silently deleting them would shrink a player's total for the crime of hitting their own pet,
           * which is how this looked before charm windows could be seen at all. They key on the mob's name, so
           * the row that opens is the mob's post-charm half; nobody gets credit for being hit by their allies.
           *
           * A charmed RAIDER attacking us reads the other way (her identity flips to Npc-side, she owns her own
           * row), and a pet damaging its own side stays dropped — the log gives us no story to tell about it.
           */
          // Our pet reads player-side by ownership rather than by charm, and gets the same exemption: see the
          // comment above - the raid's swings on its own pet are real damage and stay counted.
          if ((!IsFlipped(timeline, defName, t) && !timeline.IsOurPetAt(defName, t)) || IsFlipped(timeline, atkName, t)) continue;
          key = defName;
          creditAttacker = true;
        }
        else if (atkSide == Side.Player && (defSide == Side.Npc || defSide == Side.Unknown))
        {
          key = defName; creditAttacker = true;
        }
        else if (defSide == Side.Player && (atkSide == Side.Npc || atkSide == Side.Unknown))
        {
          key = atkName; creditAttacker = false;
        }
        else if (atkSide == Side.Npc && defSide == Side.Npc)
        {
          // Both sides NPC-side happens when a Friendly interval flips a player into the enemy
          // column: the charmed raider owns that row herself. Two mobs on each other (a mob pet,
          // or boss-vs-boss noise) is not a raid fight at all and stays out of the list.
          if (!IsFlipped(timeline, atkName, t)) continue;
          key = atkName; creditAttacker = false; charmed = true;
        }
        else if (atkSide == Side.Npc && defSide == Side.Unknown)
        {
          /*
           * A mob hitting a name NO RULE PLACED used to be dropped with the mob-on-mob noise above. Measured on
           * eqlog_Incogitable_xegony.txt that cost the tank board every point of damage several raiders took:
           * `Worthless` 159 facts / 1,479,310, `Boner` 186 / 1,389,581, `Morris` 68 / 1,360,995 — and in the same
           * minutes each of those names spends 200-300 of its own swings on the raid's enemies, which the branch
           * below happily files. It also swallows `Ddread`, a raider the roster DOES know: 80 of her 85 incoming
           * facts land before her registry verification replays (RegistrySeed starts an in-log verification at that
           * instant rather than retroactively), so her column reads 12,275 against legacy's 181,670.
           *
           * Dropping is not a neutral choice here. The same name at the same second was a combatant while it swung
           * and becomes noise while it is hit, and who got hit is the entire subject of the tank grid — where
           * legacy lists all of these names today. So the fact is announced like any other, keyed on the mob that
           * is doing the hitting (the raid's encounter), and IsRaidVictimAt — the engine's own exclusion, which until
           * this branch existed could only ever be reached by facts that arrived on some other route — decides
           * whether it counts as damage somebody received. Genuine mob-on-mob stays out: that defender reads Npc.
           */
          key = atkName; creditAttacker = false;
        }
        else if (defSide == Side.Npc && atkSide == Side.Unknown)
        {
          // known NPC defending against an unclassified attacker: our side is the anchor
          key = defName; creditAttacker = false;
        }
        else
        {
          // Unknown vs Unknown: nobody classified. Legacy's last-resort tiebreak keys on the
          // defender unless one name reads as obviously not-a-player, so nothing goes missing -
          // and a later classification pass redistributes these facts to real rows anyway.
          if (!PlayerRegistry.IsPossiblePlayerName(defName)) key = defName;
          else if (!PlayerRegistry.IsPossiblePlayerName(atkName)) key = atkName;
          else key = defName;
          // Nobody is classified: crediting an unclassified attacker as "player" would poison the
          // roll-up. A later pass that classifies it re-attributes the credit.
          creditAttacker = false;
        }

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
          while (deaths.TryPeek(out var stale) && stale < row.BeginTime) deaths.Dequeue();

          if (deaths.TryPeek(out var dt) && dt < t)
          {
            deaths.Dequeue();
            row.Dead = true;
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
        if (charmed || IsFlipped(timeline, key, t))
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
        /*
         * AtOwner is asked FIRST, and the order carries meaning. A player swinging at an unclassified name is the
         * common case in a fresh pull — and "the defender is not known to be a pet or a mob" is true of exactly that
         * mob, so testing the victim question first would file the raid's own opening damage as damage somebody
         * received. Aimed at the row's anchor wins; only what points away from the anchor can be someone getting hit.
         */
        var aimedAtAnchor = string.Equals(key, defName, StringComparison.Ordinal);
        FactTarget target;
        if (timeline.IsConfirmedRaidPersonAt(defName, t))
        {
            // A person got hit. True even when the row is keyed on her own name, which is what legacy's
            // defender-key tiebreak does to an attack from a mob nobody has classified yet — filing that as the
            // row's damage would move her incoming hits onto the outgoing side of her own number.
            target = FactTarget.RaidSide;
        }
        else if (aimedAtAnchor)
        {
            // Aimed at the anchor: the raid's output on this fight. Asked before the victim question below, because
            // an unidentified mob satisfies "not known to be a pet or a mob" and must not become damage taken.
            target = FactTarget.AtOwner;
        }
        else
        {
            // Points away from the anchor at a name we have never called a pet or a mob — the Unknown residue, in on
            // the reasoning that whatever is taking a mob's hits is one of ours (EntityTimeline.IsRaidVictimAt).
            target = timeline.IsRaidVictimAt(defName, t) ? FactTarget.RaidSide : FactTarget.Neither;
        }

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
        if (!row.Dead && DeathWithinTail(deathsByName, row))
        {
          row.Dead = true;
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
    private static bool DeathWithinTail(Dictionary<string, Queue<long>> deathsByName, DerivedFight row)
    {
      if (!deathsByName.TryGetValue(row.Name, out var deaths)) return false;

      foreach (var dt in deaths)
      {
        if (dt < row.BeginTime) continue;                 // an earlier holder of this name
        return dt <= row.LastTime + EventTailWindowS;
      }

      return false;
    }

    /*
     * Which side was this name fighting on at time t - identity, then CHARM reversal, then OWNERSHIP.
     *
     * A Friendly interval from R5-called (a summoned pet) is a static statement of allegiance, not a flip:
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

      if (IsFlipped(timeline, name, t))
        return kind is IdentityKind.Npc ? Side.Player : Side.Npc;
      if (kind is not IdentityKind.Npc)
        return Side.Player;
      return timeline.IsOurPetAt(name, t) ? Side.Player : Side.Npc;
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
