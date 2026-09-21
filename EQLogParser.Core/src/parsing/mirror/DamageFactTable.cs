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

  // Storage contract for the fact tables (D2: in-RAM now, shaped so a chunked spill can be added
  // later without touching the mirror or the deriver).
  internal interface IFactTable
  {
    int FactCount { get; }
    int DeathCount { get; }
    IReadOnlyList<string> InternedNames { get; }
    short InternName(string name);
    string NameOf(short idx);
    short InternSubtype(string subtype);
    string SubtypeOf(ushort idx);
    void AddFact(DamageFact fact);
    void AddDeath(DeathFact death);
    ReadOnlySpan<DamageFact> Facts { get; }
    ReadOnlySpan<DeathFact> Deaths { get; }
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

    private readonly List<string> _names = [];
    private readonly Dictionary<string, short> _nameMap = new(StringComparer.Ordinal);
    private readonly List<string> _subtypes = [];
    private readonly Dictionary<string, ushort> _subtypeMap = new(StringComparer.Ordinal);

    internal DamageFactTable(int initialCapacity = 65_536)
    {
      _facts = new DamageFact[initialCapacity];
      _deaths = new DeathFact[Math.Max(16, initialCapacity / 64)];
    }

    public int FactCount => _factCount;
    public int DeathCount => _deathCount;
    public IReadOnlyList<string> InternedNames => _names;

    public ReadOnlySpan<DamageFact> Facts => _facts.AsSpan(0, _factCount);
    public ReadOnlySpan<DeathFact> Deaths => _deaths.AsSpan(0, _deathCount);

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

    // Approximate in-RAM size of the fact buffers (for the D2 revisit trigger, ~512 MB).
    public long EstimatedBytes => (long)_facts.Length * Marshal.SizeOf<DamageFact>() + (long)_deaths.Length * Marshal.SizeOf<DeathFact>();
  }
}
