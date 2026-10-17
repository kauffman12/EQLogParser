using System;
using System.Collections.Generic;
using System.Threading;

namespace EQLogParser;

// Window-owned accounting. Only the worker touches it; published stats contain no references back into this state.
// Published rows support the meter and parse formatter only, not breakdowns, charts, or full board statistics.
internal sealed class DamageMeterCalculation
{
  private sealed class Life
  {
    internal MeterLifeInput Input;
    internal int DamageThrough;
    internal int TankThrough;
    internal readonly MeterDirectionSlice Damage = new();
    internal readonly MeterDirectionSlice Tank = new();
    internal readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);
  }

  private Dictionary<DerivedFight, Life> _lives = [];
  private readonly Dictionary<string, long> _names = new(StringComparer.OrdinalIgnoreCase);
  private FightFactIndex _index;
  private DamageFactTable _facts;
  private double _from = double.NaN;
  private double _to = double.NegativeInfinity;
  private double _future = double.PositiveInfinity;
  private MeterDamageFilter _filter;
  private MeterDirectionTotals _damage = new(false);
  private MeterDirectionTotals _tank = new(true);

  internal MeterActivityIndex Activity { get; } = new();

  internal DamageOverlayStats Update(MeterFactInput input, CancellationToken cancellation = default)
  {
    var trace = StatsBuildTrace.Begin("meter", "damage meter overlay", "incremental");
    try
    {
      return UpdateCore(input, cancellation, trace);
    }
    finally
    {
      StatsBuildTrace.End(trace, $"lives={input.Lives.Count}");
    }
  }

  internal void Clear()
  {
    Activity.Clear();
    _lives.Clear();
    _names.Clear();
    _index = null;
    _facts = null;
    _damage = new MeterDirectionTotals(false);
    _tank = new MeterDirectionTotals(true);
  }

  private DamageOverlayStats UpdateCore(MeterFactInput input, CancellationToken cancellation,
    in StatsBuildTrace.Handle trace)
  {
    cancellation.ThrowIfCancellationRequested();
    var filter = MeterDamageFilter.Current;
    if (!ReferenceEquals(_facts, input.Facts) || _from != input.From || _filter != filter || input.To < _to
      || input.To >= _future || !AnswersMatch(input.Index, cancellation)
      || !ReferenceEquals(_index, input.Index) && !TryRebind(input, cancellation))
    {
      _lives.Clear();
      _names.Clear();
      _damage = new MeterDirectionTotals(false);
      _tank = new MeterDirectionTotals(true);
      _future = double.PositiveInfinity;
    }
    _index = input.Index;
    _facts = input.Facts;
    _from = input.From;
    _to = input.To;
    _filter = filter;

    try
    {
      var selected = new HashSet<DerivedFight>();
      foreach (var life in input.Lives) selected.Add(life.Source);
      var rebuild = false;
      foreach (var life in _lives.Keys)
        if (!selected.Contains(life)) { rebuild = true; break; }
      if (rebuild)
      {
        var retained = new Dictionary<DerivedFight, Life>();
        foreach (var life in selected)
          if (_lives.TryGetValue(life, out var state)) retained[life] = state;
        _lives = retained;
        var retainedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in _lives.Values) retainedNames.UnionWith(state.Names);
        foreach (var name in new List<string>(_names.Keys))
          if (!retainedNames.Contains(name)) _names.Remove(name);
        RebuildTotals(cancellation);
      }

      var allRanges = new TimeRange();
      var orderedLives = new List<MeterLifeInput>(input.Lives);
      orderedLives.Sort(static (a, b) => a.Id.CompareTo(b.Id));
      var damageSlices = new List<MeterDirectionSlice>();
      var tankSlices = new List<MeterDirectionSlice>();
      var count = 0;
      var firstId = int.MaxValue;
      string firstName = null;
      foreach (var header in orderedLives)
      {
        if (!_lives.TryGetValue(header.Source, out var life))
          _lives[header.Source] = life = new Life();
        life.Input = header;
        cancellation.ThrowIfCancellationRequested();
        rebuild |= Read(life, input, false, cancellation);
        rebuild |= Read(life, input, true, cancellation);
        if (life.Damage.Activity.Count == 0 && life.Tank.Activity.Count == 0) continue;
        count++;
        damageSlices.Add(life.Damage);
        tankSlices.Add(life.Tank);
        if (header.Id < firstId) { firstId = header.Id; firstName = header.Name; }
        allRanges.Add(new TimeSegment(Math.Max(header.Begin, input.From), Math.Min(header.Last, input.To)));
      }
      if (rebuild) RebuildTotals(cancellation);
      cancellation.ThrowIfCancellationRequested();
      StatsBuildTrace.Stage(trace, "delta");
      if (count == 0) return null;
      var title = StatsUtil.CombinedPrefix(count, true) + firstName;
      var damage = _damage.Publish(title, allRanges, damageSlices);
      cancellation.ThrowIfCancellationRequested();
      var tank = _tank.Publish(title, allRanges, tankSlices);
      cancellation.ThrowIfCancellationRequested();
      var result = new DamageOverlayStats { DamageStats = damage, TankStats = tank };
      var raid = (result.DamageStats ?? result.TankStats)?.RaidStats;
      if (raid is not null)
      {
        StatsUtil.PopulateSpecials(raid);
        ApplySpecials(result.DamageStats, raid);
        ApplySpecials(result.TankStats, raid);
      }
      cancellation.ThrowIfCancellationRequested();
      StatsBuildTrace.Stage(trace, "present");
      return result;
    }
    catch
    {
      // A partial batch cannot be resumed as if it committed. The next request reconstructs from authoritative facts.
      Clear();
      throw;
    }
  }

  private bool Read(Life life, MeterFactInput input, bool tanking, CancellationToken cancellation)
  {
    var slice = tanking ? life.Tank : life.Damage;
    var totals = tanking ? _tank : _damage;
    var through = tanking ? life.TankThrough : life.DamageThrough;
    var end = tanking ? life.Input.TankCount : life.Input.DamageCount;
    if (end < through) throw new InvalidOperationException("Meter projection moved behind its cursor");
    if (through == end) return false;
    var changed = new HashSet<string>(StringComparer.Ordinal);
    var backwards = false;
    while (through < end)
    {
      cancellation.ThrowIfCancellationRequested();
      var ordinals = input.Index.ReadMeterOrdinals(life.Input.Source, tanking, through, end);
      foreach (var ordinal in ordinals)
      {
        var fact = input.Facts.At(ordinal);
        if (fact.TimeS > input.To)
        {
          _future = Math.Min(_future, fact.TimeS);
          continue;
        }
        if (fact.TimeS < input.From) continue;
        var attacker = input.Facts.NameOf(fact.AtkIdx);
        var defender = input.Facts.NameOf(fact.DefIdx);
        life.Names.Add(attacker);
        life.Names.Add(defender);
        RememberName(attacker, input.Index);
        RememberName(defender, input.Index);
        var hit = input.Index.ReadMeterHit(fact, input.Facts);
        backwards |= slice.Add(hit, tanking, _filter);
        totals.AddAmounts(hit, _filter);
        changed.Add(tanking ? hit.Defender : hit.Attacker);
      }
      through += ordinals.Length;
    }
    foreach (var name in changed) totals.AddActivity(name, slice.Activity[name]);
    totals.AddBounds(slice);
    if (tanking) life.TankThrough = through;
    else life.DamageThrough = through;
    return backwards;
  }

  private static void ApplySpecials(CombinedStats stats, PlayerStats raid)
  {
    if (stats is null) return;
    foreach (var row in stats.StatsList)
      if (raid.Specials.TryGetValue(row.OrigName, out var special)) row.Special = special;
  }

  private bool TryRebind(MeterFactInput input, CancellationToken cancellation)
  {
    if (_index is null) return false;
    var previous = new Dictionary<(string Name, int First), Life>();
    foreach (var life in _lives.Values)
    {
      cancellation.ThrowIfCancellationRequested();
      previous[(life.Input.Name, life.Input.FirstOrdinal)] = life;
    }
    var rebound = new Dictionary<DerivedFight, Life>();
    var reused = new HashSet<Life>();
    foreach (var header in input.Lives)
    {
      cancellation.ThrowIfCancellationRequested();
      if (!previous.TryGetValue((header.Name, header.FirstOrdinal), out var life)) continue;
      if (life.DamageThrough > header.DamageCount || life.TankThrough > header.TankCount
        || !input.Index.MeterPrefixMatches(_index, life.Input.Source, header.Source,
          false, life.DamageThrough, cancellation)
        || !input.Index.MeterPrefixMatches(_index, life.Input.Source, header.Source,
          true, life.TankThrough, cancellation))
        return false;
      rebound[header.Source] = life;
      reused.Add(life);
    }
    // Removed lives still need their contributions withdrawn by Update's membership comparison.
    foreach (var pair in _lives)
    {
      cancellation.ThrowIfCancellationRequested();
      if (!reused.Contains(pair.Value)) rebound[pair.Key] = pair.Value;
    }
    _lives = rebound;
    return true;
  }

  private void RememberName(string name, FightFactIndex index)
  {
    if (!_names.ContainsKey(name)) _names[name] = index.MeterAnswerFor(name);
  }

  private bool AnswersMatch(FightFactIndex index, CancellationToken cancellation)
  {
    foreach (var pair in _names)
    {
      cancellation.ThrowIfCancellationRequested();
      if (pair.Value != index.MeterAnswerFor(pair.Key)) return false;
    }
    return true;
  }

  private void RebuildTotals(CancellationToken cancellation)
  {
    _damage = new MeterDirectionTotals(false);
    _tank = new MeterDirectionTotals(true);
    foreach (var life in _lives.Values)
    {
      cancellation.ThrowIfCancellationRequested();
      _damage.Merge(life.Damage);
      _tank.Merge(life.Tank);
    }
  }
}
