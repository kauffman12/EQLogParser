using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * What happens when the overlay changes size.
   *
   * A resize is free: the drag goes exactly where the hand stops it, clamped only to what the layout can draw in and to the
   * screen. Every size down to MinWidth/MinHeight keeps every style inside the window and out of the protected strip, which was
   * probed at 980x640, 900x600, 700x520, 560x420, 460x360 and 420x300 rather than assumed. (The overlay used to settle drags onto a
   * short list of offered sizes; the magnet was deleted because it made resizing jump, and the setup panel's position fields can
   * name any size exactly anyway.) The rest of this file is the harder half: what becomes of the numbers already in flight when
   * the window moves.
   */
  internal static class FctResize
  {
    /* The smallest probed layout: at 420x300 every style still places every number inside the window and clear of the strip.
     * Smaller than this and a band is shallower than a line of text, which is not a layout, it is an overlap with extra steps. */
    public const double MinWidth = 420;
    public const double MinHeight = 300;

    /*
     * Clamp a proposed size to what the layout can draw in and to the screen. Both axes are answered together because the caller
     * clamps against one working area anyway; nothing here knows about WPF types so the arithmetic is testable without a window.
     */
    internal static void Fit(double width, double height, double maxWidth, double maxHeight,
      out double fittedWidth, out double fittedHeight)
    {
      fittedWidth = Math.Clamp(width, MinWidth, Math.Max(MinWidth, maxWidth));
      fittedHeight = Math.Clamp(height, MinHeight, Math.Max(MinHeight, maxHeight));
    }

    /*
     * Move the numbers already in flight to the new size.
     *
     * They carry geometry measured against the window they were born in: band edges, side bounds, travel distance and cell
     * position are all pixels, and motion is a pure function of (hit, age) with no canvas argument — so a resize that touches
     * nothing leaves them drawing where the old window used to be. Probed at 980x640 shrunk to 620x400: 10 of 15 held numbers
     * drawn outside the overlay and 4 more sitting in the protected strip. It healed itself as hits expired, which is a way of
     * being wrong politely, not a fix.
     *
     * Free text scales by the ratio of each axis: a thrown number keeps the same shape relative to the window it is thrown in.
     * Font sizes are not touched: how big a number is drawn is a style decision, not a layout one. Shrinking therefore means
     * proportionally less room per number, which is what the lane cap and the life shortener absorb rather than smaller type —
     * and FctLayout's and FctMotion's clamps are why this can be a mapping instead of a rebuild.
     */
    internal static void Rescale(List<FctHitState> hits, double oldW, double oldH, double w, double h)
      => Rescale(hits, oldW, oldH, FctStage.Bands(w, h));

    /*
     * Split needs no special case here: the lanes are fixed fractions of the canvas, so a proportional map sends every lane onto
     * its new self and nothing crosses into a neighbour — which column a number belongs to is decided by its category at spawn,
     * never by where it happens to be standing. What does change with the size is everything derived from that size, which Refit
     * and Reseat re-derive.
     */
    internal static void Rescale(List<FctHitState> hits, double oldW, double oldH, FctStage stage)
    {
      if (hits is null || hits.Count == 0 || oldW <= 0 || oldH <= 0 || stage.W <= 0 || stage.H <= 0)
      {
        return;
      }

      var sx = stage.W / oldW;
      var sy = stage.H / oldH;
      if (Math.Abs(sx - 1) < 0.001 && Math.Abs(sy - 1) < 0.001)
      {
        return;
      }

      for (var i = 0; i < hits.Count; i++)
      {
        var hit = hits[i];
        var y0 = hit.Y0 * sy;

        FctLayout.Refit(hit, stage);

        /* A column that got wider can carry more name again, and one that shrank has to give some back: the fitted label is a decision
           against the room, so the room changing invalidates it. The canvas re-measures with real glyphs on the next frame. */
        if (hit.Source is not null)
        {
          hit.TextDirty = true;
        }


        hit.X0 *= sx;
        hit.Y0 = Math.Clamp(y0, hit.BandMinY, Math.Max(hit.BandMinY, hit.BandMaxY));
        hit.Rise *= sy;
        hit.FallDist *= sy;
        hit.Sway *= sx;
        hit.Bow *= sx; // the bow is a territory share too: mapped the same way, so an arc ends in its new half

        /* A rail row rescaled its road; the rate it travels that road at is the one promise split does not resize.
         * Restamp from the new travel so stretching the window buys a row more time, not more speed. */
        FctIngest.FinalizeRailTempo(hit);
      }
    }
  }
}