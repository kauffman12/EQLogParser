using System;

namespace EQLogParser;

/// <summary>A cell position in the numeric block, left to right; visibility switches belong to MeterColumn.</summary>
internal enum MeterSlot
{
  Percent,
  Damage,
  Dps,
  Rdps,

  /// <summary>The raid-time rate, drawn to the right of the rolling one so the two rates sit together and elapsed seconds stays last.</summary>
  Sdps,
  Seconds,
}

/*
 * Header and rows share fixed per-font widths and right insets; hidden cells have zero width, not merely collapsed text.
 * Negative insets may extend into the next cell, so neighbour visibility matters. Unknown slots must not silently default.
 * docs/DesignNotes.md → "Damage meter column switches: hiding one reserves no width".
 */
internal sealed class MeterColumnLayout
{
  // Icon plus ~six row-font characters. A round allowance, not a promise of exactly this many free DIPs: it is also what the
  // window's own border and the header's right margin come out of (see MinimumWindowWidthFor).
  internal const double NameAreaMinWidthDip = 96;
  internal const int MeterSlotCount = 6;

  private readonly double[] _width = new double[MeterSlotCount];

  internal MeterColumnLayout(int fontPt, MeterColumn shown)
  {
    var preset = PresetFor(fontPt);

    // The table owns which cell each switchable column draws in, so an off column costs that cell its width and nothing else moves.
    foreach (var column in MeterColumns.All)
    {
      _width[(int)column.Slot] = MeterColumns.Includes(shown, column.Bit) ? PresetWidth(column.Slot, preset) : 0;
    }

    _width[(int)MeterSlot.Seconds] = preset.Narrow;

    var total = 0.0;
    for (var i = 0; i < MeterSlotCount; i++)
    {
      total += _width[i];
    }

    NumericWidth = total;
    FontPt = fontPt;
  }

  /// <summary>The row font these widths were picked for.</summary>
  internal int FontPt { get; }

  /// <summary>The header's own font: exactly one point over the rows, at every shipped size.</summary>
  internal int HeaderFontPt => FontPt + 1;

  /// <summary>DIPs this column occupies; zero when it is switched off.</summary>
  internal double WidthOf(MeterSlot slot) => _width[(int)slot];

  /// <summary>Whether a switched-off column draws: no width means no text and no inset.</summary>
  internal bool Shows(MeterSlot slot) => _width[(int)slot] > 0;

  /// <summary>How far past its cell edge this column's right-aligned text ends; zero when the column is off.</summary>
  internal double InsetOf(MeterSlot slot) => Shows(slot) ? InsetPreset(slot) : 0;

  /// <summary>DIPs the visible numeric block costs, excluding names and icons.</summary>
  internal double NumericWidth { get; }

  /// <summary>Chrome-free floor for the visible numeric block plus the name allowance, without a live toolbar.</summary>
  internal double MinimumWindowWidth => NumericWidth + NameAreaMinWidthDip;

  /// <summary>Live floor reserving the larger of name space and measured toolbar space, without counting chrome twice.</summary>
  internal double MinimumWindowWidthFor(double toolbarWidth, double horizontalChrome)
  {
    if (!double.IsFinite(toolbarWidth) || toolbarWidth <= 0
      || !double.IsFinite(horizontalChrome) || horizontalChrome < 0) return MinimumWindowWidth;

    const double gap = 4;
    return NumericWidth + Math.Max(NameAreaMinWidthDip, toolbarWidth + horizontalChrome + gap);
  }

  // An unnamed slot is a programmer error, unlike a bad configured font size. MeterColumnLayoutTest pins every drawn cell.
  private static double PresetWidth(MeterSlot slot, (double Narrow, double Wide, double Rolling) preset) => slot switch
  {
    MeterSlot.Damage or MeterSlot.Dps => preset.Wide,
    MeterSlot.Rdps => preset.Rolling,
    MeterSlot.Sdps => preset.Rolling,
    MeterSlot.Percent or MeterSlot.Seconds => preset.Narrow,
    _ => throw new ArgumentException($"MeterSlot {slot} has no width preset", nameof(slot)),
  };

  // Insets share the width table's refusal of unnamed cells.
  private static double InsetPreset(MeterSlot slot) => slot switch
  {
    MeterSlot.Percent => -13,
    MeterSlot.Damage => -12,
    MeterSlot.Dps => -6,
    MeterSlot.Rdps => -6,
    MeterSlot.Sdps => -6,
    MeterSlot.Seconds => 2,
    _ => throw new ArgumentException($"MeterSlot {slot} has no right inset", nameof(slot)),
  };

  // (percent/seconds, damage/dps, rolling) per shipped font size. An unreadable size lays out like the biggest one rather than
  // throwing on the UI path: a bad config value is not a reason to lose a meter.
  private static (double Narrow, double Wide, double Rolling) PresetFor(int fontPt) => fontPt switch
  {
    10 => (40, 50, 46),
    12 => (45, 60, 52),
    14 => (50, 70, 60),
    _ => (55, 75, 66),
  };
}
