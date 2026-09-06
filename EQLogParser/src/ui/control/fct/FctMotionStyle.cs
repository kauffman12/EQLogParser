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

    /* No travel at all: appear slightly small, swell just past full size, settle, fade in place. For a player who
     * wants the overlay to stay out of the way, for a second small overlay over the target's health bar, or for
     * healing where motion adds nothing. Cheapest style to draw — the position never changes after spawn, and it is
     * chosen deeper inside the band than travelling text is, because there its spawn point is where it gets read. */
    Pulse,

    /* A random cone per hit — height traded for lateral distance — then gravity (mirrored on the incoming band).
     * Diablo/PoE-ish: repeated hits fan out instead of stacking into one unreadable column, and the spread itself
     * communicates rate. The fall is tied to the height each number actually reached, so no angle can put one back
     * where it came from, which is what keeps the strip clear by construction rather than by clamping. */
    Spray,
  }
}
