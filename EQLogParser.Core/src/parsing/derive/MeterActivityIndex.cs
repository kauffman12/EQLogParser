using System;
using System.Collections.Generic;
using System.Threading;

namespace EQLogParser;

internal readonly record struct MeterLifeActivity(bool HasDamage, double Last, int LastSequence = -1)
{
  internal static readonly MeterLifeActivity None = new(false, double.NegativeInfinity);
}

/*
 * Which panel an act of combat belongs to — and, because one word should answer both questions, which panels the operator
 * switched on. A fact EARNS a panel: outgoing damage the exclusions allow earns Damage; a hit landing on our side (player,
 * pet or mercenary) earns Tanking. What a meter may act on is what was earned INTERSECTED with what is displayed, which is the
 * whole law behind "a lane you do not watch cannot wake my overlay": the same receipt answers both readings, so changing the
 * choice costs no rescan and widening it shows history at once.
 *
 * The values are what settings.txt stores for `OverlayMeterPanels`, so `None` is never a legal choice — an absent or unreadable
 * key reads Both, and a meter can never be configured into having no tabs.
 */
[Flags]
internal enum MeterPanels : byte
{
  None = 0,
  Damage = 1,
  Tanking = 2,
  Both = Damage | Tanking,
}

// Worker-owned scalar prefixes. The index routes incoming/outgoing; only outgoing obeys damage exclusions.
internal sealed class MeterActivityIndex
{
  private sealed class Life
  {
    internal int DamageThrough;
    internal int TankThrough;
    internal MeterLifeActivity Activity = MeterLifeActivity.None;
    internal MeterLifeInput Header;
  }

  private Dictionary<DerivedFight, Life> _lives = [];
  private FightFactIndex _index;
  private MeterDamageFilter _filter;
  private double _from = double.NaN;
  private double _future = double.PositiveInfinity;
  private double _to = double.NegativeInfinity;
  private int _firstOrdinal;

  internal bool InputsCurrent => _index is not null && _filter == MeterDamageFilter.Current;

  internal MeterLifeActivity For(DerivedFight row) =>
    _lives.TryGetValue(row, out var life) ? life.Activity : MeterLifeActivity.None;

  internal void Clear()
  {
    _lives.Clear();
    _index = null;
    _from = double.NaN;
    _future = double.PositiveInfinity;
    _to = double.NegativeInfinity;
  }

  internal void Update(MeterFactInput input, CancellationToken cancellation = default)
  {
    var filter = MeterDamageFilter.Current;
    if (input.FirstOrdinal != _firstOrdinal || input.From != _from || filter != _filter
      || double.IsFinite(_future) && input.To >= _future || input.To < _to) Clear();
    if (_index is not null && !ReferenceEquals(input.Index, _index)) Rebind(input, cancellation);
    _index = input.Index;
    _filter = filter;
    _from = input.From;
    _to = input.To;
    _firstOrdinal = input.FirstOrdinal;
    var selected = new HashSet<DerivedFight>();
    foreach (var header in input.Lives)
    {
      cancellation.ThrowIfCancellationRequested();
      selected.Add(header.Source);
      if (!_lives.TryGetValue(header.Source, out var life)) _lives[header.Source] = life = new Life();
      life.Header = header;
      Read(header, life, input, false, cancellation);
      Read(header, life, input, true, cancellation);
    }
    foreach (var row in new List<DerivedFight>(_lives.Keys))
      if (!selected.Contains(row)) _lives.Remove(row);
  }

  // Pending rows have no routing index yet. Share projection's admission, anchor and target decisions:
  // incoming earns Tanking without outgoing exclusions; permitted output earns Damage; Neither earns nothing.
  internal static MeterPanels AcceptsPending(DamageFact fact, EntityTimeline timeline, string attacker, string defender,
    MeterDamageFilter filter)
  {
    if (!FightProjection.TryCombatSides(fact, timeline, attacker, defender, out var atkSide, out var defSide))
      return MeterPanels.None;

    var anchor = FightProjection.CombatAnchor(timeline, attacker, defender, fact.TimeS, atkSide, defSide);
    var aimedAtAnchor = string.Equals(anchor, defender, StringComparison.Ordinal);
    var target = FightProjection.CombatTarget(timeline, defender, fact.TimeS, defSide, aimedAtAnchor);
    return target switch
    {
      FightProjection.FactTarget.RaidSide => MeterPanels.Tanking,
      FightProjection.FactTarget.AtOwner when filter.Allows(fact) => MeterPanels.Damage,
      _ => MeterPanels.None
    };
  }

  private void Rebind(MeterFactInput input, CancellationToken cancellation)
  {
    var previous = new Dictionary<int, Life>();
    foreach (var life in _lives.Values)
      if (life.Header.FirstOrdinal != int.MaxValue) previous[life.Header.FirstOrdinal] = life;
    var rebound = new Dictionary<DerivedFight, Life>();
    foreach (var header in input.Lives)
    {
      cancellation.ThrowIfCancellationRequested();
      if (!previous.TryGetValue(header.FirstOrdinal, out var life)
        || life.DamageThrough > header.DamageCount || life.TankThrough > header.TankCount) continue;
      if (input.Index.MeterPrefixMatches(_index, life.Header.Source, header.Source, false,
          life.DamageThrough, cancellation)
        && input.Index.MeterPrefixMatches(_index, life.Header.Source, header.Source, true,
          life.TankThrough, cancellation)) rebound[header.Source] = life;
    }
    _lives = rebound;
  }

  private void Read(MeterLifeInput header, Life life, MeterFactInput input, bool incoming,
    CancellationToken cancellation)
  {
    var through = incoming ? life.TankThrough : life.DamageThrough;
    var end = incoming ? header.TankCount : header.DamageCount;
    if (end < through) throw new InvalidOperationException("Meter activity projection moved behind its cursor");
    while (through < end)
    {
      cancellation.ThrowIfCancellationRequested();
      var ordinals = input.Index.ReadMeterOrdinals(header.Source, incoming, through, end);
      foreach (var ordinal in ordinals)
      {
        if (ordinal < input.FirstOrdinal) continue;
        var fact = input.Facts.At(ordinal);
        if (fact.TimeS < input.From) continue;
        if (fact.TimeS > input.To)
        {
          _future = Math.Min(_future, fact.TimeS);
          continue;
        }
        if (!incoming && !_filter.Allows(fact)) continue;
        life.Activity = new MeterLifeActivity(life.Activity.HasDamage || fact.Total > 0,
          Math.Max(life.Activity.Last, fact.TimeS), Math.Max(life.Activity.LastSequence, fact.Seq));
      }
      through += ordinals.Length;
    }
    if (incoming) life.TankThrough = through;
    else life.DamageThrough = through;
  }
}
