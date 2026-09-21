namespace EQLogParser.Mirror
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
    Merc = 3
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
    public AffiliationInterval(AffiliationKind kind, double t0, double t1, int strength, string source)
    {
      Kind = kind;
      T0 = t0;
      T1 = t1;
      Strength = strength;
      Source = source;
    }

    public AffiliationKind Kind;
    public double T0;
    public double T1;   // exclusive upper bound; double.PositiveInfinity for open-ended
    public int Strength;
    public string Source;
  }

  // Per-name identity + time-scoped affiliation intervals (D3 choice (b)). Written by evidence
  // (manual in Phase 1, rules from Phase 2), read by the deriver. Thread model: single writer
  // (rules/manual) before derivation; reads during derivation are lock-free.
  internal sealed class EntityTimeline
  {
    private readonly Dictionary<string, List<IdentityAssignment>> _identity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AffiliationInterval>> _affiliation = new(StringComparer.Ordinal);

    // ---- evidence input ----

    public void SetIdentity(string name, IdentityKind kind, int strength, string source, double effectiveFrom = double.NegativeInfinity)
    {
      if (string.IsNullOrEmpty(name)) return;
      var list = GetOrCreate(_identity, name);
      var assignment = new IdentityAssignment(kind, effectiveFrom, strength, source);
      // keep the list sorted by effective time so AffiliationAt-style lookups can sweep;
      // conflicts are resolved at read time by (strength, effectiveFrom) — nothing is silently dropped.
      list.Add(assignment);
      list.Sort((a, b) => a.EffectiveFrom.CompareTo(b.EffectiveFrom));
    }

    public void AddAffiliation(AffiliationKind kind, string name, double t0, double t1, int strength, string source)
    {
      if (string.IsNullOrEmpty(name)) return;
      var list = GetOrCreate(_affiliation, name);
      list.Add(new AffiliationInterval(kind, t0, t1, strength, source));
      list.Sort((a, b) => a.T0.CompareTo(b.T0));
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

    public bool HasIdentity(string name) => _identity.ContainsKey(name);

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

    // One cursor per name over a time-ordered fact stream — the pointer sweep §6 asks for.
    // Yields AffiliationAt without re-walking from the start each call.
    public AffiliationCursor OpenAffiliationCursor(string name) => new(this, name);

    public IReadOnlyCollection<string> NamesWithIdentity() => _identity.Keys;

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
