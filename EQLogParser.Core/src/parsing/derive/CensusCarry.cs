using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

/*
 * Annotations only, no null-flow analysis (the same pair ClassificationReport opens with).
 */
#nullable enable annotations

namespace EQLogParser
{
  // Carries append-only totals and hit pairs for one capture; final verdicts re-bucket pairs on every census.
  // Only the healer crowd depends on historical identity strength and needs stamp-based invalidation.
  // docs/DesignNotes.md → "A census carries what it already counted; only the healer crowd asks a stamp".
  internal sealed class CensusCarry
  {
    private DamageFactTable? _damageFacts;
    private HealFactTable? _healFacts;

    private double[] _damage = [];
    private double[] _healing = [];
    private long[] _events = [];
    private int[] _selfHits = [];
    private int[] _onRaid = [];
    private int[] _onMobs = [];
    private int[] _onPets = [];

    /*
     * The FINAL (+infinity) verdict of every name id folded so far, resolved once per census for the whole pool — the same resolution the walk
     * used to do inline, kept here because it feeds this file's bucketing and nothing else. Array default is `IdentityKind.Unknown`, which is
     * nobody's side; that is also what a census with no timeline gets, exactly as before.
     */
    private IdentityKind[] _verdict = [];

    /*
     * How many times each attacker hit each defender, self-hits excluded (see above). Keyed `(attacker << 32) | (uint)defender`.
     * Ordered pairs, because "A hit B" and "B hit A" belong to different rows.
     */
    private Dictionary<long, int> _pairs = new();

    private int _damageConsumed;
    private int _healConsumed;

    // Cast proof depends only on append-only evidence; healer crowds also depend on historical kind AND strength.
    // StateStamp catches strength changes; AnswerStamp also catches tie order, which the commutative state digest omits.
    private Dictionary<string, string>? _classSafeCast;
    private Dictionary<string, string>? _petSpellCast;
    private DamageFactTable? _proofFacts;
    private int _evidenceConsumed;

    private Dictionary<string, HashSet<string>>? _healerCrowds;
    private HealFactTable? _crowdStore;       // the heal store THIS census is reading
    private HealFactTable? _crowdBuiltFrom;   // the one the carried table was folded from
    private EntityTimeline? _crowdTimeline;
    private long _crowdStamp;                 // the full timeline state the carried table was folded under
    private long _crowdAnswerStamp;           // the resolution of equal-strength/time ties in that state
    private int _crowdLimit;                  // the census's heal boundary; rows past it are not counted yet
    private int _crowdConsumed;               // its own watermark, because the crowd is folded lazily

    /*
     * A row whose name id fell outside the census's pool snapshot was not counted. Today's walk drops it and finds it again on the next pass, so
     * it is never lost; a watermark that walked past it WOULD lose it permanently, so this flag makes the next census start over.
     */
    private bool _scanIncomplete;

    /// <summary>Per-name damage over every consumed fact. Valid for `[0, count)` of the census that asked.</summary>
    internal double[] Damage => _damage;

    /// <summary>Per-name healing over every consumed heal row.</summary>
    internal double[] Healing => _healing;

    /// <summary>Per-name line count: damage rows taken as attacker plus heal rows taken as healer.</summary>
    internal long[] Events => _events;

    /// <summary>Facts whose attacker and defender are the same name, kept apart from every side question.</summary>
    internal int[] SelfHits => _selfHits;

    /// <summary>Hits on defenders the CURRENT final verdicts call Player or Merc. Rebuilt every census — see `Bucket`.</summary>
    internal int[] OnRaid => _onRaid;

    /// <summary>Hits on defenders the CURRENT final verdicts call Npc.</summary>
    internal int[] OnMobs => _onMobs;

    /// <summary>Hits on defenders the CURRENT final verdicts call Pet.</summary>
    internal int[] OnPets => _onPets;

