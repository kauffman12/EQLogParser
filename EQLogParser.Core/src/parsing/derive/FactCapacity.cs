using System;

namespace EQLogParser
{
  /*
   * How much room the fact arrays should start with, from the size of the file about to be read.
   *
   * Both tables grow by doubling (that law is pinned: `HealFactCaptureTest`, and the slack it leaves is reclaimed
   * by `CombatCapture.CompactRows` at end of file). Doubling is the right policy when you cannot see ahead — a live
   * tail, a socket, anything without a length — but a log file HAS a length, and paying for it in overshoot is
   * choice: on the 998 MB capture `eqlog_Kizant_xegony-09-03-26.txt` the arrays ended up holding 4,832,102 damage
   * facts in 8,388,608 slots and 2,670,809 heals in 4,194,304, i.e. **about 162 MB of address space holding
   * nothing at the moment the load finished**, plus roughly 480 MB of memcpy across the doublings that got there.
   * Both tables' constructors already say a caller may estimate from file size; this is that estimate.
   *
   * The densities come from two captures of the same server seven months apart, which is why one pair of constants
   * is trusted:
   *
   * | capture | bytes | damage facts | heal facts |
   * |---|---|---|---|
   * | `eqlog_Kizant_xegony-2.txt` | 467 MB | 2,286,368 (1 per 204 B) | 1,247,984 (1 per 374 B) |
   * | `eqlog_Kizant_xegony-09-03-26.txt` | 998 MB | 4,832,102 (1 per 206 B) | 2,670,809 (1 per 374 B) |
   *
   * `MarginPercent` then decides which error is cheaper. Undershooting by a hair is the WORST outcome — one fact
   * past capacity doubles the array, so a 1 % miss costs 100 % more slots; overshooting costs exactly the overshoot,
   * and the end-of-file compaction hands most of that back. Hence +15 %, not −0 %.
   *
   * The clamp is about a different failure: a hint is a guess, and a guess about a file nobody has looked at (a
   * multi-gigabyte archive, a bogus length from a redirect) must not be allowed to precommit gigabytes. Past the
   * ceiling the table starts at the ceiling and doubles like it always did — the same behaviour as no hint at all.
   */
  internal static class FactCapacity
  {
    // Measured: one damage fact per ~205 bytes of log, one heal fact per ~375. Rounded to the conservative side of
    // each measurement (more bytes per fact = fewer facts estimated) before the margin is applied.
    private const long BytesPerDamageFact = 210;
    private const long BytesPerHealFact = 380;

    // Which way to be wrong: see the header. 15 % covers the spread between the two captures several times over.
    private const int MarginPercent = 15;

    // Precommit ceilings, in slots: 8,388,608 damage rows = 256 MB, 4,194,304 heal rows = 128 MB.
    internal const int MaxDamageSlots = 8_388_608;
    internal const int MaxHealSlots = 4_194_304;

    // What the tables construct themselves when nobody hands them a number (`DamageFactTable`/`HealFactTable`).
    internal const int DefaultDamageSlots = 65_536;
    internal const int DefaultHealSlots = 16_384;

    /// <summary>Starting capacity for the damage table, or the table's own default when there is nothing to size from.</summary>
    internal static int DamageForBytes(long bytes) => For(bytes, BytesPerDamageFact, MarginPercent, DefaultDamageSlots, MaxDamageSlots);

    /// <summary>Starting capacity for the heal table, or the table's own default when there is nothing to size from.</summary>
    internal static int HealForBytes(long bytes) => For(bytes, BytesPerHealFact, MarginPercent, DefaultHealSlots, MaxHealSlots);

    private static int For(long bytes, long bytesPerFact, int marginPercent, int minSlots, int maxSlots)
    {
      if (bytes <= 0 || bytesPerFact <= 0) return minSlots;

      // Percent first, division second: doing it the other way round loses the margin to integer truncation on small
      // files, and a rounding order that only shows up under a megabyte is the kind nobody notices until a test log fails.
      var estimate = bytes * (100 + marginPercent) / 100 / bytesPerFact;
      if (estimate <= minSlots) return minSlots;
      return (int)Math.Min(estimate, maxSlots);
    }
  }
}
