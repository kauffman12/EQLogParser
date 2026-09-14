using System;

namespace EQLogParser
{
  /*
   * The two things a player is allowed to nudge: how big the numbers are, and how fast they come and go. Both are applied where a number is born -
   * never while it is on screen - which is what makes dragging either dial change what happens next instead of tugging at text already in flight.
   * That is also why these live here rather than being read at draw time: motion is a pure function of (hit, age), and a number whose size or timing
   * changed mid-flight would have to be re-measured, re-clamped and re-placed, which is the class of bug that layout was built to avoid.
   *
   * **The two size dials run -50 % to +110 % and the speed dial ±50 %, and all can be parked by feel on their default**, which they were not for a while.
   * The size window stopped being symmetric because its ends are set by looks, not by round numbers: the floor draws what the old ±75 dial's -60 % mark drew —
   * as small as anybody actually plays it — and the ceiling reaches double the tier table, the +100 % the old dial could only ever name. The middle moved with
   * them: text normal ships a tenth under the tier table because that is where shipped text looked right in game, so the crit dial's unchanged +10 % now draws
   * exactly where plain numbers used to. Speed used to be a tempo multiplier
   * running -30 % to +90 %, because playing with the feature said the usable band sat faster than the measured baseline and did not extend nearly as far
   * toward slow as a symmetric dial implies. That is all still true; what changed is where the arithmetic happens. The middle is now the midpoint of
   * that band's two results - see TimeDefault - so the dial is centred, symmetric and reads the way a slider should, and the shape of the usable range
   * lives in one number instead of in two asymmetric ends.
   *
   * **The unit is time, not rate.** A control called speed whose right end is faster, measured in percent of how long a number stays up: +50 % takes
   * half as long on screen, -50 % lasts half again as long, and the middle is nothing. The first version of this row was labelled "time" and its right
   * end was the slower one, which was reported as confusing rather than imagined; naming it speed is what fixed that, and the direction of the sign is
   * handled here once (TimeFromPercent) so that nothing downstream has to hold the inversion in its head. settings.ini still stores a rate
   * (`FctOverlaySpeed`), because "1.4" reads as faster and "0.7" reads as slower without having to think about reciprocals.
   *
   * As for how far the ranges go: everything about the layout - lane columns at fractions of the width, the vertical reserve a line of text needs, the
   * adaptive lifetime under load - was measured at 1.0 and at 1x (see docs/DesignNotes.md). Past one-and-a-half the size, damage and healing columns begin
   * to press against each other at ordinary window sizes, so the last quarter of a size dial is for wide overlays; at the fast end of speed a number
   * arrives and leaves before it is readable, which is what the floors in FctIngest are underneath for rather than the dial's own bounds.
   */
  internal static class FctScale
  {
    /* The reference the percentages speak against - what "normal" means on either dial, and what a double-click parks
       the text slider on. It used to be the tier table's own 1.0; it came down a tenth because that is where shipped
       text looked its best in game. The window hangs off it additively (SizeFromPercent below), its ends pegged to the
       two looks the player asked for: 0.4x and 2.0x of the tier table, -50 % and +110 % from here. */
    public const double SizeDefault = 0.9;
    public const int SizePercentMin = -50;
    public const int SizePercentMax = 110;
    public const double SizeMin = SizeDefault + SizePercentMin / 100.0;
    public const double SizeMax = SizeDefault + SizePercentMax / 100.0;

    /*
     * Two size dials for two classes of number, sized independently under the SAME rule: each is "percent over the shipped
     * middle, -50 % to +110 %", it just answers for different rows. TEXT SIZE covers every ordinary number (each in its lane's tier); CRIT SIZE covers the
     * big class - crits and the marked special attacks - which share one size because they share one lane, one colour and one draw pass.
     *
     * Independent rather than one scaling the other, after two failed couplings. A fixed crit tier above every lane meant "0 %" still drew
     * clearly bigger than a normal hit - the dial could not reach its own promise. Making it a multiplier over the text dial fixed the
     * parity but stacked: font x dial x pop held two hidden multiplications deep, and parking both sliders mid put crits forty-odd percent
     * over hits with nothing on screen to say so. Under this rule a sentence fits on the slider: "crit size 0 % draws a crit exactly as
     * big as a normal hit at its own setting's baseline; every percent adds that much." The text dial deliberately does NOT reach into
     * the crit class: a player making ordinary numbers readable should not have their exceptions swell unasked, and one dial per class is
     * the only promise a two-dial UI can actually keep.
     *
     * Crit ships at +10 % - modest by design, since halo, pop, colour and draw order already announce the event. The retune moved the reference under it
     * rather than this number: +10 % over a lower normal draws exactly where plain text used to, so "barely bigger, still obviously crits" became the
     * shipping answer instead of a dial position to hunt for. From here the class can still be pushed to double the tier table or pulled under ordinary
     * numbers for someone who wants crits QUIET, which is a real opinion now that the dial reaches it.
     */
    public const int CritSizePercentDefault = 10;
    public const double CritSizeDefault = 1.00;

