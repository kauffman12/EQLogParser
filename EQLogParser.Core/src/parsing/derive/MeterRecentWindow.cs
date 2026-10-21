using System;
using System.Collections.Generic;

namespace EQLogParser;

/*
 * Six second-labelled buckets of counted damage for one life and direction, including the current partly elapsed one and
 * always divided by six. A bucket keeps its absolute .NET-epoch second because modulo six only picks storage: adds outside
 * [edge - 5, edge] are refused BEFORE a bucket is touched, and `SumInto` asks that edge question itself instead of trusting
 * that somebody called `Advance` first. The edge is LOG time — the newest second the meter read — never the wall clock.
 * docs/DesignNotes.md → "Six-second RDPS: per-life buckets beside the meter's own amounts".
 */
internal sealed class MeterRecentWindow
{
  internal const int WindowSeconds = 6;

  // No usable window second. A window with no edge holds nothing and sums nothing.
  internal const long NoSecond = long.MinValue;

  private readonly Bucket[] _buckets = new Bucket[WindowSeconds];

  internal MeterRecentWindow()
  {
    for (var i = 0; i < WindowSeconds; i++) _buckets[i] = new Bucket();
  }

  // The first whole second at or before a captured time; non-finite means "no usable second".
  internal static long SecondOf(double timeS) => double.IsFinite(timeS) ? (long)Math.Floor(timeS) : NoSecond;

  internal static bool HasWindow(long edge) => edge != NoSecond;

  // Age against the window's right edge, including on an update that read nothing: expiry IS the answer.
  internal void Advance(long edge)
  {
    if (edge == NoSecond)
    {
      Clear();
      return;
    }

    var oldest = edge - (WindowSeconds - 1);
    for (var i = 0; i < _buckets.Length; i++)
      if (_buckets[i].Second < oldest) _buckets[i].Clear();
  }

  internal void Add(string name, string owner, double timeS, long amount, long edge)
  {
    if (edge == NoSecond || amount <= 0 || !double.IsFinite(timeS)) return;
    var second = SecondOf(timeS);
    // Refused before touching a bucket: an out-of-window stamp must not rotate a live one out.
    if (second > edge || second < edge - (WindowSeconds - 1)) return;

    var bucket = _buckets[SlotOf(second)];
    if (bucket.Second != second)
    {
      bucket.Clear();
      bucket.Second = second;
    }

    var amounts = bucket.Amounts ??= new Dictionary<(string Name, string Owner), long>();
    amounts.TryGetValue((name, owner), out var prior);
    amounts[(name, owner)] = prior + amount;
  }

  internal void SumInto(Dictionary<(string Name, string Owner), long> totals, long edge)
  {
    if (edge == NoSecond) return;
    var oldest = edge - (WindowSeconds - 1);
    for (var i = 0; i < _buckets.Length; i++)
    {
      // Stale labels are skipped here as well as cleared by Advance: the sum must not depend on somebody aging first.
      if (_buckets[i].Second < oldest || _buckets[i].Second > edge) continue;
      var amounts = _buckets[i].Amounts;
      if (amounts is null) continue;
      foreach (var pair in amounts)
      {
        totals.TryGetValue(pair.Key, out var prior);
        totals[pair.Key] = prior + pair.Value;
      }
    }
  }

  internal void Clear()
  {
    for (var i = 0; i < _buckets.Length; i++) _buckets[i].Clear();
  }

  private static int SlotOf(long second)
  {
    var slot = (int)(second % WindowSeconds);
    return slot < 0 ? slot + WindowSeconds : slot;
  }

  private sealed class Bucket
  {
    // A sentinel second keeps an unused bucket empty without allocating its dictionary.
    internal long Second = NoSecond;
    internal Dictionary<(string Name, string Owner), long> Amounts;

    internal void Clear()
    {
      Second = NoSecond;
      Amounts?.Clear();
    }
  }
}

/*
 * The detached rolling answer for one direction, keyed by the FINAL row name (pet folding already applied): overlay
 * presentation only — RDPS is informational, not a ranking mode. A class rather than a record because ByRow is compared by
 * reference and mutating it after publication would be a bug, so it claims no value equality.
 *
 * No second clock: WindowSecond is the newest SECOND THE CAPTURE HOLDS, so once the lines stop nothing ages out and this
 * answer stands still exactly like the encounter columns beside it. Retiring a quiet board is the overlay lifecycle's job.
 */
internal sealed class MeterDirectionRates
{
  // Empty publications share one row map; publication still needs its temporary aggregation maps.
  internal static readonly IReadOnlyDictionary<string, long> EmptyByRow = new Dictionary<string, long>(StringComparer.Ordinal);

  internal static readonly MeterDirectionRates None = new(EmptyByRow, 0L, MeterRecentWindow.NoSecond);

  internal MeterDirectionRates(IReadOnlyDictionary<string, long> byRow, long total, long windowSecond)
  {
    ByRow = byRow;
    Total = total;
    WindowSecond = windowSecond;
  }

  internal IReadOnlyDictionary<string, long> ByRow { get; }
  internal long Total { get; }
  internal long WindowSecond { get; }

  internal long RateFor(string rowName) =>
    rowName is not null && ByRow.TryGetValue(rowName, out var rate) ? rate : 0L;
}
