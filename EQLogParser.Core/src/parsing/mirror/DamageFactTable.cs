using System.Runtime.InteropServices;

namespace EQLogParser.Mirror
{
  // Closed vocabulary of damage record types seen on DamageRecord.Type (Labels).
  // Stored as a byte in DamageFact; LabelOf() round-trips back to the Labels string so the
  // deriver can call the same StatsUtil.IsHitType / Labels checks the current pipeline uses.
  internal static class LabelTypes
  {
    public const byte Unknown = 0;
    public const byte Melee = 1;
    public const byte Dd = 2;
    public const byte Dot = 3;
    public const byte Proc = 4;
    public const byte Ds = 5;
    public const byte Rs = 6;
    public const byte Bane = 7;
    public const byte OtherDmg = 8;
    public const byte Absorb = 9;
    public const byte Miss = 10;
    public const byte Dodge = 11;
    public const byte Parry = 12;
    public const byte Block = 13;
    public const byte Invulnerable = 14;
    public const byte Riposte = 15;

    private static readonly Dictionary<string, byte> Map = new(StringComparer.Ordinal)
    {
      [Labels.Melee] = Melee,
      [Labels.Dd] = Dd,
      [Labels.Dot] = Dot,
      [Labels.Proc] = Proc,
      [Labels.Ds] = Ds,
      [Labels.Rs] = Rs,
      [Labels.Bane] = Bane,
      [Labels.OtherDmg] = OtherDmg,
      [Labels.Absorb] = Absorb,
      [Labels.Miss] = Miss,
      [Labels.Dodge] = Dodge,
      [Labels.Parry] = Parry,
      [Labels.Block] = Block,
      [Labels.Invulnerable] = Invulnerable,
      [Labels.Riposte] = Riposte
    };

    private static readonly string[] LabelsById =
    [
      null,
      Labels.Melee,
      Labels.Dd,
      Labels.Dot,
      Labels.Proc,
      Labels.Ds,
      Labels.Rs,
      Labels.Bane,
      Labels.OtherDmg,
      Labels.Absorb,
      Labels.Miss,
      Labels.Dodge,
      Labels.Parry,
      Labels.Block,
      Labels.Invulnerable,
      Labels.Riposte
    ];

    public static byte IdOf(string type) => Map.TryGetValue(type ?? string.Empty, out var id) ? id : Unknown;

    public static string LabelOf(byte id) => (uint)id < (uint)LabelsById.Length ? LabelsById[id] : null;

    // Mirrors StatsUtil.IsHitType for the closed vocabulary above (everything is a hit type
    // except Absorb/Dodge/Invulnerable/Miss/Parry/Riposte).
    public static bool IsHit(byte id) => id != Absorb && id != Dodge && id != Invulnerable && id != Miss && id != Parry && id != Riposte;
  }