    /// <summary>Drops every accumulated byte and the tables the carry belongs to. A new capture, or a disposed engine, starts here.</summary>
    internal void Reset()
    {
      _damageFacts = null;
      _healFacts = null;
      _damage = [];
      _healing = [];
      _events = [];
      _selfHits = [];
      _onRaid = [];
      _onMobs = [];
      _onPets = [];
      _verdict = [];
      _pairs = new Dictionary<long, int>();
      _damageConsumed = 0;
      _healConsumed = 0;
      _scanIncomplete = false;

      _classSafeCast = null;
      _petSpellCast = null;
      _proofFacts = null;
      _evidenceConsumed = 0;

      _healerCrowds = null;
      _crowdStore = null;
      _crowdBuiltFrom = null;
      _crowdTimeline = null;
      _crowdStamp = 0;
      _crowdAnswerStamp = 0;
      _crowdLimit = 0;
      _crowdConsumed = 0;
    }

    /*
     * Folds the streams since the last census and re-buckets the direction counts.
     *
     * `count`, `factLimit` and `healLimit` come from the CALLER, read in the census's own order — fact bound first, then pool size — because
     * that ordering IS the boundary rule CensusGrowingPoolTest pins (a pool count taken before the fact bound sizes the arrays behind a name
     * still being interned). This method must not re-read them: it would pick a newer bound than the walk was sized for.
     */
    internal void Fold(DamageFactTable? damageFacts, HealFactTable? healFacts, EntityTimeline? timeline,
                       IReadOnlyList<string>? names, int count, int factLimit, int healLimit)
    {
      try
      {
        if (!Carries(damageFacts, healFacts, count, factLimit, healLimit)) Reset();
        _damageFacts = damageFacts;
        _healFacts = healFacts;

        if (count > _events.Length) Grow(count);
        ResolveVerdicts(timeline, names, count);

        if (damageFacts is not null) FoldDamage(damageFacts, factLimit, count);
        if (healFacts is not null) FoldHeals(healFacts, healLimit, count);
        FoldCastProof(damageFacts);

        // The first R15 row pays for the lazy crowd fold over the same boundary as the healing totals.
        _crowdStore = healFacts;
        _crowdTimeline = timeline;
        _crowdLimit = healLimit;

        Bucket(count);
      }
      catch
      {
        // A failed walk may have added rows without advancing its watermark; never replay onto those partial totals.
        Reset();
        throw;
      }
    }

    /// <summary>The cast that earned a spell-based verdict, for Row.ReasonDetail; nothing for any other rule's word.</summary>
    internal string? CastProofFor(string? source, string name)
    {
      if (source is not { Length: > 0 }) return null;

      var map = source switch
      {
        _ when source.StartsWith("R4-spell", StringComparison.Ordinal) => _classSafeCast,
        _ when source.StartsWith("R20-petspell", StringComparison.Ordinal) => _petSpellCast,
        _ => null,   // Prior:R4-spell is another log's conclusion, not this capture's cast.
      };

      return map is not null && map.TryGetValue(name, out var spell) ? spell : null;
    }

    // Only an R15 row needs the crowd walk. Its Strong, raid-side, non-self gates match the rule's own evidence gates.
    internal int CasterBreadth(string? source, string name)
    {
      if (source is not { Length: > 0 } || !source.StartsWith("R15-healed", StringComparison.Ordinal)) return 0;
      EnsureCrowds();
      return _healerCrowds is not null && _healerCrowds.TryGetValue(name, out var casters) ? casters.Count : 0;
    }

    /*
     * The carry answers for ONE capture: the same two table instances, no fewer rows than it has already eaten, and a pool that has not gone
     * backwards under an id it already counted. Anything else — a re-open handing back new tables, a store reset, a shrink — and the honest
     * answer is "start over", which is what an every-census walk always effectively was.
     */
    private bool Carries(DamageFactTable? damageFacts, HealFactTable? healFacts, int count, int factLimit, int healLimit)
      => !_scanIncomplete
         && ReferenceEquals(_damageFacts, damageFacts)
         && ReferenceEquals(_healFacts, healFacts)
         && _damageConsumed <= factLimit
         && _healConsumed <= healLimit
         && count >= _events.Length;

    /*
     * Grown rather than reallocated-and-refolded: the ids below the old length are counted and their answers stand. Only the new tail is zero —
     * which is what an uncounted name deserves — and `Bucket` rewrites its three arrays every census regardless.
     */
    private void Grow(int count)
    {
      Array.Resize(ref _damage, count);
      Array.Resize(ref _healing, count);
      Array.Resize(ref _events, count);
      Array.Resize(ref _selfHits, count);
      _onRaid = new int[count];
      _onMobs = new int[count];
      _onPets = new int[count];
      _verdict = new IdentityKind[count];
    }

