using log4net;
using System.Reflection;
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
   * One parsed damage line, exactly as the parser fired it. Names are interned int ids; the clock is a rebased int
   * read back as epoch seconds (FactTime). Size is asserted by FactTimeTest, with the storage decisions recorded in
   * docs/DesignNotes.md → "What a loaded raid costs in memory".
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
     * it unreproducible (a name carries it only from its own evidence second onward, so warm-up order changes it —
     * names flipped side mid-file on a real capture; the census is in docs/DesignNotes). Nothing read them outside
     * retired measurement tooling; identity
     * is derived per name by the rules, and "when did the registry learn this" travels as IdentityEvent instead.
     *
     * So a NEW flag takes bit 16 or higher, never 4/8: spool files written by an older build have those bits set
     * with the old meaning, and reusing them would read last night's registry opinion as this build's new fact.
     */

    // consumer-order sequence across BOTH fact streams — preserves within-second line order, which
    // the slain queue's flush-vs-enqueue decisions depend on
    public readonly int Seq;

    /*
     * The row's second, stored as seconds since 2000-01-01 (see FactTime) and READ BACK as the dotnet-epoch second it
     * always was. The raw long was 6.3e10 in the 2020s and an int cast of it overflowed silently once, which is what the
     * old comment on this line recorded — but the width that needs a long is the RAW epoch, not the value: rebased, the
     * whole range 1931-2068 fits in four bytes, and this row is the most numerous thing the process keeps.
     */
    private readonly int _timeS;

    /// <summary>dotnet-epoch seconds, exactly as every consumer has always read them. See FactTime for the storage.</summary>
    public long TimeS => FactTime.ToEpochSeconds(_timeS);
    // int ids: see InternName. These two are the whole reason a damage row is 28 bytes and not 24.
    public readonly int AtkIdx;
    public readonly int DefIdx;

    /*
     * The hit's amount, 48 bits wide, split across the four-byte slot this row always had and the two padding bytes that
     * sat above the modifier mask: an ordinary hit keeps its low half and a zero high word, and the row is still 28 bytes.
     * Written through the constructor, which refuses anything the domain cannot carry, and read back as one number.
     * Measured ceilings and why 48: docs/DesignNotes.md → "Amount widths measured across 44 million hits".
     */
    private readonly uint _totalLow;
    private readonly ushort _totalHigh;

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

    public DamageFact(int seq, long timeS, int atkIdx, int defIdx, long total, byte typeId, byte flags, short modMask, ushort subIdx)
    {
      if (!TextUtils.InAmountRange(total))
        throw new ArgumentOutOfRangeException(nameof(total), total, "A damage amount is a magnitude within 48 bits.");

      Seq = seq;
      _timeS = FactTime.FromEpochSeconds(timeS);
      AtkIdx = atkIdx;
      DefIdx = defIdx;
      _totalLow = (uint)total;
      _totalHigh = (ushort)(total >> 32);
      TypeId = typeId;
      Flags = flags;
      ModMask = modMask;
      SubIdx = subIdx;
    }

    /// <summary>The amount the line printed, both halves of it, up to <see cref="TextUtils.MaxAmount"/>.</summary>
    public long Total => (long)_totalLow | ((long)_totalHigh << 32);

    public bool AttackerIsSpell => (Flags & FlagAttackerIsSpell) != 0;
    public bool OwnerInLine => (Flags & FlagOwnerInLine) != 0;

    /*
     * The two live bits, and nothing else — asserted rather than commented, because a registry opinion smuggled back
     * onto a fact would look exactly like the deleted pair: correct on a warm single pass, wrong everywhere else.
     */
    public const byte LineDerivedFlagMask = FlagAttackerIsSpell | FlagOwnerInLine;
  }

  // One "X was slain by Y!" / "X died." line.

  /*
   * Sequential read of an ordinal range as chunk slices: `foreach (var slice in table.FactsIn(from, to))`. A ref struct on purpose - it
   * yields ReadOnlySpan over a live store, and it must not be able to outlive the frame that reads it. No allocation, no boxing, no per-row
   * delegate; the only cost versus the old flat loop is one outer loop over at most ceil(range / 65536) slices.
   */
  internal ref struct DamageFactRange(DamageFactTable table, int from, int to)
  {
    private readonly DamageFactTable _table = table;
    private readonly int _from = from;
    private readonly int _to = Math.Max(from, to);

    public Enumerator GetEnumerator() => new(_table, _from, _to);

    // Not `readonly`: MoveNext advances the cursor in place, which is the whole point of a zero-alloc slice walk.
    public ref struct Enumerator(DamageFactTable table, int from, int to)
    {
      private readonly DamageFactTable _table = table;
      private readonly int _to = to;
      private int _next = from;
      private int _start = from;
      private int _length;

      /*
       * RowsOf rather than indexing the reference array: a chunk that was put away cold decodes on first touch, so a walk over history
       * pays ~3 ms per chunk and every later walk reads rows. A slice never spans two chunks, so at most one decode happens per step.
       */
      public ReadOnlySpan<DamageFact> Current => _table!.RowsOfChunk(_start >> DamageFactTable.FactChunkShift)
        .AsSpan(_start & DamageFactTable.FactChunkMask, _length);

      public bool MoveNext()
      {
        if (_next >= _to) return false;

        _start = _next;
        _length = Math.Min(DamageFactTable.FactChunkSize - (_start & DamageFactTable.FactChunkMask), _to - _start);
        _next += _length;
        return true;
      }
    }
  }


  /*
   * Ordinal view over the chunked store — `Length` and `this[ordinal]`, which is all the cursor loops ever asked of the old flat span.
   * It is a CLASS on purpose: ReadOnlySpan cannot be an interface member, and the classification stages reach the rows through IFactTable.
   * A row read costs one bounds check plus the shift/mask split; the hot whole-capture walks skip it by iterating FactsIn's slices instead.
   */
  internal sealed class DamageFactReader(DamageFactTable table) : IEnumerable<DamageFact>
  {
    public int Length => table.FactCount;

    public ref readonly DamageFact this[int ordinal] => ref table.RefAt(ordinal);

    /// <summary>Pattern-based enumeration for `foreach` over the whole capture. A struct cursor, so a loop costs no allocation; a row read
    /// is a 24-byte copy of one row, which is what every non-hot consumer (tests, reports, one-off scans) already paid on the flat array.</summary>
    public RowEnumerator GetEnumerator() => new(table);

    IEnumerator<DamageFact> IEnumerable<DamageFact>.GetEnumerator()
    {
      for (var i = 0; i < table.FactCount; i++) yield return table.At(i);
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => ((IEnumerable<DamageFact>)this).GetEnumerator();

    public struct RowEnumerator(DamageFactTable table)
    {
      private readonly DamageFactTable _table = table;
      private int _index = -1;

      public DamageFact Current => _table.At(_index);

      public bool MoveNext() => ++_index < _table.FactCount;
    }
  }

  internal readonly struct DeathFact
  {
    public readonly int Seq;
    public readonly long TimeS;   // dotnet-epoch seconds — see DamageFact.TimeS
    public readonly int KilledIdx;
    public readonly int KillerIdx;   // -1 when the line named no killer

    public DeathFact(int seq, long timeS, int killedIdx, int killerIdx)
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
    public readonly int NameIdx;
    public readonly byte Kind;

    public IdentityEvent(int seq, long timeS, int nameIdx, byte kind)
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
    public const byte EvPetSlotHit = 19;     // a spell the DB aims at a pet landed on <name>: `Wanabe hit Fred for … by Elemental Conversion VI.` (R24)

    /*
     * ``Mgsk says, 'My leader is Xxuro.'`` — a summon naming its own master, about ITSELF: aux carries the owner text.
     * Until now this sentence was an identity door only the WPF project could open: ChatDB wrote the pair straight into
     * PlayerRegistry, whose entire route into the rule book is RegistrySeed at strength 8 — so a `Targeted (NPC)` frame
     * (which the client also prints for every mercenary and every pet) outvoted the pet's own confession. Measured on
     * Incogitable: ``Useless`` reads Npc with this line in the log; on Kizant-2026 ``Mgsk`` read Unplaced across 726 group
     * heal lines while naming Xxuro as its leader. The confession is what a line SAYS, so it belongs here and R5-leader
     * decides what it means (docs/DesignNotes.md → "A summon's own sentence about its master").
     */
    public const byte EvPetLeader = 20;

    public readonly int Seq;
    public readonly long TimeS;
    public readonly int NameIdx;
    public readonly byte Kind;
    public readonly int AuxIdx;   // -1 when the evidence carries no secondary string

    public EvidenceFact(int seq, long timeS, int nameIdx, byte kind, int auxIdx = -1)
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
    public readonly int NpcIdx;
    public readonly int AttackerIdx; // the taunter; -1 when the line carried no name for them
    public readonly byte Flags;        // TauntSuccess, TauntImproved

    public TauntFact(int seq, long timeS, int npcIdx, int attackerIdx = -1, byte flags = 0)
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
    int InternName(string name);

    /// <summary>Lookup without interning: the name's index, or -1 when this capture never named it.</summary>
    int NameIndexOf(string name);
    string NameOf(int idx);
    ushort InternSubtype(string subtype);
    string SubtypeOf(ushort idx);
    void AddFact(DamageFact fact);
    void AddDeath(DeathFact death);
    void AddIdentity(IdentityEvent identity);
    void AddTaunt(TauntFact taunt);
    void AddEvidence(EvidenceFact evidence);
    int InternAux(string aux);
    string AuxOf(int idx);
    DamageFactReader Facts { get; }
    ReadOnlySpan<DeathFact> Deaths { get; }
    ReadOnlySpan<IdentityEvent> IdentityEvents { get; }
    ReadOnlySpan<TauntFact> Taunts { get; }
    ReadOnlySpan<EvidenceFact> Evidence { get; }

    /// <summary>Allocated row-array bytes beyond what is stored. See CompactToCount.</summary>
    long SlackBytes { get; }

    /// <summary>Row-array bytes as allocated (slots, not rows) — kept beside SlackBytes so "the capture is big"
    /// and "the capture reserved more than it wrote" stay two separate questions.</summary>
    long EstimatedBytes { get; }

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

    /*
     * Row storage: fixed-size power-of-two CHUNKS, addressed by shift and mask.
     *
     * Why this shape and not a growing array: the rows are the biggest thing the application holds (28 bytes apiece, millions of them), and
     * a single resizable buffer paid for that three ways. Every doubling copied the whole capture (tens of millions of bytes on the parse
     * thread, which is already the bottleneck), it briefly held the old and new buffers at once (a >100 MB transient at raid scale), and it
     * left up to 100 % of the rows' worth of reserved-but-empty slots behind until a compaction pass reclaimed them. Chunks end all three:
     * a chunk is allocated when the first row needs it, nothing is ever copied, and the slack is at most one chunk instead of one whole capture.
     *
     * The currency stays exactly what it was. Ordinals are still "the nth fact of the capture", which is what FightFactIndex stores and what
     * the board blocks are runs of: an ordinal splits into (chunk = i >> Shift, slot = i & Mask) with no table lookup, no search, no offset list.
     * That is why the addressing door (RefAt / FactsIn) exists in front of this field rather than readers holding the buffer.
     */
    internal const int FactChunkShift = 16;                       // 65,536 rows ~= 1.5 MiB per chunk
    internal const int FactChunkSize = 1 << FactChunkShift;
    internal const int FactChunkMask = FactChunkSize - 1;

    private readonly ColdChunks<DamageFact> _rows;
    private DamageFact[] _tail;
    private int _factCount;

    // ColdChunks owns publication, growth and bounded decoded retention for both streams.
    // See docs/DesignNotes.md → "Rows go cold, and what a chunk costs to remember".
    // Same per-class logger convention as every other Core store: a cold-chunk problem has to be findable in eqlogparser.log.
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

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
    private readonly Dictionary<string, int> _nameMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _subtypes = [];
    private readonly Dictionary<string, ushort> _subtypeMap = new(StringComparer.Ordinal);
    // Secondary strings for evidence facts (spell/class/channel names) - small, shared namespace.
    private readonly List<string> _auxs = [];
    private readonly Dictionary<string, int> _auxMap = new(StringComparer.OrdinalIgnoreCase);

    internal DamageFactTable(int initialCapacity = 65_536)
    {
      // The capacity hint becomes a chunk count: preallocating ceil(hint / chunk) chunks costs at most one chunk more than the old
      // exact-size buffer, and a hint of 0 (follow-from-end opens: startup auto-monitor, Clear All) allocates NOTHING up front, which is
      // the same law FactCapacity.HintForOpen exists to enforce, now honoured by the store instead of only by the caller.
      _rows = new ColdChunks<DamageFact>(FactChunkSize, initialCapacity, EncodeChunk,
        static blob => DamageFactChunkCodec.Decode(blob, FactChunkSize),
        static ex => Log.Warn("cold chunk: damage encoding failed; leaving rows available", ex));
      _deaths = new DeathFact[Math.Max(16, initialCapacity / 64)];
      _identities = new IdentityEvent[256];   // registry changes are rare (verifications), not per-line
      _taunts = new TauntFact[256];
      _evidences = new EvidenceFact[256];     // identity evidence lines: common, but not per-hit
    }

    public int FactCount => Volatile.Read(ref _factCount);
    public int DeathCount => _deathCount;
    public int IdentityEventCount => _identityCount;
    public int TauntCount => _tauntCount;
    public IReadOnlyList<string> InternedNames => _names;

    /// <summary>The whole capture, addressed by ordinal. Same shape the flat span offered (Length + `this[ordinal]`), because that is what
    /// every cursor loop already speaks; see DamageFactReader.</summary>
    public DamageFactReader Facts => new(this);

    /// <summary>Sequential access over [from, to) as the CHUNK SLICES covering it: zero allocation, and the inner loop runs over a
    /// contiguous span exactly as it did on the flat buffer. The hot walks use this instead of `Facts` for that reason.</summary>
    public DamageFactRange FactsIn(int from, int to) => new(this, Math.Max(0, from), Math.Min(to, _factCount));

    /// <summary>Bytes currently held as encoded cold blobs. Cold rows are counted here rather than as chunk bytes.</summary>
    internal long ColdBlobBytes => _rows.BlobBytes;

    /// <summary>Completed chunks with compressed backing, including any currently decoded in the read cache.</summary>
    internal int ColdChunkCount => _rows.BlobCount;

    /// <summary>The row array for a chunk, decoding it first if it went cold. Never null for a chunk the capture reached.</summary>
    internal DamageFact[] RowsOfChunk(int ci) => _rows.Read(ci);

    /*
     * Put one completed chunk away. Runs on a pool thread and is never asked about a chunk the append path could still write to: it is
     * called with the array whose successor was just bought, so rows.Length is a full chunk by construction.
     *
     * The blob is published BEFORE the slot is cleared — the invariant readers rely on is "a null slot always has a blob behind it" — and
     * what it encodes is verified by decoding it back. Verification costs ~3 ms per chunk against a ~70 ms encode, off the parse thread,
     * and buys the one thing worth buying here: an encoding bug can degrade to "this chunk stayed raw" with a line in the log, never to a
     * board that quietly reads somebody else's row.
     */
    private static byte[] EncodeChunk(DamageFact[] rows)
    {
      var blob = DamageFactChunkCodec.Encode(rows, rows.Length);
      var check = DamageFactChunkCodec.Decode(blob, rows.Length);
      for (var i = 0; i < rows.Length; i++)
        if (!SameRow(rows[i], check[i])) throw new InvalidOperationException($"damage codec differs at row {i}");
      return blob;
    }

    /// <summary>Join the append encoder after writing stops; returns the completed backing count, not work raced for.</summary>
    internal int ColdifyNow() => _rows.Settle();

    internal IDisposable RetainDecoded() => _rows.RetainDecoded();

    private static bool SameRow(in DamageFact a, in DamageFact b) =>
      a.Seq == b.Seq && a.TimeS == b.TimeS && a.AtkIdx == b.AtkIdx && a.DefIdx == b.DefIdx && a.Total == b.Total
      && a.TypeId == b.TypeId && a.Flags == b.Flags && a.ModMask == b.ModMask && a.SubIdx == b.SubIdx;

    /// <summary>Chunks allocated (not chunks used) - the honest denominator for EstimatedBytes under chunked storage.</summary>
    internal int FactChunksAllocated => _rows.RawCount;

    /*
     * THE ordinal door. One row, addressed by the ordinal everything else already speaks: `FightFactIndex` stores ordinals, the board
     * blocks are ordinal runs, and a materialized selection is a list of these. Every read that wants ONE row should come through here
     * rather than indexing a span, because this is where the flat buffer stops being one allocation: the day rows move into chunks, the
     * only change is inside this method (chunk = ordinal >> shift, slot = ordinal & mask), and no caller, no golden board and no
     * `FightFactIndex` ordinal moves. `Facts` stays for the whole-range walks that want a span; it is the last thing that can be answered
     * only while storage is contiguous, which is exactly why the door exists before any chunking does.
     */
    public DamageFact At(int ordinal) => RefAt(ordinal);

    /// <summary>By-reference form of the door, for readers that hold a row briefly (`ref readonly` at the call site costs no 24-byte
    /// copy). Valid in a chunked world too: a chunk is an array, so the reference points into it exactly as it does today.</summary>
    public ref readonly DamageFact RefAt(int ordinal)
    {
      if ((uint)ordinal >= (uint)FactCount) return ref MissedRow(ordinal);
      var chunk = RowsOfChunk(ordinal >> FactChunkShift);
      return ref chunk[ordinal & FactChunkMask];
    }

    private static readonly DamageFact ZeroRow;
    private int _refOutOfRangeReported;

    /*
     * An ordinal at or past the end is not a condition to handle: `FightFactIndex` stores ordinals and the board blocks are ordinal
     * runs, so this means a caller's index and this table's rows disagree — a bug. A zero row is still what gets handed back, because
     * throwing inside a board build lands in MainWindow's swallowing catch, which logs once and leaves every grid showing its LAST
     * answer: a frozen pane is harder to read than a wrong one (see the standing law about the board build's catch). What it must not
     * be is silent, so this says it once per capture — ordinal and count, which is the whole diagnosis — with the counter keeping a bad
     * walk from writing a line per record.
     */
    private ref readonly DamageFact MissedRow(int ordinal)
    {
      if (Interlocked.Exchange(ref _refOutOfRangeReported, 1) == 0)
        Log.Warn($"fact ordinal {ordinal} is past the end of this capture ({FactCount:N0} row(s)); a zero row was read in its place");

      return ref ZeroRow;
    }
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
        /*
         * The name id is an `int`, and that is a paid decision: it was a short (32,767 usable names, with an error message claiming 65,535), the pool
         * legitimately passes that in a long capture, and the throw fired on the INGEST path where the per-line catch swallows it — so a capture went
         * quietly blind instead of failing. Cost: a 28-byte row and zigzag varint cold ids (DamageFactChunkCodec). `ushort` is not enough and clamping
         * invents entities. There is deliberately no capacity check here: an int space is unreachable. docs/DesignNotes.md → "A fact row is 24 bytes".
         */
    public int InternName(string name)
    {
      if (_nameMap.TryGetValue(name, out var idx)) return idx;
      _names.Add(TextUtils.CapitalizeFirst(name));
      idx = _names.Count - 1;
      _nameMap[name] = idx;
      return idx;
    }

    public string NameOf(int idx) => _names[idx];

    /*
     * Look a name up WITHOUT adding it (same OrdinalIgnoreCase key as InternName). Rules that want to know whether a name took
     * part in the capture must not intern it: interning from a read path would grow the very pool they are asking about, and the
     * id's string is what rows display.
     */
    public int NameIndexOf(string name) => _nameMap.TryGetValue(name, out var idx) ? idx : -1;

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
      var ordinal = _factCount;
      var ci = ordinal >> FactChunkShift;

      if ((ordinal & FactChunkMask) == 0) _tail = _rows.NewTail(ci);
      _tail[ordinal & FactChunkMask] = fact;
      Volatile.Write(ref _factCount, ordinal + 1);
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
     * int, same decision as InternName (see the comment there — 16-bit ids threw on the ingest path at 32,767, where a per-line
     * catch swallowed it and the capture silently lost lines). -1 means "this evidence carries no secondary string", so the
     * table can never intern the sentinel; there is no ceiling check because an int id space is not reachable.
     */
    public int InternAux(string aux)
    {
      if (string.IsNullOrEmpty(aux)) return -1;
      if (_auxMap.TryGetValue(aux, out var idx)) return idx;
      _auxs.Add(aux);
      idx = _auxs.Count - 1;
      _auxMap[aux] = idx;
      return idx;
    }

    public string AuxOf(int idx) => idx < 0 ? null : _auxs[idx];

    /*
     * Bytes of row array that hold nothing — THE QUEUE SLACK ONLY.
     *
     * Damage facts contribute none: rows live in fixed chunks allocated on demand, so the most the row store can hold without writing is
     * the tail of the newest chunk (~1.5 MB) rather than up to a whole doubling of the capture. Before chunks, the growth doubled
     * (AddFact and its siblings) and a capture that stopped growing held between 0 and 100 % more slots than rows — measured on a 467 MB
     * capture whose 2,285,746 damage facts sat in a 3,200,000-slot array: 29 MB in the damage table, ~47 MB beside it with the heal table's
     * share, out of the 397 MB a loaded session held. That went out by construction, not by a trim pass.
     *
     * What is left here is real and still doubles: the deaths, identity-event, taunt and evidence queues, and CompactToCount hands it back
     * when CombatCapture asks (a trim per fact would turn doubling's amortized copy into a copy per fact). The evidence queue is the one
     * that scales with the log (a line can emit evidence without emitting a fact), so this number is NOT pinned at zero — and note the
     * once-per-doubling watermark in CombatCapture.CompactRows keys on the DAMAGE fact count, which a quiet capture whose evidence queue
     * alone keeps growing never trips.
     */
    public long SlackBytes => RowArrays.SlackOf<DeathFact>(_deaths.Length, _deathCount)
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
      // Chunk rows: drop whole chunks the capture never reached (possible when a capacity hint preallocated them), and nothing else -
      // rows are never renumbered, so FightFactIndex ordinals and any span taken beforehand keep naming the same fact.
      var freed = 0L;
      freed += RowArrays.TrimTo(ref _deaths, _deathCount);
      freed += RowArrays.TrimTo(ref _identities, _identityCount);
      freed += RowArrays.TrimTo(ref _taunts, _tauntCount);
      freed += RowArrays.TrimTo(ref _evidences, _evidenceCount);
      return freed;
    }


    // Approximate in-RAM size of the fact buffers (for the D2 revisit trigger, ~512 MB).
    /*
     * A cold chunk counts as its blob, not as the row array it replaced — this number feeds the D2 revisit trigger, so a chunk that got
     * 5.8x smaller has to show up smaller or the trigger decides this session needs spilling when it already does not.
     */
    public long EstimatedBytes => (long)FactChunksAllocated * FactChunkSize * Marshal.SizeOf<DamageFact>()
      + ColdBlobBytes
      + (long)_deaths.Length * Marshal.SizeOf<DeathFact>()
      + (long)_identities.Length * Marshal.SizeOf<IdentityEvent>()
      + (long)_taunts.Length * Marshal.SizeOf<TauntFact>()
      + (long)_evidences.Length * Marshal.SizeOf<EvidenceFact>();
  }
}
