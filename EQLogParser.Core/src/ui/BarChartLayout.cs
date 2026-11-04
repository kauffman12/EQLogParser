using System;

namespace EQLogParser;

internal readonly record struct BarChartAxis(long Maximum, long Step);
internal readonly record struct BarChartRange(int First, int End)
{
  internal int Count => End - First;
}
internal readonly record struct BarChartBounds(double Top, double Height, double PrimaryTop,
  double SecondaryTop, double LineHeight)
{
  internal double Bottom => Top + Height;
}

/// <summary>Physical-pixel-independent geometry for exact-category and percent bar charts.</summary>
internal static class BarChartLayout
{
  internal static BarChartAxis PercentAxis => new(100, 10);

  internal static BarChartAxis FrequencyAxis(int maximum)
  {
    var target = Math.Max(1L, ((long)maximum + 4) / 5);
    var power = 1L;
    while (power * 10 < target) power *= 10;
    var step = target <= power ? power : target <= 2 * power ? 2 * power
      : target <= 5 * power ? 5 * power : 10 * power;
    return new BarChartAxis(Math.Max(step, (((long)Math.Max(0, maximum) + step - 1) / step) * step), step);
  }

  internal static double Extent(int count, double stride)
  {
    var extent = Math.Max(0, count) * NonNegative(stride);
    return double.IsFinite(extent) ? extent : 0;
  }

  internal static double ClampOffset(double offset, double extent, double viewport)
  {
    var maximum = Math.Max(0, NonNegative(extent) - NonNegative(viewport));
    return double.IsNaN(offset) ? 0 : Math.Clamp(offset, 0, maximum);
  }

  internal static BarChartRange Visible(int count, double stride, double offset, double viewport, int buffer = 2)
  {
    if (count <= 0 || !double.IsFinite(stride) || stride <= 0 || NonNegative(viewport) == 0) return new(0, 0);
    var extent = Extent(count, stride);
    if (extent == 0) return new(0, 0);
    offset = ClampOffset(offset, extent, viewport);
    buffer = Math.Max(0, buffer);
    var first = (int)Math.Max(0, Math.Floor(offset / stride) - buffer);
    var end = (int)Math.Min(count, Math.Ceiling((offset + viewport) / stride) + buffer);
    return new(first, end);
  }

  internal static double PreservePosition(double offset, double oldStride, double newStride,
    double extent, double viewport)
    => ClampOffset(oldStride > 0 ? offset / oldStride * newStride : 0, extent, viewport);

  internal static BarChartBounds Bounds(double height, double fontSize, bool secondary)
  {
    height = NonNegative(height);
    var line = (double.IsFinite(fontSize) && fontSize > 0 ? fontSize : 12) * 1.5;
    if (!double.IsFinite(line)) line = 18;
    var top = Math.Min(height, line + 6);
    var plot = Math.Max(0, height - top - ((secondary ? 2 : 1) * line) - 8);
    var primary = Math.Min(height, top + plot + 4);
    return new(top, plot, primary, Math.Min(height, primary + line), line);
  }

  internal static double BarHeight(double value, long maximum, double plotHeight)
    => maximum > 0 && double.IsFinite(value)
      ? Math.Clamp(value / maximum, 0, 1) * NonNegative(plotHeight) : 0;

  internal static double WheelOffset(double offset, int delta, int lines, double stride,
    double viewport, double extent)
  {
    var distance = lines < 0 ? NonNegative(viewport) : Math.Max(0, lines) * NonNegative(stride);
    return ClampOffset(offset - (delta / 120.0 * distance), extent, viewport);
  }

  private static double NonNegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
