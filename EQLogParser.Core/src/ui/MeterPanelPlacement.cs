using System;

namespace EQLogParser;

/*
 * Setup companion placement uses native screen pixels throughout, including negative desktop coordinates.
 * The caller converts panel size and gap from DIPs using the stage's DPI and supplies the relevant monitor's work area.
 * docs/DesignNotes.md → "Damage meter column switches: hiding one reserves no width".
 */
internal static class MeterPanelPlacement
{
  // Prefer the right, then the left; a reachable corner wins over the gap when neither side fits.
  internal static (int Left, int Top) PlaceBesideStage(int stageLeft, int stageRight, int stageTop, int panelWidth,
    int panelHeight, int gap, int workLeft, int workTop, int workRight, int workBottom)
  {
    var left = stageRight + gap;
    if (left + panelWidth > workRight) left = stageLeft - panelWidth - gap;

    return (Math.Clamp(left, workLeft, Math.Max(workLeft, workRight - panelWidth)),
      Math.Clamp(stageTop, workTop, Math.Max(workTop, workBottom - panelHeight)));
  }

  // Narrowing cannot cover the companion; a panel docked left does not follow right-edge growth.
  internal static int FollowWidth(int panelLeft, int panelWidth, int stageLeft, int growth, int workLeft, int workRight)
  {
    if (growth <= 0 || panelLeft < stageLeft) return panelLeft;

    return Math.Clamp(panelLeft + growth, workLeft, Math.Max(workLeft, workRight - panelWidth));
  }

  // Clamp after applying the drag delta so the panel cannot trail below its current monitor's work area.
  internal static int FollowTop(int panelTop, int panelHeight, int workTop, int workBottom)
    => Math.Clamp(panelTop, workTop, Math.Max(workTop, workBottom - panelHeight));
}
