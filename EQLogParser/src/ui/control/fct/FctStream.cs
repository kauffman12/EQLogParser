using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * Row discipline for the parabola in halves — what MSBT actually is: not scattered flying numbers but a stream of
   * rows running down one centre column, each new line taking its place behind the last. The constants are theirs
   * (minimum spacing between lines and between columns); everything else is reuse rather than a second engine:
   * launch points go through FctLayout.Spawn like any placement candidate, and FctPlacement's flight-scored search
   * picks among them with its pad raised so "free" means MSBT's gap apart, not merely not touching.
   *
   * Most spacing costs nothing to enforce because the parabola scrolls at constant speed: two rows born half a second
   * apart stay half a second of travel apart for their whole lives — which also required taking the parabola's park
   * away (FctIngest.AssignLifetime): rows that rest at the end of the travel all rest at the same place, and a queue
   * that shares a parking space is not a stream. Only bursts need room made for them, and the way room is made here
   * is the way MSBT's areas look when text arrives faster than it scrolls: the centre column, two emergency columns
   * braided upstream of the drift, then — past three simultaneous rows — accept the least-bad overlap rather than
   * drop a number. A parser overlay losing your damage because the layout is busy is not a trade the genre ever had
   * to make (its text was already throttled upstream); here it never gets made.
   */
  internal static class FctStream
  {
    /* MSBT's MIN_VERTICAL_SPACING / MIN_HORIZONTAL_SPACING (MSBTData.lua): lines keep 8 px between blocks and
     * columns 10 px. Fixed pixels, not window fractions — the gap is about what reads as two separate rows. */
    public const double MinRowGap = 8;
    public const double ColumnGap = 10;

    /* Place one row of the stream: score this side's three columns at its spawn edge and return whichever reads
     * cleanest, always one of them — a parabola number never arrives off its column. */
    internal static FctHitState Place(FctHitState hit, List<FctHitState> hits, FctStage stage, Random rand)
    {
      var region = stage.RegionFor(hit.Incoming);
      var cx = region.X + (region.Width / 2);
      var edgeY = stage.UpFor(hit.Incoming) > 0 ? hit.BandMaxY : hit.BandMinY;

      var widest = hit.ValueWidth;
      var price = DrawnHalf(hit);
      for (var i = 0; i < hits.Count; i++)
      {
        // only this side's numbers can crowd this side's stream; the other half is a different region entirely
        if (hits[i].Incoming != hit.Incoming)
        {
          continue;
        }

        widest = Math.Max(widest, hits[i].ValueWidth);
        price = Math.Max(price, DrawnHalf(hits[i]));
      }

      var step = widest + ColumnGap;

      /*
       * Centre first — a tie breaks toward it because the scoring keeps the first equal cost, which is what makes an
       * empty stream one column rather than a coin flip — then two emergency columns braided UPSTREAM of the drift.
       * Every row in this half sweeps outward for its whole life; a column placed downstream of that sweep parks near
       * the wall the centre row is still travelling toward, and the two collide at the clamp no matter how carefully
       * their spawn rows were spaced. Against the drift they stay parallel to everything and touch nothing.
       *
       * The row y is the edge itself with no depth jitter: along-travel placement belongs to birth time and scroll
       * speed, not to dice. Column distance is the widest live text in this region plus the horizontal gap, so two
       * rows one step apart cannot touch however wide they are drawn, and a resize mid-stream needs no special case
       * because every later spawn measures the current widths anyway.
       */
      var braid = hit.Bow >= 0 ? -1.0 : 1.0;

      /*
       * Two steps must fit between the centre and that side's clamp wall, priced at the widest any row here can be
       * drawn (crits blow out; a column parked half off-screen is not a column). Where the territory is narrower than
       * the text demands — ordinary fight, ordinary font, somebody's 420 px overlay — the columns compress evenly:
       * adjacent rows graze a little and the scorer still separates them as far as geometry allows. Compression is
       * visible; pinning every emergency column onto the same wall clamp is worse, and dropping numbers is not on the table.
       */
      var span = braid < 0 ? cx - (hit.SideMin + price) : (hit.SideMax - price) - cx;
      if (span < 2 * step)
      {
        step = Math.Max(0, span) / 2;
      }

      var origins = new (double X, double Y)[]
      {
        (cx, edgeY),
        (cx + (braid * step), edgeY),
        (cx + (2 * braid * step), edgeY),
      };

      // the anchor is the stream's own centre, so the drift preference pulls rows back to the middle lane rather
      // than to wherever this hit's free throw happened to land
      return FctPlacement.PlaceOrigins(hit, hits, stage, rand, origins, cx, edgeY, MinRowGap);
    }

    /* Half-width a row can occupy at its widest draw — which for a stream is a crit's peak, since that is the only
     * way an ordinary number gets wide (pulse never runs here; blowout is the crit pop, priced the same way
     * FctLayout.Spawn prices its own side clamp). */
    private static double DrawnHalf(FctHitState h) => (h.ValueWidth * (h.Blowout ? FctMotion.CritPeakScale : 1.0)) / 2.0;
  }
}
