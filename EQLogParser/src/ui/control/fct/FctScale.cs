using System;

namespace EQLogParser
{
  /*
   * The two things a player is allowed to nudge: how big the numbers are, and how fast they come and go. Both are applied where a number is born -
   * never while it is on screen - which is what makes dragging either dial change what happens next instead of tugging at text already in flight.
   * That is also why these live here rather than being read at draw time: motion is a pure function of (hit, age), and a number whose size or timing
   * changed mid-flight would have to be re-measured, re-clamped and re-placed, which is the class of bug that layout was built to avoid.
   *
   * The two dials are not symmetrical any more, and both asymmetries are deliberate:
   *
   * **Size is ±50 %**, centred on 1.0, because a slider whose default is not in the middle cannot be parked by feel and the type scale genuinely is
   * centred on the size everything was measured at.
   *
   * **Speed runs -30 % to +90 % of the tempo that was measured, and its centre of gravity is above the old one.** Played against the game, half speed
   * (twice as long on screen) is unusable and half again as fast was not enough; the tempo worth sitting at turned out to be about 15 % quicker than
   * the one that shipped. So `SpeedDefault` is that tempo - which means the shipped overlay now runs slightly faster than it did, and the useful range
   * opens upward: 2.2x speed at the top end, and on the slow side only enough to get back to the old pace and a little past it. What "a little" means is
   * written once, as percentages, and the absolute ends are derived from it.
   *
   * **The player adjusts speed; numbers only ever get a duration.** A control called "time" whose right-hand end makes things happen sooner is
   * backwards, and it was - the first version of this row was labelled time, and the confusion it causes was reported rather than imagined. So the dial
   * speaks speed, and FctScale.Time stays what FctIngest has always multiplied by: how long a number shows, travels and fades. A speed therefore costs
   * a division (TimeFromSpeed), because "50 % faster" is two thirds of the time on screen rather than 50 % less of it.
   *
   * As for the outer limits: everything about the layout - lane columns at fractions of the width, the vertical reserve a line of text needs, the
   * adaptive lifetime under load - was measured at the shipped tempo and size (see docs/DesignNotes.md). Beyond half again the size, the damage and
   * healing columns begin to overlap at ordinary window sizes; a tempo past 2.2x arrives and leaves before it is readable, which the floors in
   * FctIngest are underneath to prevent rather than the dial's own bounds.
   */
  internal static class FctScale
  {
    public const double SizeMin = 0.5;
    public const double SizeMax = 1.5;
    public const double SizeDefault = 1.0;

    /* The speed dial is measured in percent of the tempo everything was choreographed at, so its ends are ± that rather than raw multipliers. */
    public const int SpeedPercentMin = -30;
    public const int SpeedPercentMax = 90;
    public const int SpeedPercentDefault = 0;

    /*
     * The tempo the feature ships at, and it is not 1.0. Playing with the overlay made the measured baseline feel like it was sitting on the slow
     * side of useful, roughly 15 % too leisurely, so that is where the middle of this dial now is: the shipped default IS today's "+15 %" from before
     * the change. Re-scaling everything else to match would have meant re-measuring layout, choreography and the adaptive controller for a preference;
     * moving the centre of one dial is the same result with one number in it.
     */
    public const double SpeedDefault = 1.15;

    /* The ends that percent span implies: 0.805 (24 % longer on screen) to 2.185 (46 % of the time on screen). */
    public const double SpeedMin = SpeedDefault * (1 + SpeedPercentMin / 100.0);
    public const double SpeedMax = SpeedDefault * (1 + SpeedPercentMax / 100.0);

    /*
     * A multiplier on every type size, applied once in FctStyle.ApplyTo so the hit carries its real drawn size and everything downstream - line
     * height, vertical reserve, clamp bands, the pulse grid, glyph measurement - follows without a second place that has to remember to scale.
     */
    public static double Text = SizeDefault;

    /*
     * A multiplier on how long a number lives, its travel and its fade together, in the range 1/SpeedMax .. 1/SpeedMin (0.46 to 1.24). Scaling only the
     * lifetime would leave a number hanging in mid-air past the end of its animation, which reads as a stutter rather than as a slower overlay. Nothing
     * clamps this to the size dial's range: it is the reciprocal of what the other dial measures.
     */
    public static double Time = TimeFromSpeed(SpeedDefault);

    /*
     * These come from settings.ini, where a hand edit can put "big" or 12 in place of a fraction. Two rules, in this order:
     *
     * A value that is a number gets clamped into range - 0 becomes the smallest and 9000 the largest, which is what "I wrote something small/large"
     * means, and a scale of zero would draw no text at all while looking like a setting that was honoured.
     *
     * A value that is not a number (NaN, Infinity - both of which compare false against the bounds, so Math.Clamp would pass them through untouched)
     * falls back to that dial's default, because there is no direction to clamp it toward. Neither case is worth an error dialog: this is a cosmetic
     * setting.
     */
    public static double ClampSize(double value) => !double.IsFinite(value) ? SizeDefault : Math.Clamp(value, SizeMin, SizeMax);

    public static double ClampSpeed(double value) => !double.IsFinite(value) ? SpeedDefault : Math.Clamp(value, SpeedMin, SpeedMax);

    /* Where a position on the speed dial lands, and back. Percent is of the shipped tempo, so 0 is not "no speed change" but "the default". */
    public static double SpeedFromPercent(double percent) => SpeedDefault * (1 + Math.Clamp(percent, SpeedPercentMin, SpeedPercentMax) / 100.0);

    public static int PercentOfSpeed(double speed) => (int)Math.Round((ClampSpeed(speed) / SpeedDefault - 1) * 100);

    /*
     * The duration multiplier a given speed means, and the one place that reversal lives. Going the other way by hand - scaling durations by -50 % -
     * would make the right-hand end of the slider the slower one again, which is what this is here to stop.
     */
    public static double TimeFromSpeed(double speed) => 1 / ClampSpeed(speed);

    /* Back the other way, for reading a duration scale written by an older build. A zero or nonsense duration means "no idea", so: default. */
    public static double SpeedFromTime(double time) => !double.IsFinite(time) || time <= 0 ? SpeedDefault : ClampSpeed(1 / time);

    /* The size dial's own readout: -50 % … +50 % rather than 0.5 … 1.5, because "bigger" is what the player is thinking in. */
    public static int PercentOfSize(double scale)
    {
      var clamped = ClampSize(scale);

      return (int)((clamped - SizeDefault) * 100 + (clamped >= SizeDefault ? 0.5 : -0.5));
    }

    public static double SizeFromPercent(int percent) => ClampSize(SizeDefault + (percent / 100.0));
  }
}
