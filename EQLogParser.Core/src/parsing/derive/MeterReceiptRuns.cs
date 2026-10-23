using System;

namespace EQLogParser;

// Cache all three panel choices together: changing the choice never requires another fact scan.
internal sealed class MeterReceiptRuns
{
  internal const int MaxMode = 100;
  private readonly Run _both;
  private readonly Run _damage;
  private readonly Run _tanking;

  // Retention asks whether either lane still speaks for the newest batch.
  internal MeterArrival Last => _both.Last;

  internal MeterReceiptRuns()
  {
    _both = new();
    _damage = new();
    _tanking = new();
  }

  private MeterReceiptRuns(MeterReceiptRuns source)
  {
    _both = source._both.Copy();
    _damage = source._damage.Copy();
    _tanking = source._tanking.Copy();
  }

  internal MeterReceiptRuns Copy() => new(this);

  internal MeterArrival ForMode(int mode, MeterPanels panels)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(mode);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(mode, MaxMode);
    return panels switch
    {
      MeterPanels.Damage => _damage.ForMode(mode),
      MeterPanels.Tanking => _tanking.ForMode(mode),
      MeterPanels.Both => _both.ForMode(mode),
      _ => MeterArrival.None
    };
  }

  internal void Add(MeterReceipt receipt)
  {
    _both.Add(receipt.Both);
    _damage.Add(receipt.Damage);
    _tanking.Add(receipt.Tanking);
  }

  // A hidden lane must not bridge a quiet gap, even when no worker observed the expiry.
  private sealed class Run
  {
    private readonly int[] _firstOrdinals;
    internal MeterArrival Last { get; private set; } = MeterArrival.None;

    internal Run() => _firstOrdinals = new int[MaxMode + 1];

    private Run(Run source)
    {
      Last = source.Last;
      _firstOrdinals = (int[])source._firstOrdinals.Clone();
    }

    internal Run Copy() => new(this);

    internal MeterArrival ForMode(int mode) => Last with { FirstOrdinal = _firstOrdinals[mode] };

    internal void Add(MeterArrival receipt)
    {
      if (receipt.Through == 0) return;
      if (Last.Through > 0)
      {
        var gap = receipt.Time - Last.Time;
        for (var mode = 1; mode <= MaxMode && gap > mode; mode++)
          _firstOrdinals[mode] = receipt.FirstOrdinal;
      }
      Last = receipt;
    }
  }
}

// Each lane keeps its own ordinal endpoints; both share the batch's receipt time.
internal readonly record struct MeterReceipt(double Time, int DamageFirst, int DamageThrough,
  int TankingFirst, int TankingThrough)
{
  internal MeterReceipt(MeterArrival damage, MeterArrival tanking)
    : this(Math.Max(damage.Time, tanking.Time), damage.FirstOrdinal, damage.Through,
      tanking.FirstOrdinal, tanking.Through) { }

  internal MeterArrival Damage => DamageThrough == 0 ? MeterArrival.None : new(DamageThrough, Time, DamageFirst);
  internal MeterArrival Tanking => TankingThrough == 0 ? MeterArrival.None : new(TankingThrough, Time, TankingFirst);
  internal int Through => Math.Max(DamageThrough, TankingThrough);
  internal MeterArrival Both => DamageThrough == 0 ? Tanking : TankingThrough == 0 ? Damage
    : new(Through, Time, Math.Min(DamageFirst, TankingFirst));
}