  // One parsed damage line, exactly as the parser fired it. Names are interned indices (short)
  // into the fact table's name table — 24 B/record, no per-record allocation after init.
  internal readonly struct DamageFact
  {
    public const byte FlagAttackerIsSpell = 1;
    public const byte FlagOwnerInLine = 2;   // ownership evidence ("X`s pet", "Owner: X") present on the raw line

    // What PlayerRegistry.IsPetOrPlayerOrMerc answered for this name at the instant the parser
    // fired this event — captured by the mirror on the same thread, same instant as FightManager
    // consumes it. This is a FACT (what the current pipeline saw), not a judgment: replaying the
    // pipeline requires the same answers, and Phase 2 rules layer retroactive reclassification on
    // top of them. Verified mid-log names therefore read player-side only from their evidence time
    // onward — exactly like the live registry did.
    public const byte FlagAttkPlayerSide = 4;
    public const byte FlagDefPlayerSide = 8;

    // consumer-order sequence across BOTH fact streams — preserves within-second line order, which
    // the slain queue's flush-vs-enqueue decisions depend on
    public readonly int Seq;

    // dotnet-epoch seconds (DateTime origin, year 0001) as the parser fires them — ~6.4e10 in the
    // 2020s, so long is required; int overflowed silently and corrupted every timestamp
    public readonly long TimeS;
    public readonly short AtkIdx;
    public readonly short DefIdx;
    public readonly uint Total;
    public readonly uint OverTotal;
    public readonly byte TypeId;
    public readonly byte Flags;
    public readonly ushort SubIdx;   // index into the subtype table (spell/modifier name); 65535 = none

    public DamageFact(int seq, long timeS, short atkIdx, short defIdx, uint total, uint overTotal, byte typeId, byte flags, ushort subIdx)
    {
      Seq = seq;
      TimeS = timeS;
      AtkIdx = atkIdx;
      DefIdx = defIdx;
      Total = total;
      OverTotal = overTotal;
      TypeId = typeId;
      Flags = flags;
      SubIdx = subIdx;
    }

    public bool AttackerIsSpell => (Flags & FlagAttackerIsSpell) != 0;
    public bool OwnerInLine => (Flags & FlagOwnerInLine) != 0;
    public bool AttackerPlayerSide => (Flags & FlagAttkPlayerSide) != 0;
    public bool DefenderPlayerSide => (Flags & FlagDefPlayerSide) != 0;
  }

  // One "X was slain by Y!" / "X died." line.
  internal readonly struct DeathFact
  {
    public readonly int Seq;
    public readonly long TimeS;   // dotnet-epoch seconds — see DamageFact.TimeS
    public readonly short KilledIdx;
    public readonly short KillerIdx;

    public DeathFact(int seq, long timeS, short killedIdx, short killerIdx)
    {
      Seq = seq;
      TimeS = timeS;
      KilledIdx = killedIdx;
      KillerIdx = killerIdx;
    }
  }

  // A registry state change that the current pipeline consumes as a side effect. Captured in
  // consumer order so a replay can apply it at exactly the point the live pipeline did — most
  // importantly VerifiedPet, which makes FightManager.RemoveFight silently drop any active fight
  // for that name (no Dead flag), the only way a fight closes without damage, expiry, or a slain
  // line. The other kinds have no fight-side effect in today's pipeline; they are captured so
  // Phase 2 rules can reason about when each name became (or stopped being) player-side.
  internal readonly struct IdentityEvent
  {
    public const byte VerifiedPet = 1;
    public const byte VerifiedPlayer = 2;
    public const byte RemovedVerifiedPet = 3;
    public const byte RemovedVerifiedPlayer = 4;

    public readonly int Seq;
    // Best-effort: the last BeginTime observed before this event fired (registry events carry no
    // timestamp). Replay order is by Seq, never by this field.
    public readonly long TimeS;
    public readonly short NameIdx;
    public readonly byte Kind;

    public IdentityEvent(int seq, long timeS, short nameIdx, byte kind)
    {
      Seq = seq;
      TimeS = timeS;
      NameIdx = nameIdx;
      Kind = kind;
    }
  }

  // One identity/side evidence event captured by the mirror (Phase 2 rules input). Facts stay
  // factual: the event says what a parser recognized, never what it means. AuxIdx carries the one
  // secondary string some kinds have (class for who-roster, spell for casts, channel for chat),
  // -1 when absent.
  internal readonly struct EvidenceFact
  {
    public const byte EvTargetedPlayer = 1;
    public const byte EvTargetedNpc = 2;
    public const byte EvJoinedRaid = 3;
    public const byte EvLeftRaid = 4;
    public const byte EvJoinedGroup = 5;
    public const byte EvLeftGroup = 6;
    public const byte EvMercJoinedGroup = 7;
    public const byte EvRaidLeader = 8;
    public const byte EvWhoRoster = 9;      // aux: class name
    public const byte EvCalledToOwner = 10;
    public const byte EvCharmStart = 11;
    public const byte EvCharmEnd = 12;
    public const byte EvCast = 13;          // aux: spell name
    public const byte EvChat = 14;          // aux: channel ("guild", "group", "raid", ...)

    public readonly int Seq;
    public readonly long TimeS;
    public readonly short NameIdx;
    public readonly byte Kind;
    public readonly short AuxIdx;

    public EvidenceFact(int seq, long timeS, short nameIdx, byte kind, short auxIdx = -1)
    {
      Seq = seq;
      TimeS = timeS;
      NameIdx = nameIdx;
      Kind = kind;
      AuxIdx = auxIdx;
    }
  }

