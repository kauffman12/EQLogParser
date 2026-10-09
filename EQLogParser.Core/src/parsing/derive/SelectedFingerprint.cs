using System;
using System.Collections.Generic;

namespace EQLogParser;

/*
 * "Content moved" is not "MY content moved" — and a live tail proved the difference costs the operator a blinking table.
 *
 * The fight pane re-announces its selection whenever the capture-wide stamp moves (captured facts folded with the identity
 * digest), because a verdict can change what the same facts MEAN. On a running log that stamp moves on EVERY pass — one new
 * fact anywhere in a 4,500-row capture is enough — so a selected mob that has been dead for ten minutes got its boards
 * materialized and re-presented once every derive pass (measured 2026-10-08 over a live-tailed capture: 157 rebuilds from
 * [ContentMoved] in a few minutes, one fight selected, damage summary clearing and reloading figures that never changed).
 * The CPU is small at one row; the FLICKER is the product.
 *
 * So ask the cheaper question first: did anything INSIDE the selected rows move? A row's displayed content is its fact span's
 * totals, its direction windows and its outcome flags — all readable off the row in O(1) each, so a selection costs O(rows),
 * which is what a selection already is. Facts landing on OTHER rows cannot change any of it. Verdicts can (the same facts
 * re-routing between `X` and `X +Pets`, charm taking a mob off the enemy list), and that term is separate and rare, so
 * `WorthRebuild` still says yes whenever a verdict moved, exactly as the capture-wide stamp always did.
 *
 * The fingerprint is deliberately commutative (a sum over per-row hashes): two rows trading places in a selection describe the
 * same boards, and a rebuild for that would be this class's own flicker bug. Per-row hashing — not arithmetic mixing of raw
 * fields — is what stops one row's +N cancelling another's -N into "nothing happened".
 */
internal static class SelectedFingerprint
{
  /*
   * One long for the whole selection: every figure a board under it would show. NaN means "this direction never happened" and
   * carries no information, so non-finite times fold as zero rather than as a payload that could differ between passes.
   */
  internal static long Of(IReadOnlyList<DerivedFight> rows)
  {
    if (rows is null) return 0;

    long sum = rows.Count;
    foreach (var row in rows)
    {
      if (row is null) continue;

      var h = Mix(0, row.Id);
      h = Mix(h, row.DamageTotal);
      h = Mix(h, row.DamageByOwner);
      h = Mix(h, row.DamageToOwner);
      h = Mix(h, row.TankTotal);
      h = Mix(h, Time(row.BeginTime));
      h = Mix(h, Time(row.LastTime));
      h = Mix(h, Time(row.LastDamageTime));
      h = Mix(h, Time(row.LastTankingTime));
      h = Mix(h, (row.Dead ? 7 : 0) + (row.CharmedOwned ? 13 : 0) + (row.RaidPet ? 29 : 0));

      sum = unchecked(sum + h);
    }

    return sum;
  }

  /*
   * The law: a pass-driven rebuild is owed when a verdict moved (the term no row can see) or when the selected rows themselves
   * moved. Both equal means every number on every board under this selection already reads correctly, and the announce that
   * would redraw it is the flicker.
   */
  internal static bool WorthRebuild(bool verdictsMoved, long current, long announced) => verdictsMoved || current != announced;

  /*
   * Seconds, or 0 for "never happened" (NaN) and "open-ended" (±∞), so a missing window cannot hash differently across passes.
   */
  private static long Time(double t) => double.IsFinite(t) ? BitConverter.DoubleToInt64Bits(t) : 0L;

  /*
   * Murmur3's finalizer, and it is load-bearing rather than decoration: a linear accumulator (h * k + value) made one row's +500 cancel
   * another row's −500 into an identical fingerprint, which OneRowsGainNeverCancelsAnotherRowsLoss caught the day it was written — and a
   * false "nothing moved" is exactly the failure this class must never have, because it leaves stale numbers on screen permanently. Every
   * field goes through a avalanche step so the only way two selections collide is by 2⁻⁶⁴ accident, not by arithmetic.
   */
  private static long Mix(long h, long value)
  {
    unchecked
    {
      h ^= value + unchecked((long)0x9e3779b97f4a7c15UL);
      h = (h ^ (h >> 30)) * unchecked((long)0xbf58476d1ce4e5b9UL);
      h = (h ^ (h >> 27)) * unchecked((long)0x94d049bb133111ebUL);
      return h ^ (h >> 31);
    }
  }
}
