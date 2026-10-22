using System;

namespace EQLogParser;

// One cut per supported quiet period, independent of which mode a window currently displays.
internal sealed class MeterReceiptRuns
{
  internal const int MaxMode = 100;
  private readonly int[] _firstOrdinals;

  internal MeterArrival Last { get; private set; } = MeterArrival.None;

  internal MeterReceiptRuns() => _firstOrdinals = new int[MaxMode + 1];

  private MeterReceiptRuns(MeterReceiptRuns source)
  {
    Last = source.Last;
    _firstOrdinals = (int[])source._firstOrdinals.Clone();
  }

  internal MeterReceiptRuns Copy() => new(this);

  internal MeterArrival ForMode(int mode)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(mode);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(mode, MaxMode);
    return Last with { FirstOrdinal = _firstOrdinals[mode] };
  }

  internal void Add(MeterReceipt receipt)
  {
    if (Last.Through > 0)
    {
      var gap = receipt.Time - Last.Time;
      for (var mode = 1; mode <= MaxMode && gap > mode; mode++)
        _firstOrdinals[mode] = receipt.FirstOrdinal;
    }
    Last = new MeterArrival(receipt.Through, receipt.Time);
  }
}

internal readonly record struct MeterReceipt(int FirstOrdinal, int Through, double Time);
