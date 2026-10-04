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
    Npc = 2,
    Merc = 3,
    // Owned companion (catalog R5): player-side by affiliation, never a player itself.
    Pet = 4
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

    // ---- evidence input ----

    public void SetIdentity(string name, IdentityKind kind, int strength, string source, double effectiveFrom = double.NegativeInfinity)
    {
      if (string.IsNullOrEmpty(name)) return;
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
    }

    public void AddAffiliation(AffiliationKind kind, string name, double t0, double t1, int strength, string source, string owner = null)
    {
      if (string.IsNullOrEmpty(name)) return;
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
      return kind is not IdentityKind.Npc and not IdentityKind.Pet;
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

    // One cursor per name over a time-ordered fact stream — the pointer sweep §6 asks for.
    // Yields AffiliationAt without re-walking from the start each call.
    public AffiliationCursor OpenAffiliationCursor(string name) => new(this, name);

    public IReadOnlyCollection<string> NamesWithIdentity() => _identity.Keys;

    /*
     * "Did classification reach the same conclusions as last time?" The one user is the incremental fight projection
     * (FightProjection.Continue — see DeriveEngine): rows carried across passes stay valid only while the classification
     * they were projected over is unchanged, and every question the projection asks (IdentityAt, IsCharmedAt, IsOurPetAt,
     * CharmStartAfter, HasIndependentIdentity, OwnerOf) reads these two dictionaries and nothing else.
     *
     * It is an INCREMENTAL digest: every accepted insertion adds a term to `_digest` (see that field), so reading this
     * costs O(1) instead of walking every name and entry — which was not academic, hashing 2,436 names on Incogitable
     * measured ~250 ms per pass, more than the fold it was guarding (docs/DesignNotes.md → "Continuing a projection").
     * The two mutators are the only writers and both drop re-assertions, so rules replaying the same evidence over and
     * over leave it alone, which is precisely what lets a live refresh skip re-projection.
     *
     * So THE LAW stays, in the form that matters: these two stores are the whole state, and any THIRD store added here
     * would be invisible to this stamp and let a stale row survive a verdict that should have moved it — silently, with
     * plausible numbers. `EntityTimelineDigestTest` refuses a third dictionary appearing without this being told, and pins
     * both directions of the version: new evidence moves it, the same evidence again does not.
     */
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

    internal sealed class AffiliationCursor
    {
      private readonly EntityTimeline _timeline;
      private List<AffiliationInterval> _intervals;
      private int _index;

      internal AffiliationCursor(EntityTimeline timeline, string name)
      {
        _timeline = timeline;
        timeline._affiliation.TryGetValue(name, out _intervals);
      }

      public AffiliationKind At(double t)
      {
        var list = _intervals;
        if (list is null || list.Count == 0) return AffiliationKind.Enemy;

        while (_index < list.Count && list[_index].T0 <= t)
        {
          // advance past intervals that ended before t, but remember the strongest live one
          _index++;
        }

        // walk back over intervals containing t (few in practice: one charm window per name)
        var best = AffiliationKind.Enemy;
        var bestStrength = int.MinValue;
        for (var i = _index - 1; i >= 0 && list[i].T0 <= t; i--)
        {
          if (t < list[i].T1 && list[i].Strength >= bestStrength)
          {
            best = list[i].Kind;
            bestStrength = list[i].Strength;
          }
        }
        return best;
      }
    }
  }
}
