namespace EQLogParser
{
  /*
   * How a hit moves over its life. Motion is presentation, not information: which region a number sits in and which way
   * it travels carry who acted, so every style below keeps that promise and none of them may put text in the protected
   * middle strip (in bands; split's columns have no strip because they do not overlap each other). Rationale:
   * docs/DesignNotes.md → "Four motion styles, and the one thing none of them may change".
   */
  internal enum FctMotionStyle
  {
    /* Travel away from the gap, decelerate to a stop, then fade in place. The default because it is the least demanding: the
     * number arrives, stays put where it can be read twice, and leaves. Everything else is a variation on that. */
    Freeze,

    /* Kept for possible future use: no control selects this style — the settings panel's FOUNTAIN mode is bands geometry
     * wearing Spray (FctOverlaySettings.SaveIsFountain), which is the plume players were offered when the checkbox went away.
     * Reachable through settings.ini (LoadMotion) and FctSimulationWindow, so the path stays exercised while it waits for a UI.
     *
     * Overshoot, then accelerate under gravity while shrinking (mirrored upward on the incoming band). The most
     * legible "something happened" of the lot and the loudest: numbers cross a third of the overlay, which is also why
     * it is opt-in and why it fixes the life to the choreography instead of the adaptive one. */
    Fountain,

    /* A random cone per hit — height traded for lateral distance — then gravity (mirrored on the incoming band).
     * Diablo/PoE-ish: repeated hits fan out instead of stacking into one unreadable column, and the spread itself
     * communicates rate. The fall is tied to the height each number actually reached, so no angle can put one back
     * where it came from, which is what keeps the strip clear by construction rather than by clamping. */
    Spray,

    /*
     * Constant vertical speed up the owning lane plus a quadratic outward drift — the arc, whose curve is MSBT’s parabola and which is the shape the
     * scrolling-text genre ships as its default (MSBT; docs/DesignNotes.md). The one style that only makes sense in a scheme
     * whose regions do not overlap: it scrolls across whatever owns the number, and in bands that is the strip included, so
     * ingest degrades it to freeze there. Its train is kept in step by the conveyor (FctConveyor), which owns its spacing.
     */
    Arc,

    /*
     * The split shape with the bend taken out: the same rail as the arc — constant scroll, shared entrance, one clock
     * per column and all — running with bow zero, so each number climbs or sinks its lane in a straight line. MSBT calls it
     * Straight and ships it as the no-nonsense alternative to its parabola; the reason it is
     * a sibling style rather than a knob is that everything rail-related keys off the style once, and forgetting one of
     * those places would make the two shapes drift apart silently. FctMotionStyles.IsRail is the only correct test.
     */
    Straight,
  }

  /* The two styles that ride a rail: shared entrance, shared beat, no jitter, conveyor placement. Anything asking
   * "does this travel a rail?" must ask here, not name one style (see Straight's comment). */
  internal static class FctMotionStyles
  {
    internal static bool IsRail(FctMotionStyle style) => style is FctMotionStyle.Arc or FctMotionStyle.Straight;
  }
}