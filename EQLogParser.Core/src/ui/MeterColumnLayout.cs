using System;

namespace EQLogParser;

/// <summary>Where a number is drawn: one cell of the meter's numeric block, left to right. A position, not a switch — the mask of
/// which of these cells the user turned on is <see cref="MeterColumn"/>.</summary>
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
 * What the meter's numeric block costs: one fixed width per column per font, so digits never reflow as they cross 999 → 1K → 1M,
 * and names/icons get whatever the window has left. A hidden column's width is zero rather than collapsed text, because a
 * collapsed TextBlock inside a fixed-width cell still moves nothing out of the way. Widths come from the presets below — the
 * shipped table, measured on Windows — and never from content, so layout depends only on font size and the visible set.
 *
 * The insets are how far a right-aligned number is allowed to end past its own cell edge; they are what tightened the block
 * before headers shared it, and the header reuses them so each word's right edge sits on its digits' right edge at every font
 * instead of being nudged by a per-font table of margins. Both tables below name every MeterSlot they handle and refuse one they do
 * not (see PresetWidth), so a new cell cannot default its way into the block unseen. A negative inset therefore lands in whatever
 * cell comes NEXT, which is normally a neighbour's empty left margin — hide that neighbour and the overflow reaches the following
 * column instead (percent's 13
 * DIP past its own cell against wide DPS digits at 10 pt is the tightest case). That is a sight question for the Windows gate, not a
 * reason to move a number off its own cell. Minimum window width is the visible numeric block plus room for a name: a floor under
 * that draws names underneath the numbers. docs/DesignNotes.md →
 * "Six-second RDPS: per-life buckets beside the meter's own amounts".
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

    _width[(int)MeterSlot.Seconds] = preset.Narrow; // duration is not hideable and is not in the table

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

  /// <summary>
  /// The narrowest window that still leaves a name readable left of the numbers, with no toolbar to seat: the VISIBLE block plus
  /// the name allowance. This figure is chrome-free — it is what the Setup stage and a windowless meter ask for — while
  /// MinimumWindowWidthFor adds the caller's measured horizontal chrome to whichever branch wins. Both are floor figures, and the
  /// allowance is deliberately round rather than a measured label width.
  /// </summary>
  internal double MinimumWindowWidth => NumericWidth + NameAreaMinWidthDip;

  /// <summary>
  /// The live header must fit its measured toolbar beside, not over, the numeric block, so the floor is the wider of a name's room
  /// and the toolbar plus the window chrome and a gap. Chrome spent against a toolbar is not also spent against a name: when the
  /// name branch wins, the border comes out of the allowance, which is why NameAreaMinWidthDip is rounded up.
  /// </summary>
  internal double MinimumWindowWidthFor(double toolbarWidth, double horizontalChrome)
  {
    if (!double.IsFinite(toolbarWidth) || toolbarWidth <= 0
      || !double.IsFinite(horizontalChrome) || horizontalChrome < 0) return MinimumWindowWidth;

    const double gap = 4;
    return NumericWidth + Math.Max(NameAreaMinWidthDip, toolbarWidth + horizontalChrome + gap);
  }

  /*
   * Which of the three presets a cell draws from: percent and elapsed seconds share the narrow one, Damage and DPS share the wide
   * one, and the rolling rate has its own. Both tables below name every cell they handle and REFUSE anything else, so a new MeterSlot
   * fails the first meter open with its own name in the message rather than drawing itself narrow-and-flush into the block and being
   * found later as "the new column looks squished" on a Windows gate. `MeterColumnLayoutTest` pins the drawn block to exactly six
   * named cells, so the refusal is a programmer-error tripwire and no shipped mask can reach it — unlike an unreadable FONT size,
   * which is a state players do reach and therefore still lays out like the ceiling (PresetFor).
   */
  private static double PresetWidth(MeterSlot slot, (double Narrow, double Wide, double Rolling) preset) => slot switch
  {
    MeterSlot.Damage or MeterSlot.Dps => preset.Wide,
    MeterSlot.Rdps => preset.Rolling,
    MeterSlot.Sdps => preset.Rolling, // the same digit budget as the other rates: it is a share of what DPS shows, never wider
    MeterSlot.Percent or MeterSlot.Seconds => preset.Narrow,
    _ => throw new ArgumentException($"MeterSlot {slot} has no width preset", nameof(slot)),
  };

  // How far each cell's right-aligned text is allowed past its own edge, as shipped and measured on Windows. Refuses an unnamed cell
  // for the same reason PresetWidth does.
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
