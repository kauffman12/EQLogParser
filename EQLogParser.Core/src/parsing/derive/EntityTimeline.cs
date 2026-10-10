namespace EQLogParser
{
  // Stable answer to "is this name a player / NPC / merc?" — one value per name over the whole
  // log, retroactive (D3). Each assignment carries an effective-from time so Phase 1 can seed
  // ingest-time behavior (verification replay) and Phase 2 rules emit retroactive evidence
  // (effectiveFrom = start of log); higher strength wins on conflict, same strength: later time.
  internal enum IdentityKind : byte
  {
    Unknown = 0,
    Player = 1,

    /*
     * THE APP'S WORD, and it is narrower than the client's. In this application NPC means NONE of player, pet or
     * mercenary - a hostile-shaped name with no ownership behind it. The client's target frame is what muddies it:
     * `Targeted (NPC)` asserts only "not a player", and it prints that for somebody's custom-named wolf exactly as it
     * prints it for a skeleton, so the frame's verdict may not outvote a POSITIVE claim. When a rule proves ownership
     * - the name spells it (`Ammeren`s pet`, R5), a spell only a pet can be aimed at hit it (R24), its own summon line
     * named it - the kind is Pet and the cell says Pet. See docs/DesignNotes.md → "What NPC means here".
     */
    Npc = 2,
    Merc = 3,
    // Owned companion (catalog R5): player-side by affiliation, never a player itself.
    Pet = 4,

    /*
     * A SPELL NAME standing where a fighter would be (catalog R21) - neither side. The client writes `… damage from Slicing
     * Energy by .` with an empty caster slot and fills it with the spell, so the word that reaches the name pool is a spell and
     * not a creature; the raid's own DoT effects arrive the same way (`Bastion of Divinity Rk. II healed Xxuro over time for
     * 6670 hit points by Bastion of Divinity Effect II.`), equally nameless and equally not an opponent. NPC made those look
     * like mobs the raid had failed to fight; Player / "our side" is what the operator reported seeing. Spell answers the
     * identity question without claiming one: never one of ours (IdentityLookup), never a victim below, and it routes exactly
     * as the NPC verdict it replaced, so adding the kind moves no number on any board.
     */
    Spell = 5
  }

  // Answer to "whose side is this name on, right now?" — genuinely time-scoped (charm windows,
  // petted/un-petted, freed mobs). Display kind in the UI is f(identity, affiliation).
  internal enum AffiliationKind : byte
  {
    Enemy = 0,
    Friendly = 1,
    PetOfPlayer = 2
  }

  internal sealed class IdentityAssignment
  {
    public IdentityKind Kind;
    public double EffectiveFrom;
    public int Strength;   // R-catalog strength; higher overrides lower, never retracted by it
    public string Source;  // rule id / "Manual" / "RegistrySeed" — provenance for the report

    public IdentityAssignment(IdentityKind kind, double effectiveFrom, int strength, string source)
    {
      Kind = kind;
      EffectiveFrom = effectiveFrom;
      Strength = strength;
      Source = source;
    }
  }

  internal struct AffiliationInterval
  {
    public AffiliationInterval(AffiliationKind kind, double t0, double t1, int strength, string source, string owner = null)
    {
      Kind = kind;
      T0 = t0;
      T1 = t1;
      Strength = strength;
      Source = source;
      Owner = owner;
    }

    public AffiliationKind Kind;
    public double T0;
    public double T1;   // exclusive upper bound; double.PositiveInfinity for open-ended
    public int Strength;
    public string Source;

    // Whose companion this interval makes the name, when the log says so. Null for the two kinds of
    // "on your side" that name nobody: a raid-buff-driven Friendly stretch and a CHARM whose caster was
    // never in the text (measured: third-party charms write no cast line at all, so most charmed mobs
    // in somebody else's log are ownerless by construction, not by missing data).
    public string Owner;
  }

  // Per-name identity + time-scoped affiliation intervals (D3 choice (b)). Written by evidence
  // (manual in Phase 1, rules from Phase 2), read by the deriver. Thread model: single writer
  // (rules/manual) before derivation; reads during derivation are lock-free.
  internal sealed class EntityTimeline
  {
    /*
     * BOTH name keys are case-INSENSITIVE, and that is a rule rather than a detail.
     *
     * Every name the parser hands out goes through ParserUtil.UpdateAttacker/UpdateDefender/UpdateSlain, which
     * finish with TextUtils.CapitalizeFirst — a fact calls it "A Bone Walker". The evidence lines do not go
     * through those at all: "a bone walker has been charmed.", "Targeted (NPC): a bone walker", a wear-off or a
     * tell, all lower-case. Ordinal keys therefore split one entity into two spellings — and the miss is
     * invisible, because the side that reads the other spelling just gets Unknown and carries on with its
     * fallback instead of failing.
     *
     * EQ itself treats names as case-insensitive (two entities that differ only by letter case cannot coexist
     * in a zone), which is why PlayerRegistry has always been OrdinalIgnoreCase; the timeline is the same
     * entity store and had not caught up.
     *
     * Storage agrees instead of keeping every spelling: `DamageFactTable.InternName` keys the same way and holds one
     * id per entity, displayed as `CapitalizeFirst`. An ordinal pool was the worse half of this split — it gave one
     * mob two ids, so every structure keyed by id carried half of it and the row's displayed name depended on which
     * line arrived first (`NamePoolTest`).
     */
    private readonly Dictionary<string, List<IdentityAssignment>> _identity = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<AffiliationInterval>> _affiliation = new(StringComparer.OrdinalIgnoreCase);

    /*
     * CROSS-THREAD READS TAKE THIS. A timeline is built and folded on the derive worker (DeriveEngine's pass runs under
     * Task.Run off a DispatcherTimer tick), but verdicts are asked from elsewhere: IdentityLookup.LiveVerdict answers menu
     * enables on the UI thread, EventViewer walks the snapshot for its kill rows, and SpellDamageStatsViewer filters its
     * "players only" grid inside a Task.Run. A plain Dictionary read racing an insert that resizes is not a benign miss — it
     * throws (IndexOutOfRange on a bucket walk) or reports a name as absent, and a menu handler that throws is a crash.
     *
     * The convention, therefore: **mutators lock here, and any caller from another thread takes SyncRoot around its own
     * read** (Monitor is reentrant, so a caller may hold it across several lookups). Nothing on the read side of this class
     * locks, and that is deliberate rather than an omission: the rule book asks a name's identity millions of times per
     * classify pass, and an uncontended lock costs enough to move a pass by tens of percent — paying it to protect a
     * handful of UI reads is the wrong trade. The trade holds only because there is exactly one mutator at any moment (the
     * pass that owns the instance: a cheap lane folds into the carried timeline, a full pass swaps a fresh one in under the
     * engine's gate) and readers never mutate, so the only race is writer-against-foreign-reader — which the two sides
     * above do exclude. A second writer, or a reader that runs on the derive thread while another pass might run, breaks the
     * argument and would need read-side locking for real.
     *
     * DeriveEngine._liveKindAt (the seam's live hop) and EventViewer's kill rows are the two foreign readers today; both
     * take the lock. NamesWithIdentity() is NOT part of the safe set — it hands out a live Keys view and belongs to the
     * folding thread (its only callers are tests).
     *
     */
    internal readonly object SyncRoot = new();

    /*
     * An INCREMENTAL digest of everything this store holds, summed in at insert time. Both mutators below drop a
     * re-assertion of something already recorded (identical kind + strength + time + source + owner returns early, because
     * it adds no read-time information), so this value moves exactly when the answer to any lookup could have changed —
     * which is what StateStamp reports, in O(1) instead of walking every name.
     *
     * The per-insertion term is ADDED (commutative) rather than chained, so the order evidence happened to arrive in does
     * not move the stamp: RegistrySeed walks a dictionary whose enumeration order is unspecified, and a digest that read
     * that order would rebuild every time the registry grew without any verdict changing. Addition rather than XOR because
     * one tuple can legitimately be recorded twice (the same name, strength and span as both an identity and an interval);
     * XOR would let the pair cancel to nothing.
     *
     * Nothing else writes these two dictionaries: no removals, no in-place edits (grep for `.T1 =`/`.Strength =` finds
     * none). If a removal or a revision API is ever added, it has to move this digest too.
     */
    private long _digest;

    /*
     * THE ANSWER digest. `StateStamp()` answers "did the evidence change?"; this answers "would a board route a fact
     * differently?", and the gap between those two questions is where a wasted rebuild lives (2026-10 field report,
     * `EQLogParser.log`: select all right after a load, boards build, then clear and rebuild ~9 s later for a stamp that moved
     * while every figure stayed identical - 708 rows both times, the same 2,647,774 heals materialized both times).
     *
     * The mechanism that produced it: each Full pass rebuilds its timeline from scratch and seeds it from this application's own
     * memory, and the FIRST pass writes that memory. So the second pass records claims the first never had - same kind, a
     * `Prior:`-style source - and an evidence digest sees new tuples even though no name's answer moved.
     *
     * What it folds is NOT the stored tuples, which was the first attempt and is unsound: resolution reads `strength` and, among
     * equal strength at equal time, ARRIVAL PRECEDENCE, so two states holding the same kinds at different strengths answer
     * `IdentityAt` differently while a kind/time/charm tuple fold reported "unchanged" (a promoted Player/Certain over an existing
     * Player/Weak moved no bit - pinned by `AStrengthPromotionThatChangesTheWinnerMovesTheAnswerStamp`). Folding every strength
     * instead would put the ledger replay straight back into the digest, because the memory lane re-records what it already
     * concluded at `RuleStrength.Weak` - which is exactly the pass this stamp exists to see through.
     *
     * So a name contributes ONE term, recomputed from the answers the store actually hands out: `IdentityAt` at each of its own
     * breakpoints and at +infinity, `IdentityWithSource` (which has its own tie rule), the earliest independent (non-charm) reason
     * per kind; and on the affiliation side, at every interval boundary, `AffiliationAt`'s winner plus that winner's charm bit,
     * `OwnerOf`, `IsOurPetAt` and `CharmStartAfter`. A claim nobody can read an answer out of - a provenance-only restatement, a
     * weaker same-kind re-assertion - changes no term and moves nothing. A claim that promotes, competes, flips charm-ness or covers
     * new seconds moves that name's term, and with it the sum.
     *
     * Kept as a SUM of one term per name (commutative) so the digest does not depend on which name was touched when - the seed
     * walks `PlayerRegistry`, whose enumeration order shifts as the registry grows. It DOES now depend on arrival order where
     * arrival order decides an answer, which is the honest direction: those two states really do read differently, and a reuse gate
     * must not call them the same. Summed rather than XOR'd for the reason `StateStamp` carries: one name legitimately holds
     * evidence in both stores, and an XOR pair cancels to nothing.
     *
     * Cost: recomputed only when an insertion survives the dedupe - once per distinct claim, never per fact - over that ONE name's
     * list, which is a handful of entries; `ViewWalkLimit` takes the conservative branch (fold the size, so any change moves) on a
     * name with pathological evidence rather than walking quadratically.
     *
     * Nothing else writes these two dictionaries: no removals, no in-place edits (grep for `.T1 =`/`.Strength =` finds none). If a
     * removal or a revision API is ever added, it has to refresh these views too.
     */
    private const int ViewWalkLimit = 64;
    private readonly Dictionary<string, long> _identityAnswers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _affiliationAnswers = new(StringComparer.OrdinalIgnoreCase);
    private long _answerDigest;

    // ---- evidence input ----

    public void SetIdentity(string name, IdentityKind kind, int strength, string source, double effectiveFrom = double.NegativeInfinity)
    {
      if (string.IsNullOrEmpty(name)) return;
      lock (SyncRoot) SetIdentityLocked(name, kind, strength, source, effectiveFrom);
    }

    private void SetIdentityLocked(string name, IdentityKind kind, int strength, string source, double effectiveFrom)
    {
      var list = GetOrCreate(_identity, name);

      // Dedupe identical assignments: hot callers (owner-line damage facts, join-line churn)
      // re-assert the exact same tuple thousands of times per name, and a duplicate adds no
      // information at read time - conflicts resolve by (strength, effectiveFrom). Without this,
      // lists grew with the FACT count and were fully re-sorted per insert (measured: classify
      // spent 127 s on a 445k-fact log; the derive then hid behind "capturing..." in the app).
      for (var i = 0; i < list.Count; i++)
      {
        var a = list[i];
        if (a.Kind == kind && a.Strength == strength && a.EffectiveFrom == effectiveFrom
            && string.Equals(a.Source, source, StringComparison.Ordinal)) return;
      }

      // keep the list sorted by effective time so AffiliationAt-style lookups can sweep;
      // conflicts are resolved at read time by (strength, effectiveFrom) — nothing is silently dropped.
      InsertSortedByTime(list, new IdentityAssignment(kind, effectiveFrom, strength, source), static a => a.EffectiveFrom);
      _digest = unchecked(_digest + Term(0, name, (long)kind, strength, effectiveFrom, 0d, source, null));
      RefreshAnswer(_identityAnswers, name, IdentityAnswerView(name, list));
    }

    public void AddAffiliation(AffiliationKind kind, string name, double t0, double t1, int strength, string source, string owner = null)
    {
      if (string.IsNullOrEmpty(name)) return;
      lock (SyncRoot) AddAffiliationLocked(kind, name, t0, t1, strength, source, owner);
    }

    private void AddAffiliationLocked(AffiliationKind kind, string name, double t0, double t1, int strength, string source, string owner)
    {
      var list = GetOrCreate(_affiliation, name);

      // Same dedupe rationale as SetIdentity (identical interval = no new read-time information).
      for (var i = 0; i < list.Count; i++)
      {
        var a = list[i];
        if (a.Kind == kind && a.T0 == t0 && a.T1 == t1 && a.Strength == strength
            && string.Equals(a.Source, source, StringComparison.Ordinal)
            && string.Equals(a.Owner, owner, StringComparison.Ordinal)) return;
      }

      InsertSortedByTime(list, new AffiliationInterval(kind, t0, t1, strength, source, owner), static a => a.T0);
      _digest = unchecked(_digest + Term(1, name, (long)kind, strength, t0, t1, source, owner));
      RefreshAnswer(_affiliationAnswers, name, AffiliationAnswerView(name, list));
    }

    // Lists stay small per name (distinct assignments only), so a back-to-front scan beats
    // any binary-search ceremony and keeps the common append-at-end case one comparison.
    private static void InsertSortedByTime<T>(List<T> list, T item, Func<T, double> timeKey)
    {
      var t = timeKey(item);
      var i = list.Count;
      while (i > 0 && timeKey(list[i - 1]) > t) i--;
      list.Insert(i, item);
    }

    private static List<T> GetOrCreate<T>(Dictionary<string, List<T>> map, string key)
    {
      if (!map.TryGetValue(key, out var list))
      {
        list = [];
        map[key] = list;
      }
      return list;
    }


    // ---- lookups ----

    // Retroactive identity: the strongest assignment in force at +infinity.
    public IdentityKind Identity(string name) => IdentityAt(name, double.PositiveInfinity);

    // Strongest assignment with EffectiveFrom <= t. Used by the Phase 1 ingest-replay seeding and
    // will be what time-scoped rules feed in Phase 2.
    public IdentityKind IdentityAt(string name, double t)
    {
      if (!_identity.TryGetValue(name, out var list) || list.Count == 0) return IdentityKind.Unknown;

      var best = IdentityKind.Unknown;
      var bestStrength = int.MinValue;
      var bestTime = double.NegativeInfinity;
      foreach (var a in list)
      {
        if (a.EffectiveFrom > t) break;   // sorted by effective time
        if (a.Strength > bestStrength || (a.Strength == bestStrength && a.EffectiveFrom >= bestTime))
        {
          best = a.Kind;
          bestStrength = a.Strength;
          bestTime = a.EffectiveFrom;
        }
      }
      return best;
    }

    // Same resolution as IdentityAt, but reporting the WINNING STRENGTH too. A rule that must not reason
    // from its own weaker inferences (R15 will only accept a healer that line evidence or behaviour put on
    // our side, never one a Medium guess placed there) reads this instead of trusting the kind alone.
    public IdentityKind IdentityAt(string name, double t, out int strength, out string source)
    {
      strength = int.MinValue;
      source = null;
      if (!_identity.TryGetValue(name, out var list) || list.Count == 0) return IdentityKind.Unknown;

      var best = IdentityKind.Unknown;
      var bestTime = double.NegativeInfinity;
      foreach (var a in list)
      {
        if (a.EffectiveFrom > t) break;   // sorted by effective time
        if (a.Strength > strength || (a.Strength == strength && a.EffectiveFrom >= bestTime))
        {
          best = a.Kind;
          strength = a.Strength;
          bestTime = a.EffectiveFrom;
          source = a.Source;
        }
      }
      return best;
    }

    public bool HasIdentity(string name) => _identity.ContainsKey(name);

    /*
     * Does this name have a reason of its own to be this kind — an assignment whose source is not a charm line?
     * "X has been charmed" registers X as an NPC at R9-charm's own strength, and that stamp outranks most real
     * ones, so "which assignment won?" cannot answer the question. A charmed MOB has npc.txt, its article shape or
     * its own violence behind it; a raid member who is in this log chiefly because a boss charmed her has nothing
     * but the charm. FightProjection asks before calling a row a pet — pets have no fight-list row, she does.
     */
    public bool HasIndependentIdentity(string name, IdentityKind kind, double t)
    {
      if (!_identity.TryGetValue(name, out var list)) return false;

      foreach (var a in list)
      {
        if (a.EffectiveFrom > t) break;   // sorted by effective time
        if (a.Kind != kind) continue;
        if (a.Source is not null && a.Source.StartsWith("R9-charm", StringComparison.Ordinal)) continue;
        return true;
      }
      return false;
    }

    /*
     * EVERY claim this capture recorded for the name, in insertion order — the identity list's tooltip reads this so a hover
     * can say what EACH rule saw rather than only the winner. Two things it deliberately is not:
     *   - not a copy: the census walks thousands of names and an allocation per name is exactly the memory this feature was
     *     told not to spend; the caller enumerates and does not mutate (same read-only convention as every non-derive reader
     *     here — writes belong to the derive worker, UI readers take SyncRoot);
     *   - not filtered: lower-strength and superseded claims stay in, which is the whole point. IdentityAt still decides.
     */
    internal IReadOnlyList<IdentityAssignment> ClaimsOf(string name)
      => _identity.TryGetValue(name, out var list) ? list : System.Array.Empty<IdentityAssignment>();

    // Strongest assignment at +infinity, with its provenance (Phase 2 report input).
    public IdentityKind IdentityWithSource(string name, out string source)
    {
      source = null;
      if (!_identity.TryGetValue(name, out var list) || list.Count == 0) return IdentityKind.Unknown;

      var best = IdentityKind.Unknown;
      var bestStrength = int.MinValue;
      var bestTime = double.NegativeInfinity;
      foreach (var a in list)
      {
        if (a.Strength > bestStrength || (a.Strength == bestStrength && a.EffectiveFrom >= bestTime))
        {
          best = a.Kind;
          bestStrength = a.Strength;
          bestTime = a.EffectiveFrom;
          source = a.Source;
        }
      }
      return best;
    }

    // Affiliation in force at t: strongest interval containing t (t0 <= t < t1).
    public AffiliationKind AffiliationAt(string name, double t, out string source)
    {
      if (!_affiliation.TryGetValue(name, out var list) || list.Count == 0)
      {
        source = null;
        return AffiliationKind.Enemy;   // no friendly/pet evidence: default side is enemy
      }

      var best = AffiliationKind.Enemy;
      var bestStrength = int.MinValue;
      source = null;
      foreach (var iv in list)
      {
        if (iv.T0 > t) break;   // sorted by T0
        if (t >= iv.T1) continue;
        if (iv.Strength >= bestStrength)
        {
          best = iv.Kind;
          bestStrength = iv.Strength;
          source = iv.Source;
        }
      }
      return best;
    }

    // Whose companion this name is at t, or null when nothing claims it. The seam every pet/charm
    // consumer asks for ("is this row somebody's pet, and whose") without re-deriving affiliation and
    // without treating "Friendly" as ownership: a mob under a raid buff and a mob someone charmed both
    // read Friendly, only the second one has an Owner.
    public string OwnerOf(string name, double t)
    {
      if (!_affiliation.TryGetValue(name, out var list)) return null;

      var bestStrength = int.MinValue;
      string owner = null;
      foreach (var iv in list)
      {
        if (iv.T0 > t) break;   // sorted by T0
        if (t >= iv.T1 || string.IsNullOrEmpty(iv.Owner)) continue;
        if (iv.Strength >= bestStrength)
        {
          owner = iv.Owner;
          bestStrength = iv.Strength;
        }
      }
      return owner;
    }

    /*
     * True while an OWNERSHIP interval covers t - one that says the name fights for us: a possessive
     * owner line (R5-owner), a pet the operator mapped in petmapping.txt (RegistrySeed) or a bare custom
     * name the whole raid keeps healing
     * (R18-healedpet). Deliberately its own scan rather than `AffiliationAt(...) == PetOfPlayer`: a name can
     * hold a stronger `Friendly` at the same time (a charm window writes one), and "somebody owns this" is a
     * different question from "which kind won the interval table".
     *
     * This is NOT `IsCharmedAt`. A charm is a temporary flip of a name that is normally the enemy's; these
     * intervals say the name was never the enemy's. The projection reads both, and the fight list hides rows
     * this predicate covers while a char RAID MEMBER stays listed (CharmPetRows).
     */
    public bool IsOurPetAt(string name, double t)
    {
      if (!_affiliation.TryGetValue(name, out var list)) return false;

      foreach (var iv in list)
      {
        if (iv.T0 > t) break;   // sorted by T0
        if (iv.Kind == AffiliationKind.PetOfPlayer && t < iv.T1) return true;
      }
      return false;
    }

    /*
     * Would a hit landing on this name count as damage THE RAID TOOK? That is the tanking report's only question —
     * it lists what each of our people had to absorb — and answering it with `!Npc` would be wrong in two directions
     * that both show up as a raid total nobody can audit:
     *
     *   - A PET is not one of our people: mobs swatting somebody's swarm is real damage, it stays in the capture, and
     *     it is not what a player received. Legacy's unfiltered board carries 428,146,449 of it across 75 pet names on
     *     Incogitable; the derived board carries none, and the only non-Person facts left in it are the 86 Unknowns
     *     described below. If it is ever wanted it arrives as its own decision — folded
     *     under the owner the way the damage board does `X +Pets`, or as pet rows — with a settings word, not as a
     *     widened predicate that silently changes every tank number.
     *   - A CHARMED raider is, for those seconds, fighting against us. Identity says Npc while the window holds, so
     *     her incoming hits are not raid damage taken; they belong to whichever row owns the encounter she is being
     *     used in. This predicate deliberately does not undo the flip.
     *
     * So the rule is exclusion rather than proof: a hit counts unless we KNOW the name is not one of our people —
     * Pet or Npc at that second (a name can join, die, and be raised as a servant mid capture).
     *
     * Measured on eqlog_Incogitable_xegony.txt through the classification the app actually runs (registry seed + rule
     * table, which is what DeriveEngine builds inside every derive), the derived tank population is 91,036 facts /
     * 1.6389 B on names called Player and 9,480 / 196.4 M on Merc, and proof — `Identity is Player or Merc` — would
     * keep every one of those. What exclusion adds is **86 facts worth 15.6 M** (0.85 % of the board): defenders no
     * rule ever placed. That is the whole price, and it buys the case proof cannot see: a raid member who never
     * casts a database spell, never speaks and joined before the log opened stays Unknown all evening, and her damage
     * taken is precisely what this board exists to show. The trade is deliberately lopsided the other way too — the
     * same capture has only 4,323 unplaced-defender facts out of 1,727,942 hit facts (0.25 %) once R6/R14/R15 have run, so mob
     * noise leaking in through an Unknown defender is bounded by that residue, not by how many mobs exist.
     *
     * An earlier version of this note justified exclusion with a 90 % loss for proof. That was measured against the
     * timeline `PipelineHarness` hands back, which carries the registry seeds and NOT the rule table — on that state
     * R6 (npcs.txt), R14 (article shape) and R15 (healed by our side) have placed nothing, so nearly every mob reads
     * Unknown and both numbers were fiction. Ask identity questions of a classified timeline; `RealLogBoardsTest.Classified`
     * is the helper that does it.
     */
    public bool IsRaidVictimAt(string name, double t)
    {
      if (name is null) return false;

      var kind = IdentityAt(name, t);
      /*
       * Exclusion, and Spell belongs with the two it already excludes: an effect's name standing in a defender slot is not "one
       * of us being beaten on" any more than a mob or somebody's pet is. Left out, the tanking board would have widened with
       * DoT ticks on the same day the kind was added - silently, which is how that board's rules are written against.
       */
      return kind is not IdentityKind.Npc and not IdentityKind.Pet and not IdentityKind.Spell;
    }

    /*
     * AFFIRMATIVE evidence that this name is one of our people at t — Player or Merc by identity, not merely "we have
     * never said otherwise". `IsRaidVictimAt` is the exclusion test that decides what the tank board carries; this
     * one answers the sharper question, needed where a row happens to be keyed on the person herself (an unclassified
     * attacker hitting a known raider keeps the legacy defender key), because there "aimed at the row" and "one of us
     * got hit" are both true and only the second one describes what the number is for.
     *
     * Kept apart from `IsRaidVictimAt` so neither test quietly absorbs the other: widening this to Unknown would
     * route the raid's own opening swings at an unidentified mob into somebody's damage taken, which is the mistake
     * `IsRaidVictimAt` exists to avoid in the opposite direction.
     */
    internal bool IsConfirmedRaidPersonAt(string name, double t)
      => name is not null && IdentityAt(name, t) is IdentityKind.Player or IdentityKind.Merc;

    /*
     * Earliest charm-window start strictly after `afterS` for this name, NaN when there is none.
     *
     * The display list asks R9 exactly this one question — "did the raid take this mob off the enemy list
     * partway through?" — because that moment ENDS the NPC's engagement (DerivedFightEnd.Charmed) instead of
     * letting the row run on until an inactivity gap swallows it. A cursor/interval query would answer
     * "friendly at t", which is not what a closing row needs: by the time the projection notices, the name is
     * long since friendly again (or hostile again after the pet died).
     */
    public double CharmStartAfter(string name, double afterS)
    {
      if (!_affiliation.TryGetValue(name, out var list)) return double.NaN;

      // Sorted by T0, so the first charm interval past afterS is the answer — no scan to the end.
      foreach (var iv in list)
      {
        if (iv.T0 <= afterS) continue;
        if (iv.Kind == AffiliationKind.Friendly && iv.Source is not null
            && iv.Source.StartsWith("R9-charm", StringComparison.Ordinal)) return iv.T0;
      }
      return double.NaN;
    }

    // True when a charm window has this name on our side at t. Same test the projection flips rows with,
    // exposed because "did this name die to the raid, or did it die while it was ours" is a question the
    // death bookkeeping has to ask too.
    public bool IsCharmedAt(string name, double t)
      => AffiliationAt(name, t, out var source) == AffiliationKind.Friendly
         && source is not null && source.StartsWith("R9-charm", StringComparison.Ordinal);

    public IReadOnlyCollection<string> NamesWithIdentity() => _identity.Keys;

    /*
     * O(1) read of the answer digest described at the fields above: what this store's predicates would ANSWER, not a roll of what
     * was stored.
     *
     * Used where a rebuild is expensive and correctness depends only on answers: the fight projection's carry
     * (`FightProjectionCache`) and, through it, `FightTable.SelectionStamp` deciding whether selected boards went stale. Those
     * consumers must not rebuild because this application remembered something they already knew (DesignNotes -> "A board goes stale
     * on answers, not on provenance"), while a strength promotion, an arrival-precedence tie, a charm flip or a pet interval hidden
     * under a Friendly window ARE answers and may never pass as provenance.
     */
    public long AnswerStamp()
    {
      var hash = unchecked((long)14695981039346656037UL);

      static long Mix2(long h, long v) => unchecked((h ^ v) * 1099511628211L);

      hash = Mix2(hash, _answerDigest);
      hash = Mix2(hash, _identity.Count);
      return Mix2(hash, _affiliation.Count);
    }

    public long StateStamp()
    {
      var hash = unchecked((long)14695981039346656037UL);   // FNV-1a offset basis (wraps past long.MaxValue)

      static long Mix(long h, long v) => unchecked((h ^ v) * 1099511628211L);

      hash = Mix(hash, _digest);
      hash = Mix(hash, _identity.Count);      // redundant with the digest, kept so a mismatch shows in a diff
      return Mix(hash, _affiliation.Count);
    }

    /*
     * One insertion's contribution to `_digest`. Names hash CASE-INSENSITIVELY because that is how the stores key them
     * (a `bone walker` and `A bone walker` are one entity, so recording either spelling must land on the same term);
     * sources hash ordinally because they are vocabulary words (`R9-charm`, a Labels constant), not names.
     */
    /*
     * The ONE piece of provenance that is an ANSWER rather than a footnote.
     *
     * `AnswerStamp` erases rule names because no board question reads them — except this one. Three predicates recognize a charm sighting
     * by its source prefix, and every one of them routes damage:
     *   `HasIndependentIdentity` (FightProjection's charm/pet decision: a name whose only NPC reason IS the charm line becomes a hidden
     *    `RaidPet` row whose damage folds under its charmer — up to 90 M on one capture),
     *   `IsCharmedAt` / `CharmStartAfter` (the side flip for the window's duration, and which death counts as the kill), and
     *   `IsConfirmedRaidPersonAt`.
     * So "was this claim a charm" is part of what a board would route, and it is folded — as one bit, still not the source string.
     *
     * The mirror image matters more than the bit itself: `IdentityPriorStore.RememberedRules` contains `"R9-"`, so a later pass replays a
     * remembered charm under the ledger's spelling, `Prior:R9-charm`, which these same predicates read as NOT a charm (they test `StartsWith`).
     * That is not cosmetic bookkeeping — it is a name gaining a non-charm NPC reason, which turns a hidden pet row back into a listed one.
     * A digest blind to this would skip the rebuild and keep drawing the old routing: the stale-row-with-plausible-numbers failure this whole
     * stamp exists to avoid, arriving through the door the stamp itself opened. Mirroring `StartsWith` exactly is therefore load-bearing: the
     * digest moves whenever the predicates' own answer can move, and no more often than that.
     */
    private static bool IsCharmClaim(string source)
      => source is not null && source.StartsWith("R9-charm", StringComparison.Ordinal);

    /*
     * Swap one name's contribution into the answer sum. Returning when the recomputed view equals the stored one IS the
     * optimization: a restatement that changes no readable answer leaves the digest where it was, so a settle pass that only
     * re-records yesterday's conclusions asks nothing downstream to rebuild.
     */
    private void RefreshAnswer(Dictionary<string, long> views, string name, long term)
    {
      if (views.TryGetValue(name, out var old))
      {
        if (old == term) return;
      }
      views[name] = term;
      _answerDigest = unchecked(_answerDigest + term - old);
    }

    /*
     * Every answer this store gives for one name, hashed in a fixed order. Probing the PUBLIC predicates rather than re-walking
     * the fields is deliberate: the fold must not become a second implementation of resolution, or the day a tie rule changes the
     * digest quietly stops describing the answers it is supposed to gate.
     */
    private long IdentityAnswerView(string name, List<IdentityAssignment> list)
    {
      if (list.Count > ViewWalkLimit) return Term(0, name, list.Count, 0, double.NaN, 0d, null, null);

      /*
       * A SEGMENT walk, not a boundary walk: one entry per place where the resolved kind actually CHANGES, so two states that answer
       * the same questions at different second counts hash the same. Folding every breakpoint would let a weaker duplicate claim move
       * the digest for nothing - the same mistake in the other direction.
       */
      var h = ViewSeed;
      var prevBreak = double.NaN;
      IdentityKind? last = null;
      foreach (var a in list)   // sorted by EffectiveFrom
      {
        var t = a.EffectiveFrom;
        if (t == prevBreak) continue;
        prevBreak = t;

        var kind = IdentityAt(name, t);
        if (last == kind) continue;   // same answer as the previous segment: nothing changed to fold
        last = kind;
        h = MixView(h, t.GetHashCode());
        h = MixView(h, (long)kind);
      }

      /*
       * The +infinity readers, each with its own tie rule: `IdentityAt` prefers the later arrival among equal strength and time,
       * `IdentityWithSource` the first. Both reach boards and screens, so only probing both sees a precedence-driven change.
       */
      var tail = IdentityAt(name, double.PositiveInfinity);
      h = MixView(h, (long)tail);
      h = MixView(h, (long)IdentityWithSource(name, out _));

      /*
       * `HasIndependentIdentity` asks an EXISTS question over claims rather than "which kind won", so the winner walk above cannot
       * see it: a name whose only NPC reason is a charm line gains an independent reason the moment any other rule speaks, and
       * `FightProjection` lists or hides a row on exactly that. Its answer function is (kind, charm-ness) -> earliest effective time,
       * so fold one slot per pair in a fixed order.
       */
      Span<double> firstIndependent = stackalloc double[IndependentSlots];
      firstIndependent.Fill(double.PositiveInfinity);
      foreach (var a in list)
      {
        var slot = IndependentSlot(a.Kind, IsCharmClaim(a.Source));
        if (a.EffectiveFrom < firstIndependent[slot]) firstIndependent[slot] = a.EffectiveFrom;
      }
      for (var i = 0; i < firstIndependent.Length; i++) h = MixView(h, firstIndependent[i].GetHashCode());

      return h;
    }

    /*
     * The affiliation side, same idea and the same segment rule. Four readers walk that interval list four different ways - the
     * strongest winner (`AffiliationAt`, whose SOURCE `IsCharmedAt` reads), the strongest winner AMONG intervals naming an owner
     * (`OwnerOf`), an EXISTS over ownership intervals regardless of strength (`IsOurPetAt`), and the first charm start after a moment
     * (`CharmStartAfter`) - so folding the winner alone would let a second charm window, or a pet interval sitting under a stronger
     * Friendly one, leave this digest untouched while rows changed shape.
     */
    private long AffiliationAnswerView(string name, List<AffiliationInterval> list)
    {
      if (list.Count > ViewWalkLimit) return Term(1, name, list.Count, 0, double.NaN, double.NaN, null, null);

      var bounds = new double[list.Count * 2];
      var n = 0;
      foreach (var iv in list)
      {
        bounds[n++] = iv.T0;
        bounds[n++] = iv.T1;
      }
      Array.Sort(bounds, 0, n);

      var h = ViewSeed;
      long kind = -1, charm = -1, owner = 0, pet = -1, nextCharm = 0;
      long lastKind = -999, lastCharm = -999, lastOwner = -999, lastPet = -999, lastNext = -999;
      var emitted = false;
      var prevBound = double.NaN;
      for (var i = 0; i <= n; i++)
      {
        // The last step probes past every interval: that is where each reader falls back to its default.
        var t = i < n ? bounds[i] : double.PositiveInfinity;
        if (i < n && t == prevBound) continue;
        prevBound = t;

        kind = (long)AffiliationAt(name, t, out var source);
        charm = IsCharmClaim(source) ? 1 : 0;
        owner = OwnerOf(name, t) is { } named ? StringComparer.OrdinalIgnoreCase.GetHashCode(named) : 0;
        pet = IsOurPetAt(name, t) ? 1 : 0;
        nextCharm = double.IsNaN(CharmStartAfter(name, t)) ? 0 : CharmStartAfter(name, t).GetHashCode();

        if (emitted && kind == lastKind && charm == lastCharm && owner == lastOwner && pet == lastPet && nextCharm == lastNext) continue;
        emitted = true;
        h = MixView(h, t.GetHashCode());
        h = MixView(h, kind);
        h = MixView(h, charm);
        h = MixView(h, owner);
        h = MixView(h, pet);
        h = MixView(h, nextCharm);

        lastKind = kind; lastCharm = charm; lastOwner = owner; lastPet = pet; lastNext = nextCharm;
      }

      return h;
    }

    // IdentityKind is a closed byte vocabulary, so a fixed slot per (kind, charm-ness) keeps this fold's order stable.
    private const int IndependentSlots = 32;
    private static int IndependentSlot(IdentityKind kind, bool charmed)
      => (((int)kind) & 15) * 2 + (charmed ? 1 : 0);

    // Same mix as `Term`, reachable from the views above (Term's own copy is a local function on purpose: hot insert path).
    private const long ViewSeed = unchecked((long)14695981039346656037UL);
    private static long MixView(long v, long x) => unchecked((v ^ x) * 1099511628211L);

    private static long Term(int storeKind, string name, long kind, int strength, double t0, double t1,
                             string source, string owner)
    {
      var h = unchecked((long)14695981039346656037UL);

      static long Mix(long v, long x) => unchecked((v ^ x) * 1099511628211L);

      h = Mix(h, storeKind);
      h = Mix(h, StringComparer.OrdinalIgnoreCase.GetHashCode(name));
      h = Mix(h, kind);
      h = Mix(h, strength);
      h = Mix(h, t0.GetHashCode());
      h = Mix(h, t1.GetHashCode());
      h = Mix(h, source?.GetHashCode(StringComparison.Ordinal) ?? 0);
      return Mix(h, owner is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(owner));
    }
  }
}
