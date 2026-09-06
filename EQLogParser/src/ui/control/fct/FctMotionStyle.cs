namespace EQLogParser
{
  /*
   * How a hit moves over its life. Motion is presentation, not information: which band a number sits in and which way
   * it travels carry who acted, so every style below keeps that promise and none of them may put text in the protected
   * middle strip. Rationale: docs/DesignNotes.md → "Four motion styles, and the one thing none of them may change".
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
  }
}