    /*
     * WHOSE SIDE each name reads, resolved ONCE per census into a pool-sized array so the fold and the bucketing never touch a dictionary. The
     * FINAL verdict is what counts, so the sentence a row prints can never contradict the Type column beside it; charm windows are deliberately
     * not consulted — "Damaged Players" means "hit names this capture called players", the cheap claim and the honest one.
     */
    private void ResolveVerdicts(EntityTimeline? timeline, IReadOnlyList<string>? names, int count)
    {
      if (timeline is null || names is null)
      {
        // No timeline means Unknown for every name: nobody's side.
        Array.Clear(_verdict, 0, count);
        return;
      }

      // int, not short: this loop's bound is the pool size, and a short counter reaching its own ceiling would index negative.
      for (var i = 0; i < count; i++) _verdict[i] = timeline.IdentityAt(names[i], double.PositiveInfinity);
    }

    private void FoldDamage(DamageFactTable facts, int factLimit, int count)
    {
      var pairs = _pairs;
      foreach (var slice in facts.FactsIn(_damageConsumed, factLimit))
      {
        for (var i = 0; i < slice.Length; i++)
        {
          ref readonly var f = ref slice[i];

          /*
           * The census's own boundary belt, kept verbatim: the uint cast catches a new name and a corrupt negative in one compare. What changed
           * with the watermark is the consequence — see `_scanIncomplete`.
           */
          if ((uint)f.AtkIdx >= (uint)count || (uint)f.DefIdx >= (uint)count)
          {
            _scanIncomplete = true;
            continue;
          }

          _damage[f.AtkIdx] += f.Total;
          _events[f.AtkIdx]++;

          // Self first, exactly as the whole-capture walk did: a reflect is not "damaging players" (the Bjpotratz case, 61 copies).
          if (f.AtkIdx == f.DefIdx)
          {
            _selfHits[f.AtkIdx]++;
            continue;
          }

          // One probe for a pair already seen, which is the common case mid-fight: the same two names trade blows thousands of times.
          ref var hits = ref CollectionsMarshal.GetValueRefOrAddDefault(pairs, ((long)f.AtkIdx << 32) | (uint)f.DefIdx, out _);
          hits++;
        }
      }

      _damageConsumed = factLimit;
    }

    private void FoldHeals(HealFactTable heals, int healLimit, int count)
    {
      // Same boundary discipline as the damage fold: a bounded range read, never "whatever length the store has when the walk reaches it".
      foreach (var slice in heals.HealsIn(_healConsumed, healLimit))
      {
        for (var i = 0; i < slice.Length; i++)
        {
          ref readonly var h = ref slice[i];
          if ((uint)h.HealerIdx >= (uint)count)
          {
            _scanIncomplete = true;
            continue;
          }

          _healing[h.HealerIdx] += h.Total;
          _events[h.HealerIdx]++;
        }
      }

      _healConsumed = healLimit;
    }

    /*
     * The cast behind a spell-based verdict (`Row.ReasonDetail`): the first cast THIS capture whose spell name the R4 or R20 gate accepts, per
     * caster and gate. THE SAME GATES the rules apply, or the tooltip would name a cast that claimed nothing.
     *
     * KEPT SEPARATELY PER RULE GATE because the two gates claim different kinds: `IsClassSafeCast` is R4's (the caster is a player) and
     * `IsPetCastSpell` is R20's (the caster is somebody's pet). One shared "first accepted cast" map would let a name that cast both — the
     * pet-spell line first, the class line later — read "R4-spell" beside the PET's spell, crediting the wrong line for the verdict. A third
     * spell rule has to be listed here too (and in Row.ReasonDetail's own gate); a name missing from these maps simply gets no detail.
     *
     * Folded eagerly like the walk it replaces — evidence rows are sparse by design ("common, but not per-hit") — and `TryAdd` keeps the FIRST
     * answer, which is what makes continuing from a watermark exact: an ordinal folded earlier was the first then and stays first, exactly as
     * SetIdentity's own dedupe drops a later restatement.
     */
    private void FoldCastProof(DamageFactTable? facts)
    {
      if (facts is null) return;

      var evidence = facts.Evidence;
      var limit = evidence.Length; // One published prefix: the bound and its backing belong to the same snapshot.

      if (!ReferenceEquals(_proofFacts, facts) || limit < _evidenceConsumed)
      {
        _classSafeCast = null;
        _petSpellCast = null;
        _evidenceConsumed = 0;
      }

      _proofFacts = facts;
      if (_evidenceConsumed >= limit) return;

      var classSafe = _classSafeCast ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      var petSpell = _petSpellCast ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      for (var i = _evidenceConsumed; i < limit; i++)
      {
        ref readonly var e = ref evidence[i];
        if (e.Kind != EvidenceFact.EvCast) continue;

        var spell = facts.AuxOf(e.AuxIdx);
        if (string.IsNullOrEmpty(spell)) continue;
        var caster = facts.NameOf(e.NameIdx);
        if (string.IsNullOrEmpty(caster)) continue;

        if (ClassificationRules.IsClassSafeCast(spell!)) classSafe.TryAdd(caster!, spell!);
        else if (ClassificationRules.IsPetCastSpell(spell!)) petSpell.TryAdd(caster!, spell!);
      }

      _evidenceConsumed = limit;
    }

