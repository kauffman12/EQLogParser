using System;

namespace EQLogParser
{
  /*
   * The two things a player is allowed to nudge: how big the numbers are, and how long one takes to show and finish.
   * Both are a single multiplier in the range ±30 %, and both are applied where a number is born — never while it is on
   * screen — which is what makes dragging either slider change what happens next instead of tugging at text already in
   * flight. That is also why these live here rather than being read at draw time: motion is a pure function of
   * (hit, age), and a number whose size or timing changed mid-flight would have to be re-measured, re-clamped and
   * re-placed, which is the class of bug that layout was built to avoid.
   *
   * ±30 % is not a round number picked for comfort. Everything about the layout — lane columns at fractions of the
   * width, the vertical reserve a line of text needs, the adaptive lifetime under load — was measured at 1.0 (see
   * docs/DesignNotes.md). Pushing type far past that starts putting the damage and healing columns on top of each other
   * at ordinary window sizes, and shrinking life much past it puts a number on screen and takes it away again before it
   * can be read. The range is where the feature still looks like the thing that was measured; beyond that the sliders
   * would be offering to break it.
   */
  internal static class FctScale
  {
    public const double Min = 0.7;
    public const double Max = 1.3;
    public const double Default = 1.0;

    /*
     * A multiplier on every type size, applied once in FctStyle.ApplyTo so the hit carries its real drawn size and
     * everything downstream — line height, vertical reserve, clamp bands, the pulse grid, glyph measurement — follows
     * without a second place that has to remember to scale.
     */
    public static double Text = Default;

    /*
     * A multiplier on how long a number lives, its travel and its fade together. Scaling only the lifetime would leave a
     * number hanging in mid-air for longer while its animation ran at the old tempo, which reads as a stutter rather
     * than as a slower overlay.
     */
    public static double Time = Default;

    /*
     * These come from settings.ini, where a hand edit can put "big" or 12 in place of a fraction. Two rules, in this order:
     *
     * A value that is a number gets clamped into range — 0 becomes the smallest size and 9000 the largest, which is what "I wrote
     * something small/large" means, and a scale of zero would draw no text at all while looking like a setting that was honoured.
     *
     * A value that is not a number (NaN, Infinity — both of which compare false against the bounds, so Math.Clamp would pass them
     * through untouched) falls back to the default, because there is no direction to clamp it toward. Neither case is worth an error
     * dialog: this is a cosmetic setting.
     */
    public static double Clamp(double value) =>
      !double.IsFinite(value) ? Default : Math.Clamp(value, Min, Max);

    /* The same value a slider shows: -30 % … +30 % rather than 0.7 … 1.3, because "bigger" and "smaller" are what the
     * player is thinking in and the percentage is the only form that says which end is which without a label per end. */
    public static int Percent(double scale) => (int)((scale - Default) * 100 + (scale >= Default ? 0.5 : -0.5));

    public static double FromPercent(int percent) => Clamp(Default + (percent / 100.0));
  }
}
