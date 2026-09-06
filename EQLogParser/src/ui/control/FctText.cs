using System;
using System.Globalization;

namespace EQLogParser
{
  /* Shared text formatting for the FCT renderers (SkiaSharp and WPF vector backends). */
  internal static class FctText
  {
    /*
     * Formats a hit value the way NAG's toShorthandString does: 1,234 / 12.5k / 123k / 1.5m. Always
     * invariant-culture: "12,5k" from a German locale is not what a combat number should read like.
     */
    internal static string FormatHitValue(double value)
    {
      var v = (long)Math.Round(value);
      if (v < 10_000)
      {
        return v.ToString("N0", CultureInfo.InvariantCulture);
      }

      if (v < 100_000)
      {
        // rounding to one decimal carries at the top of the band (99,960 -> 100.0k), so drop the decimal there
        var tenths = Math.Round(v / 1000.0, 1);
        return Shorten(tenths < 100 ? tenths : Math.Round(v / 1000.0), "k");
      }

      if (v < 1_000_000)
      {
        // same carry one tier up: 999,500 must read 1m, not 1000k
        var thousands = Math.Round(v / 1000.0);
        return thousands < 1000 ? Shorten(thousands, "k") : Shorten(Math.Round(v / 1_000_000.0, 1), "m");
      }

      return Shorten(Math.Round(v / 1_000_000.0, 1), "m");
    }

    /* "0.#" keeps 12k looking like 12k and 12.5k looking like 12.5k. */
    private static string Shorten(double value, string suffix) => value.ToString("0.#", CultureInfo.InvariantCulture) + suffix;
  }
}
