using System;
using System.Collections.Generic;
using System.Threading;

namespace EQLogParser;

internal readonly record struct MeterLifeActivity(bool HasDamage, double Last)
{
  internal static readonly MeterLifeActivity None = new(false, double.NegativeInfinity);
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
    if (input.From != _from || filter != _filter || input.To >= _future || input.To < _to) Clear();
    if (_index is not null && !ReferenceEquals(input.Index, _index)) Rebind(input, cancellation);
    _index = input.Index;
    _filter = filter;
    _from = input.From;
    _to = input.To;
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

  // A pending row has no routing index yet. Match combat admission and preserve incoming damage exclusions' exemption.
  internal static bool AcceptsPending(DamageFact fact, EntityTimeline timeline, string attacker, string defender,
    MeterDamageFilter filter)
  {
    if (!FightProjection.TryCombatSides(fact, timeline, attacker, defender, out var atkSide, out var defSide))
      return false;
    var incoming = timeline.IsConfirmedRaidPersonAt(defender, fact.TimeS)
      || (defSide == FightProjection.Side.Player && atkSide != FightProjection.Side.Player)
      || (defSide == FightProjection.Side.Unknown && atkSide == FightProjection.Side.Npc);
    return incoming || filter.Allows(fact);
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
        var fact = input.Facts.At(ordinal);
        if (fact.TimeS < input.From) continue;
        if (fact.TimeS > input.To)
        {
          _future = Math.Min(_future, fact.TimeS);
          continue;
        }
        if (!incoming && !_filter.Allows(fact)) continue;
        life.Activity = new MeterLifeActivity(life.Activity.HasDamage || fact.Total > 0,
          Math.Max(life.Activity.Last, fact.TimeS));
      }
      through += ordinals.Length;
    }
    if (incoming) life.TankThrough = through;
    else life.DamageThrough = through;
  }
}
