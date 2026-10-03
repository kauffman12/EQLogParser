using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EQLogParser.Mirror
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
   * 40 B/fact (see the size test). Heal lines run at roughly a quarter of the damage rate on the logs
   * measured here, so on a 5 M-damage-fact log this table costs ~50 MB against damage's 160 MB — and it is
   * counted in the same D2 memory estimate (EstimatedBytes).
   */
  internal readonly struct HealFact
  {
    // No spell text on the line. Distinct from an empty string, which the interner refuses anyway.
    public const ushort NoSpell = ushort.MaxValue;

    /*
     * The healer's own name carries its owner ("Bulgar`s pet", "Bulgar`s warder"). Flag rather than a
     * resolved owner: the mirror stores what the line says and rules decide what it means (D1). The
     * explicit "Owner:" annotation has no equivalent here because HealProcessedEvent carries no raw line —
     * extending that event is the documented route if heal lines turn out to need it, not a second parse.
     */
    public const byte FlagOwnerInLine = 1;

    // PlayerRegistry answers at the instant the parser fired (same contract, same caveat as
    // DamageFact.FlagAttkPlayerSide): a fact about what the live pipeline saw, not a judgment.
    public const byte FlagHealerPlayerSide = 2;
    public const byte FlagHealedPlayerSide = 4;

    // Shared sequence with the damage stream (one counter in CombatCapture), so a heal and a hit that
    // happen between two timestamps keep the order the consumer saw them in.
    public readonly int Seq;
    public readonly long TimeS;
    public readonly short HealerIdx;
    public readonly short HealedIdx;

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
    public readonly uint Total;
    public readonly uint OverTotal;
    public readonly byte TypeId;    // LabelTypes.Heal or LabelTypes.Hot
    public readonly byte Flags;

    // HealRecord.ModifiersMask as parsed (-1 when the line carried no modifier text). The healing board's
    // filters read it, so a derived board needs it for the same reason damage does.
    public readonly short ModMask;

    // Spell name index ("Heroic Renewal Rk. II"), which is what HealRecord.SubType holds and what
    // StatsUtil.CreateRecordKey builds its activity windows from.
    public readonly ushort SubIdx;

    public HealFact(int seq, long timeS, short healerIdx, short healedIdx, uint total, uint overTotal,
      byte typeId, byte flags, short modMask, ushort subIdx)
    {
      Seq = seq;
      TimeS = timeS;
      HealerIdx = healerIdx;
      HealedIdx = healedIdx;
      Total = total;
      OverTotal = overTotal;
      TypeId = typeId;
      Flags = flags;
      ModMask = modMask;
      SubIdx = subIdx;
    }

    public bool OwnerInLine => (Flags & FlagOwnerInLine) != 0;
    public bool HealerPlayerSide => (Flags & FlagHealerPlayerSide) != 0;
    public bool HealedPlayerSide => (Flags & FlagHealedPlayerSide) != 0;

    // What the line wanted, stated the way a caller means it: a heal with no "(amount)" asked for what it
    // landed. Use this for sums and ratios; use OverTotal when materializing a record, so the record behaves
    // under StatsUtil exactly like the one the parser made.
    public uint AskedFor => OverTotal == 0 ? Total : OverTotal;

    // Healing the target did not need, never negative: an overflowing subtraction would print straight into a
    // stats column, and this table is fed by numbers parsed out of text.
    public uint Overheal => OverTotal > Total ? OverTotal - Total : 0;
  }

  // Storage contract for the heal stream, shaped like IFactTable so a chunked spill would cover both
  // streams the same way (D2). Name indices are shared with the damage table — see HealFactTable.
  internal interface IHealFactTable
  {
    int HealCount { get; }
    long EstimatedBytes { get; }
    ReadOnlySpan<HealFact> Heals { get; }
    void AddHeal(HealFact heal);
    short InternName(string name);
    string NameOf(short idx);
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
    private readonly DamageFactTable _names;

    private HealFact[] _heals;
    private int _healCount;

    private readonly List<string> _spells = [];
    private readonly Dictionary<string, ushort> _spellMap = new(StringComparer.Ordinal);

    // Sizing mirrors the damage table's contract: the caller estimates from file size, growth doubles.
    // Heal lines measured about a quarter of the damage rate, so the default starts there.
    internal HealFactTable(DamageFactTable names, int initialCapacity = 16_384)
    {
      _names = names ?? throw new ArgumentNullException(nameof(names));
      _heals = new HealFact[initialCapacity];
    }

    public int HealCount => _healCount;
    public ReadOnlySpan<HealFact> Heals => _heals.AsSpan(0, _healCount);
    public long EstimatedBytes => (long)_heals.Length * Marshal.SizeOf<HealFact>();

    // Names live in the damage table: same string, same index, in both streams.
    public short InternName(string name) => _names.InternName(name);
    public string NameOf(short idx) => _names.NameOf(idx);

    public ushort InternSpell(string spell)
    {
      if (string.IsNullOrEmpty(spell)) return HealFact.NoSpell;
      if (_spellMap.TryGetValue(spell, out var idx)) return idx;
      if (_spells.Count >= HealFact.NoSpell) throw new InvalidOperationException("mirror: spell table exceeded capacity");
      _spells.Add(spell);
      idx = (ushort)(_spells.Count - 1);
      _spellMap[spell] = idx;
      return idx;
    }

    public string SpellOf(ushort idx) => idx == HealFact.NoSpell || idx >= _spells.Count ? null : _spells[idx];

    public void AddHeal(HealFact heal)
    {
      if (_healCount == _heals.Length) Array.Resize(ref _heals, _heals.Length * 2);
      _heals[_healCount++] = heal;
    }
  }
}
