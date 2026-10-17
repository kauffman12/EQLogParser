using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace EQLogParser;

// Captured at the projection gate. Source is an index key only; workers read the detached headers, never its fields.
internal sealed record MeterLifeInput(DerivedFight Source, string Name, int Id, double Begin, double Last,
  int DamageCount, int TankCount, int FirstOrdinal);

internal sealed record MeterFactInput(FightFactIndex Index, DamageFactTable Facts,
  IReadOnlyList<MeterLifeInput> Lives, double From, double To);

internal readonly record struct MeterHit(string Attacker, string Defender, string Owner, string Type,
  short Modifiers, long Total, double Time);

internal sealed partial class FightFactIndex
{
  internal MeterFactInput CaptureMeterInput(IReadOnlyList<DerivedFight> lives, DamageFactTable facts,
    double from, double to)
  {
    lock (_gate)
    {
      var result = new List<MeterLifeInput>(lives.Count);
      foreach (var life in lives)
      {
        var damage = _damageOrdinals.TryGetValue(life, out var run) ? run.Count : 0;
        var first = damage > 0 ? run[0] : int.MaxValue;
        var tank = _tankingOrdinals.TryGetValue(life, out run) ? run.Count : 0;
        if (tank > 0) first = Math.Min(first, run[0]);
        result.Add(new MeterLifeInput(life, life.Name, life.Id, life.BeginTime, life.LastTime, damage, tank, first));
      }
      return new MeterFactInput(this, facts, result, from, to);
    }
  }

  // Copy a bounded suffix under the index lock; decoding and counting must happen after releasing it.
  internal int[] ReadMeterOrdinals(DerivedFight life, bool tanking, int start, int end)
  {
    lock (_gate)
    {
      if (start == end) return [];
      var store = tanking ? _tankingOrdinals : _damageOrdinals;
      var run = store[life];
      if (start < 0 || end > run.Count || end < start) throw new InvalidOperationException("Invalid meter cursor");
      var result = new int[Math.Min(2048, end - start)];
      run.CopyTo(start, result, 0, result.Length);
      return result;
    }
  }

  internal long MeterAnswerFor(string name) => _charmers?.AnswerStampFor(name) ?? 0;

  // An index replacement may keep every routed prefix intact. Verify ordinals, not a hash or timestamp heuristic.
  internal bool MeterPrefixMatches(FightFactIndex previous, DerivedFight oldLife, DerivedFight newLife,
    bool tanking, int count, CancellationToken cancellation = default)
  {
    cancellation.ThrowIfCancellationRequested();
    Span<int> oldRun = stackalloc int[2048];
    Span<int> newRun = stackalloc int[2048];
    for (var start = 0; start < count;)
    {
      var length = Math.Min(oldRun.Length, count - start);
      previous.CopyMeterOrdinals(oldLife, tanking, start, oldRun[..length]);
      CopyMeterOrdinals(newLife, tanking, start, newRun[..length]);
      cancellation.ThrowIfCancellationRequested();
      if (!oldRun[..length].SequenceEqual(newRun[..length])) return false;
      start += length;
    }
    return true;
  }

  internal MeterHit ReadMeterHit(DamageFact fact, DamageFactTable facts)
  {
    var attacker = AttackerOf(fact, facts);
    return new MeterHit(attacker, facts.NameOf(fact.DefIdx), OwnerOf(fact, attacker),
      LabelTypes.LabelOf(fact.TypeId), fact.ModMask, fact.Total, fact.TimeS);
  }

  private void CopyMeterOrdinals(DerivedFight life, bool tanking, int start, Span<int> target)
  {
    lock (_gate)
    {
      var run = (tanking ? _tankingOrdinals : _damageOrdinals)[life];
      CollectionsMarshal.AsSpan(run).Slice(start, target.Length).CopyTo(target);
    }
  }
}
