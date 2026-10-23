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
    _both.Add(receipt.Both, receipt.MinimumMode);
    _damage.Add(receipt.Damage, receipt.MinimumMode);
    _tanking.Add(receipt.Tanking, receipt.MinimumMode);
  }

  // A hidden lane must not bridge a quiet gap, even when no worker observed the expiry.
  private sealed class Run
  {
    private readonly int[] _firstOrdinals;
    private readonly MeterArrival[] _last;
    internal MeterArrival Last { get; private set; } = MeterArrival.None;

    internal Run()
    {
      _firstOrdinals = new int[MaxMode + 1];
      _last = new MeterArrival[MaxMode + 1];
      Array.Fill(_last, MeterArrival.None);
    }

    private Run(Run source)
    {
      Last = source.Last;
      _firstOrdinals = (int[])source._firstOrdinals.Clone();
      _last = (MeterArrival[])source._last.Clone();
    }

    internal Run Copy() => new(this);

    internal MeterArrival ForMode(int mode) => _last[mode] with { FirstOrdinal = _firstOrdinals[mode] };

    internal void Add(MeterArrival receipt, int minimumMode)
    {
      if (receipt.Through == 0) return;
      // Source age qualifies activity once, at receipt. Each mode keeps its own last qualified arrival and gap.
      if (minimumMode <= FightProjection.EngagementGapS) _last[0] = receipt;
      for (var mode = Math.Max(1, minimumMode); mode <= MaxMode; mode++)
      {
        var previous = _last[mode];
        if (previous.Through > 0 && receipt.Time - previous.Time > mode)
          _firstOrdinals[mode] = receipt.FirstOrdinal;
        _last[mode] = receipt;
      }
      Last = receipt;
    }
  }
}

// Each lane keeps its own ordinal endpoints; both share the batch's receipt time.
internal readonly record struct MeterReceipt(double Time, int DamageFirst, int DamageThrough,
  int TankingFirst, int TankingThrough, int MinimumMode = 0)
{
  internal MeterReceipt(MeterArrival damage, MeterArrival tanking, int minimumMode = 0)
    : this(Math.Max(damage.Time, tanking.Time), damage.FirstOrdinal, damage.Through,
      tanking.FirstOrdinal, tanking.Through, minimumMode) { }

  internal MeterArrival Damage => DamageThrough == 0 ? MeterArrival.None : new(DamageThrough, Time, DamageFirst);
  internal MeterArrival Tanking => TankingThrough == 0 ? MeterArrival.None : new(TankingThrough, Time, TankingFirst);
  internal int Through => Math.Max(DamageThrough, TankingThrough);
  internal MeterArrival Both => DamageThrough == 0 ? Tanking : TankingThrough == 0 ? Damage
    : new(Through, Time, Math.Min(DamageFirst, TankingFirst));
}
