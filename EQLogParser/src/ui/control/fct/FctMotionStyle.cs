namespace EQLogParser
{
  /*
   * How a hit moves over its life. Motion is presentation, not information: which region a number sits in and which way
   * it travels carry who acted, so every style below keeps that promise and none of them may put text in the protected
   * middle strip (in bands; halves has no strip because its regions do not overlap). Rationale:
   * docs/DesignNotes.md → "Five motion styles, and the one thing none of them may change".
   */
  internal enum FctMotionStyle
  {
    /* Travel away from the gap, decelerate to a stop, hold, fade. The default because it is the least demanding: the
     * number arrives, stays put where it can be read twice, and leaves. Everything else is a variation on that. */
    Hold,

    /* Overshoot, then accelerate under gravity while shrinking (mirrored upward on the incoming band). The most
     * legible "something happened" of the lot and the loudest: numbers cross a third of the overlay, which is also why
     * it is opt-in and why it fixes the life to the choreography instead of the adaptive one. */
    Fountain,

    /* Fixed cells: one number per cell in a grid inside each band, placed by allocation rather than by jitter. Each slides
     * briefly in from one spawn point, swells slightly as it lands, then does not move again. For a player who wants the
     * overlay out of the way, for a second small overlay over the target's cast bar, or for healing where motion adds
     * nothing. Cheapest style to draw, and the only one that reserves screen space — static text cannot overlap its own
     * numbers the way free-floating text does (FctCellGrid). */
    Pulse,

    /* A random cone per hit — height traded for lateral distance — then gravity (mirrored on the incoming band).
     * Diablo/PoE-ish: repeated hits fan out instead of stacking into one unreadable column, and the spread itself
     * communicates rate. The fall is tied to the height each number actually reached, so no angle can put one back
     * where it came from, which is what keeps the strip clear by construction rather than by clamping. */
    Spray,

    /*
     * Constant vertical speed across the side's region plus a quadratic outward drift — the parabola, which is the shape
     * the scrolling-text genre ships as its default (MSBT; docs/DesignNotes.md). The one style that only makes sense in a
     * scheme whose regions do not overlap: it scrolls across whatever owns the side, and in bands that is the strip included,
     * so ingest degrades it to hold there.
     */
    Parabola,

    /*
     * The split shape with the arc taken out: the same rail as the parabola — constant scroll, shared endpoints, one
     * beat per region, stream columns and all — running with bow zero, so each number climbs or sinks its column in a
     * straight line. MSBT calls it Straight and ships it as the no-nonsense alternative to Parabola; the reason it is
     * a sibling style rather than a knob is that everything rail-related keys off the style once, and forgetting one of
     * those places would make the two shapes drift apart silently. FctMotionStyles.IsRail is the only correct test.
     */
    Straight,
  }

  /* The two styles that ride a rail: shared entrance, shared beat, no jitter, stream placement. Anything asking
   * "does this travel a rail?" must ask here, not name one style (see Straight's comment). */
  internal static class FctMotionStyles
  {
    internal static bool IsRail(FctMotionStyle style) => style is FctMotionStyle.Parabola or FctMotionStyle.Straight;
  }
}