  // One taunt line. The current pipeline runs GetFight(npc) ?? Create(npc, t) on every taunt —
  // a taunt can therefore open a fight that no damage line ever touches.
  internal readonly struct TauntFact
  {
    public readonly int Seq;
    public readonly long TimeS;   // dotnet-epoch seconds — see DeathFact.TimeS
    public readonly short NpcIdx;

    public TauntFact(int seq, long timeS, short npcIdx)
    {
      Seq = seq;
      TimeS = timeS;
      NpcIdx = npcIdx;
    }
  }

  // Storage contract for the fact tables (D2: in-RAM now, shaped so a chunked spill can be added
  // later without touching the mirror or the deriver).
  internal interface IFactTable
  {
    int FactCount { get; }
    int DeathCount { get; }
    int IdentityEventCount { get; }
    int TauntCount { get; }
    IReadOnlyList<string> InternedNames { get; }
    short InternName(string name);
    string NameOf(short idx);
    short InternSubtype(string subtype);
    string SubtypeOf(ushort idx);
    void AddFact(DamageFact fact);
    void AddDeath(DeathFact death);
    void AddIdentity(IdentityEvent identity);
    void AddTaunt(TauntFact taunt);
    void AddEvidence(EvidenceFact evidence);
    short InternAux(string aux);
    string AuxOf(short idx);
    ReadOnlySpan<DamageFact> Facts { get; }
    ReadOnlySpan<DeathFact> Deaths { get; }
    ReadOnlySpan<IdentityEvent> IdentityEvents { get; }
    ReadOnlySpan<TauntFact> Taunts { get; }
    ReadOnlySpan<EvidenceFact> Evidence { get; }
  }

  // In-RAM implementation: preallocated buffers (capacity estimated from file size by the caller),
  // growing by doubling if the estimate is too small. Single-writer by contract — the LogProcessor
  // consumer task raises parser events on one thread, so no locking here.
  internal sealed class DamageFactTable : IFactTable
  {
    public const ushort NoSubtype = ushort.MaxValue;

    private DamageFact[] _facts;
    private int _factCount;
    private DeathFact[] _deaths;
    private int _deathCount;
    private IdentityEvent[] _identities;
    private int _identityCount;
    private TauntFact[] _taunts;
    private int _tauntCount;
    private EvidenceFact[] _evidences;
    private int _evidenceCount;

    private readonly List<string> _names = [];
    private readonly Dictionary<string, short> _nameMap = new(StringComparer.Ordinal);
    private readonly List<string> _subtypes = [];
    private readonly Dictionary<string, ushort> _subtypeMap = new(StringComparer.Ordinal);
    // Secondary strings for evidence facts (spell/class/channel names) - small, shared namespace.
    private readonly List<string> _auxs = [];
    private readonly Dictionary<string, short> _auxMap = new(StringComparer.OrdinalIgnoreCase);

    internal DamageFactTable(int initialCapacity = 65_536)
    {
      _facts = new DamageFact[initialCapacity];
      _deaths = new DeathFact[Math.Max(16, initialCapacity / 64)];
      _identities = new IdentityEvent[256];   // registry changes are rare (verifications), not per-line
      _taunts = new TauntFact[256];
      _evidences = new EvidenceFact[256];     // identity evidence lines: common, but not per-hit
    }

