using System;
using System.Globalization;

namespace EQLogParser
{
  /* Shared text formatting for the FCT renderers (WPF vector and SkiaSharp backends). */
  internal static class FctText
  {
    /* Formats a hit value the way NAG's toShorthandString does: 1,234 / 12.5k / 123k / 1.5m. */
    internal static string FormatHitValue(double value)
    {
      var v = (uint)Math.Round(value);
      if (v < 10_000)
      {
        return v.ToString("N0", CultureInfo.InvariantCulture);
      }

      if (v < 100_000)
      {
        return $"{Math.Round(v / 1000.0, 1)}k";
      }

      if (v < 1_000_000)
      {
        return $"{Math.Round(v / 1000.0, 0)}k";
      }

      return $"{Math.Round(v / 1_000_000.0, 1)}m";
    }
  }
}
