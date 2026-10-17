#nullable enable annotations   // the nullable MARKS below; the project itself builds with Nullable disabled (docs/CodingStandards.md)

using log4net;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EQLogParser
{
  /*
   * One parsed heal line, exactly as HealingLineParser fired it — the heal twin of DamageFact, in a table
   * of its own.
   *
   * Separate table, deliberately, and for two reasons worth having in writing before this gets "unified".
   * (1) The two streams have different shapes: a heal carries what the line ASKED for as well as what
   * landed (OverTotal - Total is the overheal every healing tab prints), and damage does not; DamageFact
   * carried an always-zero OverTotal for years and nothing could read it. (2) A heal is not an exchange
   * with a boss — healing done during a fight belongs to the fight's TIME WINDOW, which is how the healing
   * board has always worked (HealingStatsBuilder filters stored heals to the selected fights' ranges),
   * while damage is keyed by the entity it was aimed at. Bolting heals onto DamageFact would mean either a
   * column that is null for three quarters of the rows or a direction flag on a struct whose whole job is
   * "who hit whom". If the two streams ever turn out to want the same treatment, this file and its tap are
   * one deletion away from folding into DamageFactTable; the projection is where that decision will show up.
   *
   * 32 B/fact (see the size test). Heal lines run at roughly a quarter of the damage rate on the logs
   * measured here, so on a 5 M-damage-fact log this table costs ~40 MB against damage's 160 MB — and it is
   * counted in the same D2 memory estimate (EstimatedBytes).
   */
  internal readonly struct HealFact
  {
    // No spell text on the line. Distinct from an empty string, which the interner refuses anyway.
    public const ushort NoSpell = ushort.MaxValue;

    /*
     * The healer's own name carries its owner ("Bulgar`s pet", "Bulgar`s warder"). Flag rather than a
     * resolved owner: the engine stores what the line says and rules decide what it means (D1). The
     * explicit "Owner:" annotation has no equivalent here because HealProcessedEvent carries no raw line —
     * extending that event is the documented route if heal lines turn out to need it, not a second parse.
     */
    public const byte FlagOwnerInLine = 1;

    /*
     * Bits 2 and 4 are RETIRED, not free: they held PlayerRegistry's answer for healer and healed at the instant the
     * line fired — a snapshot of an opinion rather than of the sentence, which is why nothing could reproduce it and
     * nothing outside retired tooling read it. See DamageFact's same note: identity comes from the rules over captured
     * evidence, and "when did the registry learn this" travels as IdentityEvent. A NEW flag here starts at bit 8 so an
     * older spool file's old bits cannot be misread as it.
     */

    // The live bits, asserted by FactsCarryOnlyWhatTheirOwnLinesSay: no registry opinion rides on a fact.
    public const byte LineDerivedFlagMask = FlagOwnerInLine;

    /*
     * FIELD ORDER IS THE LAYOUT: widest first, then the four-byte fields, then two-byte, then bytes. The same ten values
     * in the order this struct once shipped in were 40 bytes (an int before the lone long opened a four-byte pad and eight
     * bytes of tail), the clock's rebasing took that to 28 (FactTime), and packing the label into the flags byte plus the
     * modifier mask into one byte landed it on 24; widening the two name ids subsequently took it to 28, matching
     * damage. The CLR may reorder fields, so the layout is a fact about this build rather than a promise, and
     * HealFactCaptureTest asserting the measured size is what makes a change in padding visible.
     */

    /*
     * Seconds since 2000-01-01 (FactTime), read back as the dotnet-epoch second it always was — the same rebasing that
     * took DamageFact from 32 bytes to 24. Field order still matters (widest first): with the clock narrowed to four
     * bytes this struct has no eight-byte field left, which is why it lands on a multiple of four rather than eight.
     */
    private readonly int _timeS;

    /// <summary>dotnet-epoch seconds, exactly as before the storage shrank. See FactTime.</summary>
    public long TimeS => FactTime.ToEpochSeconds(_timeS);

    /*
     * What landed, and what the line asked for: "for 3461 (60581)" is Total 3461, OverTotal 60581, and the
     * overheal every healing tab prints is OverTotal - Total.
     *
     * Stored VERBATIM off the record, including its one quirk: a line with no parenthesised amount leaves the
     * parser's overHeal at 0, which means "the line never said", not "nothing was asked". Normalising that to
     * Total at capture looks tidier and silently changes a stat — StatsUtil.UpdateHealStats computes
     * MaxPotentialHit as Total + OverTotal, so an OverTotal of Total instead of 0 doubles that column on every
     * plain heal line. Keep the fact exactly as parsed and read the intent through AskedFor.
     */
    /*
     * Both amounts are 48-bit magnitudes, split across a four-byte half and the two bytes above it, and this row is the
     * one that pays for it: unlike a damage row it has no padding to hide the high words in, so widening both amounts
     * grows the row from 28 bytes to 32. That trade is taken deliberately (healing has a paren to carry and no spare
     * bytes), asserted in HealFactCaptureTest, and measured as retention before and after in docs/DesignNotes.md →
     * "Amount widths measured across 44 million hits".
     */
    private readonly uint _totalLow;
    private readonly uint _overTotalLow;

    // Shared sequence with the damage stream (one counter in CombatCapture), so a heal and a hit that
    // happen between two timestamps keep the order the consumer saw them in.
    public readonly int Seq;

    // int ids: the shared name pool is wider than 16 bits — see DamageFactTable.InternName for the decision and its price.
    public readonly int HealerIdx;
    public readonly int HealedIdx;

    // The halves above 32 bits of the two amounts. Zero for every amount an ordinary log prints.
    private readonly ushort _totalHigh;
    private readonly ushort _overTotalHigh;

    // Spell name index ("Heroic Renewal Rk. II"), which is what HealRecord.SubType holds and what
    // StatsUtil.CreateRecordKey builds its activity windows from.
    public readonly ushort SubIdx;

    /*
     * HealRecord.ModifiersMask, in one byte, read back as the short it always was.
     *
     * Measured reach, not a guess: across three captures a heal line's mask is only ever Twincast(1), Crit(2) and
     * Lucky(4) — 0x7 ORed over 1.3 M heals on the 663 MB one ({-1, 1, 2, 3, 6, 7} distinct values), and an EMU capture
     * writes plain 0 for all 22,703 of its heals because its line shape carries no modifier text at all. Everything the
     * healing board filters on lives in those three bits; the bits above 255 (Slay, Doublebow, Flurry, Finishing) are
     * melee/ranged vocabulary and never appeared on a heal.
     *
     * Two things keep it honest rather than lucky:
     *  - `MasksBeyondByte` counts any mask that could not be stored verbatim, so the day a heal line carries a bit above
     *    128 the answer is a logged counter and four bytes back on every heal, not a filter that quietly stopped firing.
     *  - `MaskNone` (255) stands for the parser's −1 ("the line carried no modifier text"), because 0 IS a real value an
     *    EMU log writes — so it cannot double as "none". A mask of literally 0xFF on a heal would read as none; that is
     *    counted by `MaskSentinelCollisions` and was never observed.
     */
    private readonly byte _modMask;

    /// <summary>The parser's −1 ("no modifier text"), distinct from a real mask of 0.</summary>
    public const byte MaskNone = byte.MaxValue;

    /// <summary>HealRecord.ModifiersMask as the healing board has always read it. See _modMask for the packing.</summary>
    public short ModMask => _modMask == MaskNone ? LineModifiersParser.None : _modMask;


    /*
     * The label and the flags share one byte, because a heal row's type is exactly one bit wide: HealingLineParser can
     * only ever produce LabelTypes.Heal (16) or LabelTypes.Hot (17), and `HitStatSplitTest` pins the arithmetic on those
     * two words. Bit 8 carries it — not bit 2 or 4, which stay retired — so `Flags` hands back the flag bits with the
     * label's bit hidden and FactsCarryOnlyWhatTheirOwnLinesSay keeps meaning what it says.
     */
    private const byte TypeBit = 8;
    private readonly byte _labelAndFlags;

    /// <summary>LabelTypes.Heal or LabelTypes.Hot, the only two a heal line can be.</summary>
    public byte TypeId => (_labelAndFlags & TypeBit) != 0 ? LabelTypes.Hot : LabelTypes.Heal;

    /// <summary>The fact flags, with the label's bit hidden. See FlagOwnerInLine and LineDerivedFlagMask.</summary>
    public byte Flags => (byte)(_labelAndFlags & ~TypeBit);

    /* Packing guards: counted rather than assumed. Zero means "this build never met a heal line that needed more than
     * the byte it has", which is the assertion HealFactPackingTest makes and the number to look at if a healing filter
     * is ever reported wrong. */
    internal static long MasksBeyondByte;
    internal static long MaskSentinelCollisions;
    internal static long UnknownTypeLabels;

    internal static void ResetPackingCounters()
    {
      MasksBeyondByte = 0;
      MaskSentinelCollisions = 0;
      UnknownTypeLabels = 0;
    }

    public HealFact(int seq, long timeS, int healerIdx, int healedIdx, long total, long overTotal,
      byte typeId, byte flags, short modMask, ushort subIdx)
    {
      // A landed amount the row cannot carry is refused here rather than written narrow: HealingLineParser already
      // refuses the line, so this is the door behind it for any other producer (a spool read, a test).
      if (!TextUtils.InAmountRange(total))
        throw new ArgumentOutOfRangeException(nameof(total), total, "A landed heal is a magnitude within 48 bits.");
      if (!TextUtils.InAmountRange(overTotal))
        throw new ArgumentOutOfRangeException(nameof(overTotal), overTotal, "A heal's asked-for amount is a magnitude within 48 bits.");

      Seq = seq;
      _timeS = FactTime.FromEpochSeconds(timeS);
      HealerIdx = healerIdx;
      HealedIdx = healedIdx;
      _totalLow = (uint)total;
      _totalHigh = (ushort)(total >> 32);
      _overTotalLow = (uint)overTotal;
      _overTotalHigh = (ushort)(overTotal >> 32);

      // The label bit: Heal is the absence of it, Hot is its presence. Anything else is a type this stream cannot hold —
      // counted rather than silently written as Heal, because that silent write is how a new label would vanish.
      if (typeId != LabelTypes.Heal && typeId != LabelTypes.Hot) UnknownTypeLabels++;
      _labelAndFlags = (byte)(flags | (typeId == LabelTypes.Hot ? TypeBit : 0));

      // The mask's one byte, with both edges counted rather than wrapped (see _modMask).
      if (modMask < 0)
      {
        if (modMask != LineModifiersParser.None) MasksBeyondByte++;   // only -1 means "no text"; anything else is a surprise
        _modMask = MaskNone;
      }
      else if (modMask >= MaskNone)
      {
        // 0xFF and above: storable only by losing a bit, so store the low byte and say so.
        if (modMask == MaskNone) MaskSentinelCollisions++; else MasksBeyondByte++;
        _modMask = (byte)modMask;
      }
      else
      {
        _modMask = (byte)modMask;
      }

      SubIdx = subIdx;
    }

    public bool OwnerInLine => (Flags & FlagOwnerInLine) != 0;

    /// <summary>What landed, as the line printed it — both halves. See the field comment for the split.</summary>
    public long Total => (long)_totalLow | ((long)_totalHigh << 32);

    /// <summary>The parenthesised amount verbatim; 0 means "the line never said". See AskedFor.</summary>
    public long OverTotal => (long)_overTotalLow | ((long)_overTotalHigh << 32);

    // What the line wanted, stated the way a caller means it: a heal with no "(amount)" asked for what it
    // landed. Use this for sums and ratios; use OverTotal when materializing a record, so the record behaves
    // under StatsUtil exactly like the one the parser made.
    public long AskedFor => OverTotal == 0L ? Total : OverTotal;

    // Healing the target did not need, never negative: an overflowing subtraction would print straight into a
    // stats column, and this table is fed by numbers parsed out of text.
    public long Overheal => OverTotal > Total ? OverTotal - Total : 0L;
  }

  // Storage contract for the heal stream, shaped like IFactTable so a chunked spill would cover both
  // streams the same way (D2). Name indices are shared with the damage table — see HealFactTable.
  internal interface IHealFactTable
  {
    int HealCount { get; }
    long EstimatedBytes { get; }

    /// <summary>Allocated heal-array bytes beyond what is stored. See CompactToCount.</summary>
    long SlackBytes { get; }

    /// <summary>Shrink the heal array to its rows, returning the bytes released (CombatCapture.CompactRows asks).</summary>
    long CompactToCount();
    HealFactReader Heals { get; }
    void AddHeal(HealFact heal);

    /*
     * Whether the rows are ordered by time. Windowed readers may halve for a start point while this holds; a store that
     * cannot promise it (a spool handing back runs out of order, a restored log concatenated backwards) says false and
     * every reader pays the full walk rather than silently losing the rows before the backwards append. The same law as
     * `CastHistory.AscendingTime`: one backwards row retires the fast path, permanently and honestly.
     */
    bool AscendingTime { get; }

    /// <summary>First row whose time is at or after `time`, or the count when none is. Meaningful only while AscendingTime holds.</summary>
    int FirstIndexAtOrAfter(double time);
    int InternName(string name);
    string NameOf(int idx);
    ushort InternSpell(string spell);
    string SpellOf(ushort idx);
  }

  /*
   * In-RAM heal storage, single-writer by the same contract as DamageFactTable (parser events arrive on
   * the LogProcessor consumer thread).
   *
   * It interns names through the damage table it was built beside. That is not a convenience — a raider is
   * one entity, and giving her index 7 in one table and 212 in another would mean every join between the
   * two streams resolves strings instead of indices, and the name table (the one thing both streams share
   * word-for-word) would be paid for twice. Spells are their own namespace because a spell is not a name:
   * nothing classifies them, and a collision-free index space keeps that true by construction.
   */
  internal sealed class HealFactTable : IHealFactTable
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private readonly DamageFactTable _names;

    /*
     * Chunked row storage, mirroring DamageFactTable: a chunk (32,768 rows x 28 bytes ~= 896 KiB) is bought when its first row arrives, so
     * nothing is ever copied, old and new never coexist, and no doubling of empty slots survives a load. Ordinals stay the currency that
     * HealSummarySource's window walk and the sequence law speak: chunk = ordinal >> shift, slot = ordinal & mask.
     */
    internal const int HealChunkShift = 15;                       // 32,768 rows x 28 B ~= 896 KiB per chunk (row width: see DamageFactTable.InternName)
    internal const int HealChunkSize = 1 << HealChunkShift;
    internal const int HealChunkMask = HealChunkSize - 1;

    private readonly ColdChunks<HealFact> _rows;
    private HealFact[] _tail = [];
    private int _healCount;

    // Shared publication/cache machinery, separate codec: see docs/DesignNotes.md → "Rows go cold, and what a chunk costs to remember".
    /// <summary>Rows with compressed backing, including any currently decoded in the read cache.</summary>
    public int ColdRowCount => ColdChunkCount * HealChunkSize;

    private readonly List<string> _spells = [];
    private readonly Dictionary<string, ushort> _spellMap = new(StringComparer.Ordinal);

    /*
     * Set once, at the first append that goes backwards in time, and never restored: like the cast history's flag beside
     * it, an unordered store is a property of the data this session was handed, and a reader that halved over it would
     * return a window that silently omitted rows. Lines arrive through one parser thread in file order, so in practice
     * this stays true and windowed reads get their start point for a halving instead of a walk.
     */
    private bool _ascendingTime = true;

    // Sizing mirrors the damage table's contract: the caller estimates from file size, growth doubles.
    // Heal lines measured about a quarter of the damage rate, so the default starts there.
    internal HealFactTable(DamageFactTable names, int initialCapacity = 16_384)
    {
      _names = names ?? throw new ArgumentNullException(nameof(names));
      _rows = new ColdChunks<HealFact>(HealChunkSize, initialCapacity, EncodeChunk,
        static blob => HealFactChunkCodec.Decode(blob, HealChunkSize),
        static ex => Log.Warn("cold chunk: heal encoding failed; leaving rows available", ex));
    }

    public int HealCount => Volatile.Read(ref _healCount);
    /// <summary>The capture's heals by ordinal (Length + this[ordinal]) — see HealFactReader and DamageFactTable.Facts for why this is a
    /// reader rather than the span it used to be.</summary>
    public HealFactReader Heals => new(this);

    /// <summary>Sequential access over [from, to) as the chunk slices covering it: zero allocation, contiguous inside each slice.</summary>
    public HealFactRange HealsIn(int from, int to) => new(this, Math.Max(0, from), Math.Min(to, _healCount));

    internal int HealChunksAllocated => _rows.RawCount;

    /// <summary>THE ordinal door for heals — same law as DamageFactTable.At: one row by the ordinal the heal window walk already
    /// carries, so chunked storage changes this method and no caller. Out of range reads back a zero row rather than throwing, and says
    /// so once (see DamageFactTable.MissedRow for why logging is the shape and silence is not).</summary>
    public HealFact At(int ordinal) => RefAt(ordinal);   // copies the row; use RefAt where a 32-byte copy matters

    /// <summary>By-reference form of the heal door — see DamageFactTable.RefAt.</summary>
    public ref readonly HealFact RefAt(int ordinal)
    {
      if ((uint)ordinal >= (uint)HealCount) return ref MissedRow(ordinal);
      return ref _rows.Read(ordinal >> HealChunkShift)[ordinal & HealChunkMask];
    }

    private int _refOutOfRangeReported;

    /// <summary>The heal half of DamageFactTable.MissedRow: a zero row back, one Warn per capture out.</summary>
    private ref readonly HealFact MissedRow(int ordinal)
    {
      if (Interlocked.Exchange(ref _refOutOfRangeReported, 1) == 0)
        Log.Warn($"heal ordinal {ordinal} is past the end of this capture ({HealCount:N0} row(s)); a zero row was read in its place");

      return ref ZeroRow;
    }

    /// <summary>The slice door: hand back a warm chunk, or decode a cold one into place first. Out of range reads an empty array (no row asks).</summary>
    internal HealFact[] ResolveRangeChunk(int ordinal)
    {
      if ((uint)ordinal >= (uint)_healCount) return [];
      return _rows.Read(ordinal >> HealChunkShift);
    }

    private static readonly HealFact ZeroRow;
    public long EstimatedBytes => ((long)HealChunksAllocated * HealChunkSize * Marshal.SizeOf<HealFact>()) + ColdBlobBytes;

    /// <summary>No doubling slack: chunks are allocated on demand, and only the append tail has unused slots.</summary>
    public long SlackBytes => 0;

    public long CompactToCount() => 0;

    // Names live in the damage table: same string, same index, in both streams.
    public int InternName(string name) => _names.InternName(name);
    public string NameOf(int idx) => _names.NameOf(idx);

    public ushort InternSpell(string spell)
    {
      if (string.IsNullOrEmpty(spell)) return HealFact.NoSpell;
      if (_spellMap.TryGetValue(spell, out var idx)) return idx;
      if (_spells.Count >= HealFact.NoSpell) throw new InvalidOperationException("capture: spell table exceeded capacity");
      _spells.Add(spell);
      idx = (ushort)(_spells.Count - 1);
      _spellMap[spell] = idx;
      return idx;
    }

    public string SpellOf(ushort idx) => idx == HealFact.NoSpell || idx >= _spells.Count ? null : _spells[idx];

    public void AddHeal(HealFact heal)
    {
      if (_ascendingTime && _healCount > 0 && heal.TimeS < At(_healCount - 1).TimeS) _ascendingTime = false;

      var ordinal = _healCount;
      var ci = ordinal >> HealChunkShift;
      if ((ordinal & HealChunkMask) == 0) _tail = _rows.NewTail(ci);
      _tail[ordinal & HealChunkMask] = heal;
      Volatile.Write(ref _healCount, ordinal + 1);
    }

    /*
     * Encode, then PROVE it by decoding and comparing every field before the array is released — both off the parse thread. A mismatch (or a
     * throw) leaves those rows warm in place and prints one line: a healing board is not worth a clever codec that can be quietly wrong.
     */
    private static byte[] EncodeChunk(HealFact[] rows)
    {
      var blob = HealFactChunkCodec.Encode(rows, rows.Length);
      var probe = HealFactChunkCodec.Decode(blob, rows.Length);
      for (var i = 0; i < rows.Length; i++)
      {
        var a = rows[i];
        var b = probe[i];
        if (a.Seq != b.Seq || a.TimeS != b.TimeS || a.HealerIdx != b.HealerIdx || a.HealedIdx != b.HealedIdx
            || a.Total != b.Total || a.OverTotal != b.OverTotal || a.TypeId != b.TypeId || a.Flags != b.Flags
            || a.ModMask != b.ModMask || a.SubIdx != b.SubIdx)
          throw new InvalidOperationException("cold heal chunk round trip differs at row " + i);
      }
      return blob;
    }

    /// <summary>How many chunks are held as blobs right now.</summary>
    internal int ColdChunkCount => _rows.BlobCount;

    /// <summary>Blob bytes over the whole cold set (the footprint probes ask for it).</summary>
    internal long ColdBlobBytes => _rows.BlobBytes;

    /// <summary>Join the append encoder after writing stops; returns the completed backing count, not work raced for.</summary>
    internal int ColdifyNow() => _rows.Settle();

    internal IDisposable RetainDecoded() => _rows.RetainDecoded();

    /// <inheritdoc cref="IHealFactTable.AscendingTime"/>
    public bool AscendingTime => _ascendingTime;

    /*
     * The first row at or after `time` (rows ascending while AscendingTime holds — the caller's job, and HealSummarySource
     * asks it). Returns HealCount when every row is older, which is the honest "start past the end" rather than a clamp:
     * a window to the right of the capture materializes nothing either way.
     */
    public int FirstIndexAtOrAfter(double time)
    {
      var low = 0;
      var high = _healCount - 1;

      while (low <= high)
      {
        var middle = (low + high) >>> 1;
        if (At(middle).TimeS >= time) high = middle - 1;   // the ordinal door: rows live in chunks now
        else low = middle + 1;
      }

      return low;
    }
  }

  /*
   * Ordinal view over the chunked heal rows — Length and this[ordinal], which is everything HealSummarySource's window walk, the
   * classification cursors and the report ever asked of the flat span. A class because the interface cannot hand out a ref struct.
   */
  internal sealed class HealFactReader(HealFactTable table) : System.Collections.Generic.IEnumerable<HealFact>
  {
    public int Length => table.HealCount;

    public ref readonly HealFact this[int ordinal] => ref table.RefAt(ordinal);

    public RowEnumerator GetEnumerator() => new(table);

    System.Collections.Generic.IEnumerator<HealFact> System.Collections.Generic.IEnumerable<HealFact>.GetEnumerator()
    {
      for (var i = 0; i < table.HealCount; i++) yield return table.At(i);
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
      => ((System.Collections.Generic.IEnumerable<HealFact>)this).GetEnumerator();

    public struct RowEnumerator(HealFactTable table)
    {
      private readonly HealFactTable _table = table;
      private int _index = -1;

      public HealFact Current => _table.At(_index);

      public bool MoveNext() => ++_index < _table.HealCount;
    }
  }

  /// <summary>Sequential read of an ordinal range as chunk slices; see DamageFactRange for the reasoning.</summary>
  internal ref struct HealFactRange(HealFactTable table, int from, int to)
  {
    private readonly HealFactTable _table = table;
    private readonly int _from = from;
    private readonly int _to = Math.Max(from, to);

    public Enumerator GetEnumerator() => new(_table, _from, _to);

    public ref struct Enumerator(HealFactTable table, int from, int to)
    {
      private readonly HealFactTable _table = table;
      private readonly int _to = to;
      private int _next = from;
      private int _start = from;
      private int _length;

      public ReadOnlySpan<HealFact> Current => Resolve(_start).AsSpan(_start & HealFactTable.HealChunkMask, _length);

      /// <summary>A cold slice is decoded back into the table, so a window walk over last night's capture reads contiguous spans either way.</summary>
      private HealFact[] Resolve(int ordinal) => _table.ResolveRangeChunk(ordinal);

      public bool MoveNext()
      {
        if (_next >= _to) return false;

        _start = _next;
        _length = Math.Min(HealFactTable.HealChunkSize - (_start & HealFactTable.HealChunkMask), _to - _start);
        _next += _length;
        return true;
      }
    }
  }
}
