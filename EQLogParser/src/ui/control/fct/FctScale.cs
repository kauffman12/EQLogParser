using System;

namespace EQLogParser
{
  /*
   * The two things a player is allowed to nudge: how big the numbers are, and how fast they come and go. Both are ±50 %, and both are applied where
   * a number is born - never while it is on screen - which is what makes dragging either dial change what happens next instead of tugging at text
   * already in flight. That is also why these live here rather than being read at draw time: motion is a pure function of (hit, age), and a number
   * whose size or timing changed mid-flight would have to be re-measured, re-clamped and re-placed, which is the class of bug that layout was built
   * to avoid.
   *
   * The two dials are deliberately symmetrical around 1.0, because a slider whose default is not in the middle cannot be parked by feel. For size
   * that is straightforward: 0.5 to 1.5 times the type. For the second dial it is not, and the reason this file exists in its current shape:
   *
   * **The player adjusts speed; numbers only ever get a duration.** A control called "time" whose right-hand end makes things happen sooner is
   * backwards, and it was - the first version of this row was labelled time, and the confusion it caused was reported rather than imagined. So the
   * dial speaks speed (see TimeFromSpeed), and FctScale.Time stays what FctIngest has always multiplied by: how long a number shows, travels and
   * fades. ±50 % of speed is therefore 2/3 to 2 times the duration, which is asymmetric in milliseconds and symmetric in the thing being asked for.
   *
   * As for how far the range goes: everything about the layout - lane columns at fractions of the width, the vertical reserve a line of text needs,
   * the adaptive lifetime under load - was measured at 1.0 (see docs/DesignNotes.md), and half again in either direction is about where that stops
   * being a preference and starts being a different feature: past it, the damage and healing columns begin to overlap at ordinary window sizes, and
   * a number can arrive and leave before it is readable. The floors in FctIngest are underneath all of this for the same reason.
   */
  internal static class FctScale
  {
    public const double Min = 0.5;
    public const double Max = 1.5;
    public const double Default = 1.0;

    /*
     * A multiplier on every type size, applied once in FctStyle.ApplyTo so the hit carries its real drawn size and everything downstream - line
     * height, vertical reserve, clamp bands, the pulse grid, glyph measurement - follows without a second place that has to remember to scale.
     */
    public static double Text = Default;

    /*
     * A multiplier on how long a number lives, its travel and its fade together, in the range 1/Max .. 1/Min (0.67 to 2.0). Scaling only the
     * lifetime would leave a number hanging in mid-air for longer while its animation ran at the old tempo, which reads as a stutter rather than as
     * a slower overlay. Nothing clamps this to Min..Max: it is the reciprocal of what the dial measures.
     */
    public static double Time = Default;

    /*
     * These come from settings.ini, where a hand edit can put "big" or 12 in place of a fraction. Two rules, in this order:
     *
     * A value that is a number gets clamped into range - 0 becomes the smallest and 9000 the largest, which is what "I wrote something small/large"
     * means, and a scale of zero would draw no text at all while looking like a setting that was honoured.
     *
     * A value that is not a number (NaN, Infinity - both of which compare false against the bounds, so Math.Clamp would pass them through untouched)
     * falls back to the default, because there is no direction to clamp it toward. Neither case is worth an error dialog: this is a cosmetic setting.
     */
    public static double Clamp(double value) => !double.IsFinite(value) ? Default : Math.Clamp(value, Min, Max);

    /* How fast, as the dial measures it: 0.5 is half speed (slower), 1.5 is half again faster. Same range as size, opposite meaning below. */
    public static double ClampSpeed(double speed) => Clamp(speed);

    /*
     * The duration multiplier a given speed means, and the one place that reversal lives. It has to be a division rather than a subtraction because
     * the thing the player is setting is a rate: "50 % faster" is two thirds of the time on screen, and "50 % slower" is double it. Going the other
     * way by hand - scaling durations by -50 % - would make the right-hand end of the slider the slower one again, which is what this is here to stop.
     */
    public static double TimeFromSpeed(double speed) => Default / ClampSpeed(speed);

    /* Back the other way, for reading a duration scale written by an older build. A zero or nonsense duration means "no idea", so: default. */
    public static double SpeedFromTime(double time) => !double.IsFinite(time) || time <= 0 ? Default : ClampSpeed(Default / time);

    /* The same value a slider shows: -50 % … +50 % rather than 0.5 … 1.5, because "bigger" and "faster" are what the player is thinking in. */
    public static int Percent(double scale) => (int)((scale - Default) * 100 + (scale >= Default ? 0.5 : -0.5));

    public static double FromPercent(int percent) => Clamp(Default + (percent / 100.0));
  }
}