    /* Both ends of the speed dial, in percent of the time a number spends on screen. + is less time, which is faster. */
    public const int SpeedPercentMin = -50;
    public const int SpeedPercentMax = 50;
    public const int SpeedPercentDefault = 0;

    /*
     * The centre of the dial, as a share of the time everything was choreographed at. It is the midpoint of the two ends that playing with the previous
     * dial produced: its slow end left numbers up about 1.24 times as long as measured and its fast end about 0.51 times, and halfway between those
     * results is 0.877 - which is also roughly the tempo the feature had settled on shipping at, so this is a re-centring rather than a re-tuning.
     *
     * Choosing it in time rather than in rate matters: it is the thing being judged by eye, and averaging rates would have put the middle somewhere the
     * band does not do anything interesting. ±50 % of it gives 1.32x on the slow side (past the old neutral pace, nowhere near the twice-as-long that
     * nobody can play under) and 0.44x on the fast (further than the old +90 %, which is where the request to open things up came from).
     */
    public const double TimeDefault = 0.877;
    public const double TimeMin = TimeDefault * (1 - SpeedPercentMax / 100.0);
    public const double TimeMax = TimeDefault * (1 - SpeedPercentMin / 100.0);

    /* The same three points as a rate, which is what settings.ini holds: stored as speed because that is what the control is called. */
    public const double SpeedDefault = 1 / TimeDefault;
    public const double SpeedMin = 1 / TimeMax;
    public const double SpeedMax = 1 / TimeMin;

    /*
     * A multiplier on every type size, applied once in FctStyle.ApplyTo so the hit carries its real drawn size and everything downstream - line
     * height, vertical reserve, clamp bands, glyph measurement - follows without a second place that has to remember to scale.
     */
    public static double Text = SizeDefault;

    /* The crit class's own size multiplier: same band as Text (both dials are percent over the shipped middle, -50 % to +110 %), its own shipped
       middle (+10 %) and its own rescue target - junk lands on THIS dial's default, never the text dial's. */
    public static double Crit = CritSizeDefault;

    public static double ClampCritSize(double value) =>
      !double.IsFinite(value) ? CritSizeDefault : Math.Clamp(value, SizeMin, SizeMax);

    /*
     * A multiplier on how long a number lives, its travel and its fade together, in the range TimeMin .. TimeMax. Scaling only the lifetime would leave a
     * number hanging in mid-air past the end of its animation, which reads as a stutter rather than as a slower overlay. It opens at the middle of the
     * dial rather than at 1.0: the measured baseline turned out to sit on the slow side of useful, so there is no "unset" case left in which FctIngest
     * should let its floors stand down.
     */
    public static double Time = TimeDefault;

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

    public static double ClampTime(double time) => !double.IsFinite(time) ? TimeDefault : Math.Clamp(time, TimeMin, TimeMax);

    public static double ClampSpeed(double speed) => !double.IsFinite(speed) ? SpeedDefault : Math.Clamp(speed, SpeedMin, SpeedMax);

    /*
     * Where a position on the speed dial lands. One subtraction from the whole and one multiplication, in time units, because that is what the dial is
     * measured in: +50 % of speed is half the time up, not 1/1.5 of it. Everything downstream - FctIngest scaling a number's life, travel and fade - sees
     * only the duration multiplier this returns.
     */
    public static double TimeFromPercent(double percent) => ClampTime(TimeDefault * (1 - Math.Clamp(percent, SpeedPercentMin, SpeedPercentMax) / 100.0));

    public static int PercentOfTime(double time) => (int)Math.Round((1 - ClampTime(time) / TimeDefault) * 100);

    /* The stored form of the same position, and back. */
    public static double SpeedFromPercent(double percent) => 1 / TimeFromPercent(percent);

    public static int PercentOfSpeed(double speed) => PercentOfTime(TimeFromSpeed(speed));

    /*
     * Rate to duration and back, for anything thinking in tempo rather than in dial positions. A nonsense rate - zero, negative, not a number - has no
     * direction to clamp toward, so it lands on the default instead of being honoured literally into an overlay that draws nothing.
     */
    public static double TimeFromSpeed(double speed) => 1 / ClampSpeed(speed);

    public static double SpeedFromTime(double time) => !double.IsFinite(time) || time <= 0 ? SpeedDefault : ClampSpeed(1 / time);

    /* The size dials' own readout: -75 % ... +75 % rather than 0.25 ... 1.75, because "bigger" is what the player is thinking in. */
    public static int PercentOfSize(double scale)
    {
      var clamped = ClampSize(scale);

      return (int)((clamped - SizeDefault) * 100 + (clamped >= SizeDefault ? 0.5 : -0.5));
    }

    public static double SizeFromPercent(int percent) => ClampSize(SizeDefault + (percent / 100.0));
  }
}