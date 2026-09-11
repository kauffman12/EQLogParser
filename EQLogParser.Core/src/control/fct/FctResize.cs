using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * What happens when the overlay changes size.
   *
   * Two separate jobs, in one file because they are one subject: which sizes a drag settles on, and what becomes of the numbers
   * already in flight when the window moves to one of them.
   *
   * The offered sizes are a magnet, not a menu. Drag freely and the axes snap when they come near an offered value, so a precise
   * drag can still land anywhere legal and a sloppy one lands somewhere sensible. They snap per axis rather than as whole presets:
   * dragging a single edge gets the same help as dragging a corner, and a corner dragged near 800x560 settles exactly on it. The
   * combinations in between are legal too — every size down to MinWidth/MinHeight keeps every style inside the window and out
   * of the protected strip, which was probed at 980x640, 900x600, 700x520, 560x420, 460x360 and 420x300 rather than assumed.
   *
   * 980x640 is the size the layout was designed and measured at. The three smaller ones exist because that is wider than most
   * people need over a HUD, and 720 is as narrow as the lane columns stay comfortably apart: damage sits at 0.42 of the width and
   * healing at 0.63, so under ~700 a wide crit starts touching its neighbour column.
   */
  internal static class FctResize
  {
    /*
     * The offered sizes start wide now, because the narrow end of the range spends the thing the overlay needs most: room beside a
     * number for the name of whatever did it. At 980 in split a source line has a quarter column to live in — about a hundred pixels
     * after the digits and their mark — and every name longer than a handful of letters is shortened to fit. At 1280 that same column
     * carries a mob's whole name in most fights, and halves at 1280 carries a name beside a crit with room left over. The old sizes stay
     * offered for anyone playing on a small screen or beside another window: the range grew at the top, it did not move.
     *
     * Nothing here is a maximum. Fit() clamps to the working area it is given, so a second monitor offers more and a netbook less; these
     * are the sizes a corner drag snaps TO, and the largest of them is what a first run asks for (FctOverlayWindow's defaults) before that
     * clamp. "720 is as narrow as lane columns stay comfortably apart" is still true — it is just no longer the widest thing on offer.
     */
    public static readonly double[] WidthSnaps = [1920, 1760, 1600, 1440, 1280, 1120, 980, 800, 760, 720];
    public static readonly double[] HeightSnaps = [1080, 960, 900, 820, 720, 640, 560, 520];

    /* The smallest probed layout: at 420x300 every style still places every number inside the window and clear of the strip.
     * Smaller than this and a band is shallower than a line of text, which is not a layout, it is an overlap with extra steps. */
    public const double MinWidth = 420;
    public const double MinHeight = 300;

    /* How near an offered size has to be to take the drag. The offered values are 40 apart at their closest, so 16 keeps a
     * strip of free space between every pair — the magnet helps without taking the decision away. */
    public const double SnapTolerance = 16;

    /*
     * Clamp a proposed size to what the layout can use and to the screen, then let the offered sizes pull on it. Both axes are
     * answered together because the caller clamps against one working area anyway; nothing here knows about WPF types so the
     * arithmetic is testable without a window.
     */
    internal static void Fit(double width, double height, double maxWidth, double maxHeight,
      out double fittedWidth, out double fittedHeight)
    {
      fittedWidth = Nearest(Math.Clamp(width, MinWidth, Math.Max(MinWidth, maxWidth)), WidthSnaps);
      fittedHeight = Nearest(Math.Clamp(height, MinHeight, Math.Max(MinHeight, maxHeight)), HeightSnaps);
    }

    /* The offered value closest to a dragged size, or the dragged size itself when nothing is within tolerance. */
    private static double Nearest(double value, double[] snaps)
    {
      var best = value;
      var bestGap = SnapTolerance;

      for (var i = 0; i < snaps.Length; i++)
      {
        var gap = Math.Abs(snaps[i] - value);
        if (gap > bestGap)
        {
          continue;
        }

        bestGap = gap;
        best = snaps[i];
      }

      return best;
    }

    /*
     * Move the numbers already in flight to the new size.
     *
     * They carry geometry measured against the window they were born in: band edges, side bounds, travel distance and cell
     * position are all pixels, and motion is a pure function of (hit, age) with no canvas argument — so a resize that touches
     * nothing leaves them drawing where the old window used to be. Probed at 980x640 shrunk to 620x400: 10 of 15 held numbers
     * drawn outside the overlay, 4 more sitting in the protected strip, and pulse was worse than the rest because its grid was
     * the one thing calculated once. It healed itself as hits expired, which is a way of being wrong politely, not a fix.
     *
     * Free text scales by the ratio of each axis: a thrown number keeps the same shape relative to the window it is thrown in.
     * A number holding a pulse cell is not scaled but re-seated, because cells come out of the grid for the current size rather
     * than from each other — its index survives (that is what an index is for) and where that index now sits is the grid's
     * business, see FctCellGrid.Reseat.
     *
     * Font sizes are not touched: how big a number is drawn is a style decision, not a layout one. Shrinking therefore means
     * proportionally less room per number, which is what the lane cap and the life shortener absorb rather than smaller type —
     * and FctLayout's and FctMotion's clamps are why this can be a mapping instead of a rebuild.
     */
    internal static void Rescale(List<FctHitState> hits, double oldW, double oldH, double w, double h)
      => Rescale(hits, oldW, oldH, FctStage.Bands(w, h));

    /*
     * Halves needs no special case here: the regions are fixed fractions of the canvas, so a proportional map sends each
     * half onto its new self and nothing crosses the seam — a number's side is decided by its lane at spawn, never by where
     * it happens to be. What does change with the size is everything derived from it, which Refit and Reseat re-derive.
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
        hit.Arc *= sx;
        hit.Bow *= sx; // the bow is a territory share too: mapped the same way, so a parabola ends in its new half

        /* A rail row rescaled its road; the rate it travels that road at is the one promise split does not resize.
         * Restamp from the new travel so stretching the window buys a row more time, not more speed. */
        FctIngest.FinalizeRailTempo(hit);
      }
    }
  }
}