    // Neither stamp alone suffices: state tracks strength, answers track which equal-strength/time claim won.
    private void EnsureCrowds()
    {
      var heals = _crowdStore;
      var timeline = _crowdTimeline;

      if (heals is null || timeline is null)
      {
        _healerCrowds = null;
        _crowdBuiltFrom = null;
        _crowdConsumed = 0;
        return;
      }

      var stamp = timeline.StateStamp();
      var answerStamp = timeline.AnswerStamp();
      if (_healerCrowds is null || !ReferenceEquals(_crowdBuiltFrom, heals)
        || _crowdStamp != stamp || _crowdAnswerStamp != answerStamp || _crowdConsumed > _crowdLimit)
      {
        _healerCrowds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        _crowdBuiltFrom = heals;
        _crowdStamp = stamp;
        _crowdAnswerStamp = answerStamp;
        _crowdConsumed = 0;
      }

      if (_crowdConsumed >= _crowdLimit) return;

      var table = _healerCrowds;
      foreach (var slice in heals.HealsIn(_crowdConsumed, _crowdLimit))
      {
        for (var i = 0; i < slice.Length; i++)
        {
          ref readonly var h = ref slice[i];

          var healer = heals.NameOf(h.HealerIdx);
          var healed = heals.NameOf(h.HealedIdx);
          if (healer == healed) continue;                       // a self-heal says nothing about who tends them

          var hk = timeline.IdentityAt(healer, h.TimeS, out var hs, out _);
          if (hs < RuleStrength.Strong || !ClassificationRules.IsRaidSideKind(hk)) continue;

          if (!table.TryGetValue(healed, out var casters)) casters = table[healed] = new HashSet<string>(StringComparer.Ordinal);
          casters.Add(healer);
        }
      }

      _crowdConsumed = _crowdLimit;
    }

    /*
     * The verdict-dependent half, rebuilt from the pair counts on EVERY census: "Damaged Players" for a name means hits that landed on defenders
     * the FINAL verdicts call players, asked NOW — so a name the rules placed later moves between sentences on the next census exactly as a
     * whole-capture walk would have moved it, with no invalidation rule to get wrong. An unplaced defender earns nobody anything, which is the
     * same rule the per-fact walk applied.
     */
    private void Bucket(int count)
    {
      Array.Clear(_onRaid, 0, count);
      Array.Clear(_onMobs, 0, count);
      Array.Clear(_onPets, 0, count);

      if (_pairs.Count == 0 || count == 0) return;

      foreach (var pair in _pairs)
      {
        var attacker = (int)(pair.Key >> 32);
        var defender = (int)(uint)pair.Key;

        // A pair outside this snapshot's pool has no row to land in; it cannot have been folded, so this is a belt, not a repair.
        if ((uint)attacker >= (uint)count || (uint)defender >= (uint)count) continue;

        switch (_verdict[defender])
        {
          case IdentityKind.Player:
          case IdentityKind.Merc: _onRaid[attacker] += pair.Value; break;
          case IdentityKind.Pet: _onPets[attacker] += pair.Value; break;
          case IdentityKind.Npc: _onMobs[attacker] += pair.Value; break;
        }
      }
    }
  }
}
