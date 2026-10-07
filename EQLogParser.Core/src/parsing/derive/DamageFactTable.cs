using System.Runtime.InteropServices;

namespace EQLogParser
{
  /*
   * Closed vocabulary of record types, one byte wide. Ids 1-15 are the damage words seen on
   * DamageRecord.Type; 16 and 17 are the two heal words seen on HealRecord.Type. They share one table
   * because the words come from one source (Labels / HitLabels) and a fact that could carry either word
   * must not need two vocabularies — but no damage line produces a heal word and no heal line produces a
   * damage word (the damage parser cannot emit a heal label, see HitLabelTest), so which ids a given
   * stream can hold is still closed. LabelOf() round-trips back to the Labels string so the deriver can
   * call the same StatsUtil.IsHitType / Labels checks the current pipeline uses.
   */
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
    public const byte Heal = 16;
    public const byte Hot = 17;

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
      [Labels.Riposte] = Riposte,
      [Labels.Heal] = Heal,
      [Labels.Hot] = Hot
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
      Labels.Riposte,
      Labels.Heal,
      Labels.Hot
    ];

    public static byte IdOf(string type) => Map.TryGetValue(type ?? string.Empty, out var id) ? id : Unknown;

    public static string LabelOf(byte id) => (uint)id < (uint)LabelsById.Length ? LabelsById[id] : null;

    /*
     * Mirrors StatsUtil.IsHitType for the damage words (everything is a hit type except
     * Absorb/Dodge/Invulnerable/Miss/Parry/Riposte) and says NO to the two heal words. A heal is not a
     * hit: the exclusion is defensive, because no damage line can carry a heal word today, but a caller
     * that sums IsHit facts into DamageTotal must never find itself adding healing because one more
     * stream started sharing this table.
     */
    public static bool IsHit(byte id) => id != Absorb && id != Dodge && id != Invulnerable && id != Miss
      && id != Parry && id != Riposte && id != Heal && id != Hot;

    // The two words a heal line writes. Damage facts never answer yes here; the check exists so the
    // heal side does not have to compare strings (or repeat the ids) to know what it is holding.
    public static bool IsHeal(byte id) => id == Heal || id == Hot;
  }

  /*
   * One parsed damage line, exactly as the parser fired it. Names are interned indices (short)
   * into the fact table's name table — 32 B/record, no per-record allocation after init.
   *
   * The size is not an accident and it is worth knowing which bytes are load-bearing, because this is
   * the most numerous thing the engine holds: a 5 M-fact log is 160 MB of it. Seq + TimeS (and TimeS
   * must be a long — see its comment) cannot shrink; AtkIdx/DefIdx/SubIdx are the interning that makes
   * the table cheap at all. Everything else lives in the padding around those: TypeId, Flags, ModMask.
   *
   * OverTotal used to sit here and was always 0 — "asked for more than landed" is a heal-line number,
   * and HealRecord owns it for the same reason (docs/DesignNotes.md → What a loaded raid costs in
   * memory). Deleting it made room for ModMask inside the same 32 bytes, which is what lets a derived
   * damage summary honour the six modifier filters instead of reading high (see FightSummarySource).
   */
  internal readonly struct DamageFact
  {
    public const byte FlagAttackerIsSpell = 1;
    public const byte FlagOwnerInLine = 2;   // ownership evidence ("X`s pet", "Owner: X") present on the raw line

    /*
     * Bits 4 and 8 are RETIRED, not free. They held what PlayerRegistry.IsPetOrPlayerOrMerc answered for each
     * name at the instant the event fired — a snapshot of the registry's opinion rather than of the line, which made
     * it unreproducible (a name carries it only from its own evidence second onward, so warm-up order changes it and
     * a sharded reader cannot get it right at all). Nothing read them outside retired measurement tooling; identity
     * is derived per name by the rules, and "when did the registry learn this" travels as IdentityEvent instead.
     *
     * So a NEW flag takes bit 16 or higher, never 4/8: spool files written by an older build have those bits set
     * with the old meaning, and reusing them would read last night's registry opinion as this build's new fact.
     */

    // consumer-order sequence across BOTH fact streams — preserves within-second line order, which
    // the slain queue's flush-vs-enqueue decisions depend on
    public readonly int Seq;

    // dotnet-epoch seconds (DateTime origin, year 0001) as the parser fires them — ~6.4e10 in the
    // 2020s, so long is required; int overflowed silently and corrupted every timestamp
    public readonly long TimeS;
    public readonly short AtkIdx;
    public readonly short DefIdx;
    public readonly uint Total;
    public readonly byte TypeId;
    public readonly byte Flags;

    /*
     * DamageRecord.ModifiersMask verbatim — the six modifier settings (assassinate, headshot,
     * slay-undead, ...) are read out of it by DamageValidator. Without it a derived summary cannot
     * exclude what the legacy board excludes, so its totals read HIGH whenever one of those filters is
     * off. Captured from the record rather than re-derived: this stays "what the pipeline saw".
     */
    public readonly short ModMask;
    public readonly ushort SubIdx;   // index into the subtype table (spell/modifier name); 65535 = none

    public DamageFact(int seq, long timeS, short atkIdx, short defIdx, uint total, byte typeId, byte flags, short modMask, ushort subIdx)
    {
      Seq = seq;
      TimeS = timeS;
      AtkIdx = atkIdx;
      DefIdx = defIdx;
      Total = total;
      TypeId = typeId;
      Flags = flags;
      ModMask = modMask;
      SubIdx = subIdx;
    }

    public bool AttackerIsSpell => (Flags & FlagAttackerIsSpell) != 0;
    public bool OwnerInLine => (Flags & FlagOwnerInLine) != 0;

    /*
     * The two live bits, and nothing else — asserted rather than commented, because a registry opinion smuggled back
     * onto a fact would look exactly like the deleted pair: correct on a warm single pass, wrong everywhere else.
     */
    public const byte LineDerivedFlagMask = FlagAttackerIsSpell | FlagOwnerInLine;
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

  // One identity/side evidence event captured by the engine (Phase 2 rules input). Facts stay
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
    public const byte EvCompanionCalled = 10; // "X is called to it owner." — X is the SUMMONER, not the summon
    public const byte EvCharmStart = 11;
    public const byte EvCharmEnd = 12;
    public const byte EvCast = 13;          // aux: spell name
    public const byte EvChat = 14;          // aux: channel ("guild", "group", "raid", ...)
    public const byte EvSelfFeeds = 15;     // "Glug…/Chomp… <name> takes a drink/bite from …" — vessel-agnostic
    public const byte EvEyeOwnedStrike = 16; // <name> struck the summon named after them: `X hits Eye of X` (R19)
    public const byte EvGuildmate = 17;      // "Your guildmate <name> has completed …" - the client's own guild list speaking (R22)
    public const byte EvLooter = 18;         // "--<name> has looted a … / wins roll …" - a person loots, a mob does not (R23)

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
  //
  // The fact keeps the taunter and the outcome words, not just the npc: FightSummarySource rebuilds
  // fight.TauntBlocks from these for the taunt board, and Player / Success / IsImproved are exactly the
  // three fields TauntStatsViewer counts. The deriver still needs only the npc and the second.
  internal readonly struct TauntFact
  {
    public const byte TauntSuccess = 1;
    public const byte TauntImproved = 2;

    public readonly int Seq;
    public readonly long TimeS;       // dotnet-epoch seconds — see DeathFact.TimeS
    public readonly short NpcIdx;
    public readonly short AttackerIdx; // the taunter; -1 when the line carried no name for them
    public readonly byte Flags;        // TauntSuccess, TauntImproved

    public TauntFact(int seq, long timeS, short npcIdx, short attackerIdx = -1, byte flags = 0)
    {
      Seq = seq;
      TimeS = timeS;
      NpcIdx = npcIdx;
      AttackerIdx = attackerIdx;
      Flags = flags;
    }
  }

  // Storage contract for the fact tables (D2: in-RAM now, shaped so a chunked spill can be added
  // later without touching the engine or the deriver).
  internal interface IFactTable
  {
    int FactCount { get; }
    int DeathCount { get; }
    int IdentityEventCount { get; }
    int TauntCount { get; }
    IReadOnlyList<string> InternedNames { get; }
    short InternName(string name);

    /// <summary>Lookup without interning: the name's index, or -1 when this capture never named it.</summary>
    short NameIndexOf(string name);
    string NameOf(short idx);
    ushort InternSubtype(string subtype);
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

    /// <summary>Allocated row-array bytes beyond what is stored. See CompactToCount.</summary>
    long SlackBytes { get; }

    /// <summary>Shrink every row array to its rows, returning the bytes released. Mechanics only - WHEN to ask is the
    /// caller's policy (CombatCapture.CompactRows).</summary>
    long CompactToCount();
  }

  /*
   * The growth/collapse arithmetic shared by both row tables (DamageFactTable, HealFactTable).
   *
   * Both grow by doubling and both are asked to shrink to their rows when a load finishes, so the two rules that are easy
   * to get wrong live here once: the FLOOR (an empty array cannot grow - doubling 0 is 0 and the next Add would index past
   * the end) and the no-op guard (a table already at its count releases nothing, which is what makes asking again cheap).
   */
  internal static class RowArrays
  {
    private const int MinCapacity = 16;

    /// <summary>Bytes a row array of this element type holds beyond the rows it stores.</summary>
    internal static long SlackOf<T>(int capacity, int count) where T : struct
      => (long)(capacity - count) * Marshal.SizeOf<T>();

    /// <summary>Shrink to `count` rows (never below the floor), returning the bytes released. 0 when there is nothing to give.</summary>
    internal static long TrimTo<T>(ref T[] rows, int count) where T : struct
    {
      var target = Math.Max(count, MinCapacity);
      if (rows.Length <= target) return 0L;
      var freed = (long)(rows.Length - target) * Marshal.SizeOf<T>();
      Array.Resize(ref rows, target);
      return freed;
    }
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
    // Ignore-case: see InternName. Same comparer rule as EntityTimeline's identity/affiliation keys.
    private readonly Dictionary<string, short> _nameMap = new(StringComparer.OrdinalIgnoreCase);
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

    /*
     * One entity, one id, whatever spelling arrived — and one DISPLAY form derived from the name itself.
     *
     * The keys are ignore-case for the reason the timeline and `PlayerRegistry` give (EQ itself cannot hold two
     * entities whose names differ only by letter case), and because the two halves of the pipeline spell things
     * differently on purpose: `ParserUtil.UpdateAttacker/UpdateDefender/UpdateSlain` finish with
     * `CapitalizeFirst`, while the evidence lines never pass through them (`a bone walker has been charmed.`).
     * With ordinal keys that was two ids for one mob, which is worse than a cosmetic split: per-name evidence,
     * per-name rollups and fight rows each saw half the entity, and 10.5 % of the table was spelling.
     *
     * The stored string is `CapitalizeFirst` of whatever came in — not the first spelling that got there. That
     * matters because the id's string IS the row's name: before this, whether the fight list read "a bone walker"
     * or "A Bone Walker" depended on whether a charm line or a damage line touched the name first, and no amount
     * of sorting fixes a decision that was made by arrival order. Capitalising the first letter is the same
     * finishing touch the parsers already apply to every damage line, so nothing about EQ's own names changes
     * beyond that letter; inner capitals (`Tik`Tick`) come through exactly as written.
     *
     * Subtype and label tables deliberately stay ordinal: those strings are vocabulary words the user's
     * settings and `Labels` constants compare against.
     */
    public short InternName(string name)
    {
      if (_nameMap.TryGetValue(name, out var idx)) return idx;
      if (_names.Count >= short.MaxValue) throw new InvalidOperationException("capture: name table exceeded 65535 entries");
      _names.Add(TextUtils.CapitalizeFirst(name));
      idx = (short)(_names.Count - 1);
      _nameMap[name] = idx;
      return idx;
    }

    public string NameOf(short idx) => _names[idx];

    /*
     * Look a name up WITHOUT adding it (same OrdinalIgnoreCase key as InternName). Rules that want to know whether a name took
     * part in the capture must not intern it: interning from a read path would grow the very pool they are asking about, and the
     * id's string is what rows display.
     */
    public short NameIndexOf(string name) => _nameMap.TryGetValue(name, out var idx) ? idx : (short)-1;

    /*
     * The id's type is part of the contract, and it is the SAME one the fact carries: subIdx is a ushort with NoSubtype
     * reserved as its top value, so an answer wider than that would alias (a signed short wraps at 32768 and the old call
     * site clamped the wrap to 0 - the FIRST subtype, which is how a capture's 32,769th distinct verb would have been read
     * as its first) and an answer narrower than it wastes half the space. Empty answers NoSubtype itself: that is what the
     * one call site used to spend a ternary on, and nothing may ever intern the sentinel.
     */
    public ushort InternSubtype(string subtype)
    {
      if (string.IsNullOrEmpty(subtype)) return NoSubtype;
      if (_subtypeMap.TryGetValue(subtype, out var idx)) return idx;
      /*
       * Stops rather than clamps or aliases, and NAMES the offender: CombatCapture.AddDamage swallows this so one poison line
       * cannot stop the capture, which leaves LogProcessor's consumer fault as the only surface - and "capacity" without the verb
       * that hit it is a message nobody can act on. A real capture uses ~1,100 of these ids (measured), so arriving here means
       * something is interning junk, not that the session was long.
       */
      if (_subtypes.Count >= ushort.MaxValue - 1)
        throw new InvalidOperationException($"capture: subtype table exceeded its {ushort.MaxValue - 1} entries at '{subtype}'");
      _subtypes.Add(subtype);
      idx = (ushort)(_subtypes.Count - 1);
      _subtypeMap[subtype] = idx;
      return idx;
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

    /*
     * short on purpose: both EvidenceFact fields that can carry this id are shorts, and a read must not silently wrap. The
     * capacity check is what makes that true - without it the 32,769th distinct aux would intern at -32768, AuxOf would hand
     * back null for a name the capture actually used, and a later alias would hand back somebody else's.
     */
    public short InternAux(string aux)
    {
      if (string.IsNullOrEmpty(aux)) return -1;
      if (_auxMap.TryGetValue(aux, out var idx)) return idx;
      // Stops rather than wrapping to a negative id (which AuxOf would read as "no aux" or as another name's), and names the
      // string that got here - see InternSubtype for why the message has to carry the offender.
      if (_auxs.Count >= short.MaxValue)
        throw new InvalidOperationException($"capture: aux table exceeded its {short.MaxValue - 1} entries at '{aux}'");
      _auxs.Add(aux);
      idx = (short)(_auxs.Count - 1);
      _auxMap[aux] = idx;
      return idx;
    }

    public string AuxOf(short idx) => idx < 0 ? null : _auxs[idx];

    /*
     * Bytes of row array that hold nothing. Growth doubles (AddFact and its siblings), so a capture that stopped growing
     * holds between 0 and 100 % more slots than rows: measured on a 467 MB capture whose 2,285,746 damage facts sat in a
     * 3,200,000-slot array, the damage table alone kept 29 MB of address space for nothing, and with the heal table's
     * slack beside it ~47 MB of the 397 MB a loaded session holds was empty. CompactToCount hands it back; CombatCapture
     * decides when (a trim per fact would turn doubling's amortized copy into a copy per fact).
     */
    public long SlackBytes => RowArrays.SlackOf<DamageFact>(_facts.Length, _factCount)
                              + RowArrays.SlackOf<DeathFact>(_deaths.Length, _deathCount)
                              + RowArrays.SlackOf<IdentityEvent>(_identities.Length, _identityCount)
                              + RowArrays.SlackOf<TauntFact>(_taunts.Length, _tauntCount)
                              + RowArrays.SlackOf<EvidenceFact>(_evidences.Length, _evidenceCount);

    /*
     * Reallocate every row array to exactly the rows it holds and return the bytes released.
     *
     * Safe against a reader that took Facts (or any other span) beforehand: the new array is the same rows in the same
     * order at the same indices, so a stale span still reads the same facts, and every ordinal FightFactIndex stores
     * still names the same row. That is also why this is a shrink rather than a repack - nothing here renumbers.
     */
    public long CompactToCount()
    {
      var freed = RowArrays.TrimTo(ref _facts, _factCount);
      freed += RowArrays.TrimTo(ref _deaths, _deathCount);
      freed += RowArrays.TrimTo(ref _identities, _identityCount);
      freed += RowArrays.TrimTo(ref _taunts, _tauntCount);
      freed += RowArrays.TrimTo(ref _evidences, _evidenceCount);
      return freed;
    }

    // Approximate in-RAM size of the fact buffers (for the D2 revisit trigger, ~512 MB).
    public long EstimatedBytes => (long)_facts.Length * Marshal.SizeOf<DamageFact>()
      + (long)_deaths.Length * Marshal.SizeOf<DeathFact>()
      + (long)_identities.Length * Marshal.SizeOf<IdentityEvent>()
      + (long)_taunts.Length * Marshal.SizeOf<TauntFact>()
      + (long)_evidences.Length * Marshal.SizeOf<EvidenceFact>();
  }
}
