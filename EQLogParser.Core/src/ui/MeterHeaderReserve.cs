using System;

namespace EQLogParser
{
  // Saved meter height already includes the baseline header. Reserve only the toolbar's excess, once per window.
  internal sealed class MeterHeaderReserve
  {
    private double _extra;

    internal double Update(double windowHeight, double headerHeight, double toolbarHeight)
    {
      if (!double.IsFinite(windowHeight) || windowHeight <= 0
        || !double.IsFinite(headerHeight) || headerHeight < 0
        || !double.IsFinite(toolbarHeight) || toolbarHeight <= 0) return windowHeight;

      var extra = Math.Max(0, toolbarHeight - headerHeight);
      var adjusted = windowHeight + (extra - _extra);
      if (!double.IsFinite(adjusted) || adjusted <= 0) return windowHeight;
      _extra = extra;
      return adjusted;
    }
  }
}
