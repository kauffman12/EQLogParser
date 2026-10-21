using System;

namespace EQLogParser;

/*
 * Where the damage meter's Setup companion sits, in native SCREEN PIXELS — window rectangles, work areas, drag deltas and gaps all
 * of them, including negative desktop coordinates on a monitor left of the primary. Never a mix: a DIP width subtracted from a pixel
 * boundary is how the panel used to jump to the primary monitor and land half off a secondary one, because SystemParameters.WorkArea
 * is the PRIMARY work area in DIPs. The caller converts the panel's own DIP size through the STAGE's DPI (the monitor it is about to
 * land on) and every screen figure comes from MonitorFromWindow; both halves below stay pure arithmetic so they are headless-tested.
 * docs/DesignNotes.md → "Damage meter column switches: four hideable numbers, zero width when off".
 */
internal static class MeterPanelPlacement
{
  /*
   * Opening dock: right of the stage when this monitor's work area has room there, left of it when it doesn't, then clamped so the
   * panel keeps a reachable corner even on a work area too narrow for either side (the left edge wins over the ideal gap). Vertical
   * follows the stage's top and clamps up from the bottom rather than scrolling the panel off screen.
   */
  internal static (int Left, int Top) PlaceBesideStage(int stageLeft, int stageRight, int stageTop, int panelWidth,
    int panelHeight, int gap, int workLeft, int workTop, int workRight, int workBottom)
  {
    var left = stageRight + gap;
    if (left + panelWidth > workRight) left = stageLeft - panelWidth - gap;

    return (Math.Clamp(left, workLeft, Math.Max(workLeft, workRight - panelWidth)),
      Math.Clamp(stageTop, workTop, Math.Max(workTop, workBottom - panelHeight)));
  }

  // All inputs are screen pixels, including negative desktop coordinates. A narrowing stage cannot cover its companion.
  internal static int FollowWidth(int panelLeft, int panelWidth, int stageLeft, int growth, int workLeft, int workRight)
  {
    if (growth <= 0 || panelLeft < stageLeft) return panelLeft;

    return Math.Clamp(panelLeft + growth, workLeft, Math.Max(workLeft, workRight - panelWidth));
  }

  /*
   * A follow moves by the drag delta and then keeps its bottom edge on the monitor it is following, exactly as the opening dock does.
   * Without this half the panel trails off the taskbar's bottom while the stage is dragged down — a delta has no opinion about where
   * the screen ends. The caller passes the work area of whichever window the pair now shares, so a drag onto a second monitor clamps
   * against that one rather than the monitor it left.
   */
  internal static int FollowTop(int panelTop, int panelHeight, int workTop, int workBottom)
    => Math.Clamp(panelTop, workTop, Math.Max(workTop, workBottom - panelHeight));
}