    public int FactCount => _factCount;
    public int DeathCount => _deathCount;
    public int IdentityEventCount => _identityCount;
    public int TauntCount => _tauntCount;
    public IReadOnlyList<string> InternedNames => _names;

    public ReadOnlySpan<DamageFact> Facts => _facts.AsSpan(0, _factCount);
    public ReadOnlySpan<DeathFact> Deaths => _deaths.AsSpan(0, _deathCount);
    public ReadOnlySpan<IdentityEvent> IdentityEvents => _identities.AsSpan(0, _identityCount);
    public ReadOnlySpan<TauntFact> Taunts => _taunts.AsSpan(0, _tauntCount);

    public int EvidenceCount => _evidenceCount;
    public ReadOnlySpan<EvidenceFact> Evidence => _evidences.AsSpan(0, _evidenceCount);

    // Names are interned with ordinal exactness — the same keying the current pipeline uses for
    // fight map keys (ParserUtil normalization happens upstream in the parsers).
    public short InternName(string name)
    {
      if (_nameMap.TryGetValue(name, out var idx)) return idx;
      if (_names.Count >= short.MaxValue) throw new InvalidOperationException("mirror: name table exceeded 65535 entries");
      _names.Add(name);
      idx = (short)(_names.Count - 1);
      _nameMap[name] = idx;
      return idx;
    }

    public string NameOf(short idx) => _names[idx];

    public short InternSubtype(string subtype)
    {
      if (string.IsNullOrEmpty(subtype)) return -1;
      if (_subtypeMap.TryGetValue(subtype, out var idx)) return (short)idx;
      if (_subtypes.Count >= ushort.MaxValue - 1) throw new InvalidOperationException("mirror: subtype table exceeded capacity");
      _subtypes.Add(subtype);
      idx = (ushort)(_subtypes.Count - 1);
      _subtypeMap[subtype] = idx;
      return (short)idx;
    }

    public string SubtypeOf(ushort idx) => idx == NoSubtype ? null : _subtypes[idx];

    public void AddFact(DamageFact fact)
    {
      if (_factCount == _facts.Length) Array.Resize(ref _facts, _facts.Length * 2);
      _facts[_factCount++] = fact;
    }

    public void AddDeath(DeathFact death)
    {
      if (_deathCount == _deaths.Length) Array.Resize(ref _deaths, _deaths.Length * 2);
      _deaths[_deathCount++] = death;
    }

    public void AddIdentity(IdentityEvent identity)
    {
      if (_identityCount == _identities.Length) Array.Resize(ref _identities, _identities.Length * 2);
      _identities[_identityCount++] = identity;
    }

    public void AddTaunt(TauntFact taunt)
    {
      if (_tauntCount == _taunts.Length) Array.Resize(ref _taunts, _taunts.Length * 2);
      _taunts[_tauntCount++] = taunt;
    }

    public void AddEvidence(EvidenceFact evidence)
    {
      if (_evidenceCount == _evidences.Length) Array.Resize(ref _evidences, _evidences.Length * 2);
      _evidences[_evidenceCount++] = evidence;
    }

    public short InternAux(string aux)
    {
      if (string.IsNullOrEmpty(aux)) return -1;
      if (_auxMap.TryGetValue(aux, out var idx)) return idx;
      _auxs.Add(aux);
      idx = (short)(_auxs.Count - 1);
      _auxMap[aux] = idx;
      return idx;
    }

    public string AuxOf(short idx) => idx < 0 ? null : _auxs[idx];

    // Approximate in-RAM size of the fact buffers (for the D2 revisit trigger, ~512 MB).
    public long EstimatedBytes => (long)_facts.Length * Marshal.SizeOf<DamageFact>()
      + (long)_deaths.Length * Marshal.SizeOf<DeathFact>()
      + (long)_identities.Length * Marshal.SizeOf<IdentityEvent>()
      + (long)_taunts.Length * Marshal.SizeOf<TauntFact>()
      + (long)_evidences.Length * Marshal.SizeOf<EvidenceFact>();
  }
}
