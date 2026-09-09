using System;

namespace EQLogParser
{
  /*
   * Frame maths shared by every FCT backend: where a hit is, how big it is, how opaque it is and what
   * text it shows. Pure functions of (hit, age) so the whole animation is unit-testable without a
   * window — see EQLogParser.Wpf.Test/src/ui/control/FctMotionTest.cs. Rationale for the motion window
   * and the hold: docs/DesignNotes.md → Floating Combat Text.
   */
  internal static class FctMotion
  {
    /* Rise+arc finish this early in life, after which the hit holds position and only fades. */
    public const double MotionWindowMs = 2000;

    /*
     * The parabola’s cadence in ms per pixel of region height: MSBT scrolls every value in an area at ONE rate
     * (ScrollUp is height × progress), and the chain look — each number following the one before along the same path
     * — needs one shared beat rather than a speed per row, so a parabola’s lifetime comes from this and its region’s
     * height rather than from the adaptive controller (FctIngest.ApplyRailTempo). Rows of different font sizes cover
     * slightly different distances within the beat, a few percent apart in rate and invisible; 5 ms/px is ~200 px/s,
     * the pace measured playable at default size, and like MSBT’s a taller region is a longer journey, not faster text.
     */
    public const double ParabolaScrollMsPerPx = 5;

    /*
     * Spray's choreography is deliberately shorter than fountain's. Shape separates the two styles (see LateralProgress),
     * but timing was the other half of why they read as one thing: two 2 second flights look alike whatever path they draw.
     * Shrapnel is quick — out, over and down in about 1.7 s — which also lets a burst clear before the next one lands.
     */
    public const double SprayMotionWindowMs = 1700;

    /* Fountain style: the fall spans the last fraction of life, shrinking as it goes. */
    public const double FallPhaseFrac = 0.45;
    public const double FallScaleEnd = 0.55;

    /*
     * How much of an incoming hit's sink distance it gets back on the way out, in bands mode where a literal downward
     * fall is not available (see FctIngest.AssignLifetime). Half of it reads as the same overshoot-and-settle your own
     * numbers have, mirrored, without crawling back up into the protected strip.
     */
    public const double IncomingFallsBackFrac = 0.5;

    /*
     * Crit blowout, restyled once crit SIZE became a dial: the number swells IN from just under full size over CritScaleInMs, rests at
     * exactly the size its font says, then collapses away over the last CritScaleOutMs so it shrinks out instead of blinking off.
     *
     * The envelope never exceeds 1.0, and that is the whole point. Size belongs to FctStyle.ApplyTo and to nothing else: the big class is
     * written at its own font, so a pop that also scaled the number above 1 would be a second multiplication stacked on the player's dial -
     * which is exactly how this used to work, and how "crit size at its minimum" still produced numbers far bigger than ordinary hits.
     * Emphasis without arithmetic: growth from below reads as ARRIVAL (the pulse swell makes the same move), while the halo, the hue and the
     * top draw pass were never size in the first place.
     */
    public const double CritScaleInStart = 0.72;
    public const double CritScaleInMs = 90;
    public const double CritScaleOutMs = 700;
    public const double CritScaleEnd = 0.06;

    /*
     * Pulse style: appear a little small, swell just past full size, settle onto it. Its only movement is the short slide
     * into an assigned cell (FctCellGrid), so the swell carries the announcement — and its overshoot is deliberately modest (12%) because a lane full of pulses
     * shimmering in and out at 30% would be worse than the motion it replaces.
     */
    public const double PulseStartScale = 0.86;
    public const double PulsePeakScale = 1.12;
    public const double PulseInMs = 130;
    public const double PulseSettleMs = 420;

    /*
     * How long a celled (pulse) number takes to slide from its band's single spawn point into the cell it was assigned.
     * Short enough that it is readable in place almost at once, long enough that a burst still reads as one event spreading
     * out. Zero drops numbers straight into their cells, which is what anyone who finds the slide busy should run.
     */
    public const double PulseSlideMs = 220;

    public const double FadeInMs = 160;

    /*
     * Procs run the whole fight at 70% tempo: travel, hold and fade all shorten together so the number is gone shortly
     * after the swing that provoked it instead of lingering as long as that swing's own text. Scaling only the tail would
     * leave a proc sitting on screen at full size while the hit behind it had already faded. See FctIngest.ApplyProcTempo.
     */
    public const double ProcTimeFrac = 0.7;

    /*
     * Where a hit is along its travel, 0..1. The zero guard is not decoration: pulse mode's MotionMs comes from
     * FctCellGrid.SlideMs, so setting that to 0 — "appear straight in place", a plausible knob to expose — divides 0 by 0
     * on the hit's very first frame, and NaN survives both Math.Clamp and the band clamps (NaN compares false against
     * every limit), so ArcedX and RaisedY hand back NaN and the number simply fails to be drawn for that frame.
     */
    public static double Progress(FctHitState hit, double ageMs) =>
      hit.MotionMs > 0 ? Math.Clamp(ageMs / hit.MotionMs, 0.0, 1.0) : 1.0;

    /*
     * Hold style: ease out along the hit's travel (up for outgoing, down for an incoming hit in bands mode, whose
     * Rise is negative) and then hold. Fountain style: same travel, then fall over the last FallPhaseFrac of life
     * (paired with shrink + fade by the caller). FallDist is signed the way Rise is: positive accelerates toward the
     * bottom of the screen, negative back up toward the gap, which is how the incoming band gets a mirrored fountain
     * instead of parking against its own bottom edge.
     *
     * Both halves are continuous in velocity: smootherstep arrives at zero speed and the ease-in tail leaves from
     * zero, so the handoff at riseFrac has no kink to look at — the seam most parabolas show.
     *
     * The band clamp is what stops traffic entering the protected middle strip when the two disagree — a resize that
     * moves the gap under a number already in flight, or a fall asked for more room than the band has.
     */
    public static double RaisedY(FctHitState hit, double t) => ClampedToBand(hit, TravelledY(hit, t));

    private static double TravelledY(FctHitState hit, double t)
    {
      if (hit.FallDist == 0.0)
      {
        /* The parabola scrolls at constant vertical speed — that is the shape: with y linear in t and x quadratic in t,
           x is a function of y², which is what makes it a parabola rather than an arc. Everything else eases. */
        var v = FctMotionStyles.IsRail(hit.Style) ? t : Ease(t);
        return hit.Y0 - (hit.Rise * v);
      }

      const double riseFrac = 1.0 - FallPhaseFrac;
      if (t < riseFrac)
      {
        return hit.Y0 - (hit.Rise * Ease(t / riseFrac));
      }

      var u = (t - riseFrac) / FallPhaseFrac;
      return (hit.Y0 - hit.Rise) + (hit.FallDist * u * u); // ease-in: accelerate along the fall's own sign
    }

    /* A layout with no vertical limit leaves the band at 0..0; clamping against that would pin every hit to the
     * top of the canvas, so an unset band means "no vertical clamp". */
    private static double ClampedToBand(FctHitState hit, double y) =>
      hit.BandMaxY > hit.BandMinY ? Math.Clamp(y, hit.BandMinY, hit.BandMaxY) : y;

    /*
     * Where the value's CENTRE is at time t. Two rules, one shared spine:
     *
     * Right-aligned (everything but pulse): values are an odometer. The spine X0+lateral carries the value's RIGHT
     * edge, so a 950 and a 12,040 in the same column line their ones digit up under each other instead of their
     * middles — Mik's "right-justify" text alignment, the one every parse-heavy player turns on. It ships as the
     * hidden default with no knob: centre-aligning a number column is never what anyone wants. The clamp keeps the
     * WHOLE drawn box (at its peak scale) left of SideMax and right of SideMin — the box grows LEFTWARDS out of the
     * rail, which is also how a crit blowout pops: the rail holds, the number punches wider. `scale` is the frame's
     * blowout so the returned centre tracks the box that is actually being drawn; placement scoring passes the same
     * value it scores with, so the odometer the renderer draws and the one the collision cost sees are one number.
     *
     * Centre-aligned (pulse only): a cell is a box the value sits IN; hanging it off the cell's left edge would put
     * neighbours' values inside each other. The pulse grid keeps the old half-width clamp at the cell centre.
     *
     * Degenerate either way — a label wider than its territory degrades to the territory's middle instead of throwing.
     */
    public static double ArcedX(FctHitState hit, double t)
    {
      // the reserved special-event glyph is part of the drawn box's reach, though it never moves the rail itself
      var peak = (hit.ValueWidth * ScaleAllowance(hit)) + hit.IconAllowance;

      /* The parabola is MSBT's geometry as written — x = y²/4a measured from the rail's mid-point, which with y linear
         in t comes out as 4·t(1−t) off the column: a number leaves its column straight up or down, bows out to the
         vertex at half height, and comes BACK to the column by the time it fades. Ends on the column, arc between them:
         that is the semicircle chain in Mik's demos, and it is also why stream columns keep their spacing for the whole
         flight — a shared bow cancels in every difference. (The old t² drift here was a curve with its vertex at the
         spawn: outward forever, never back, and it read as nothing in the genre.) */
      var lateral = FctMotionStyles.IsRail(hit.Style) ? hit.Bow * 4 * t * (1 - t) : hit.Arc * LateralProgress(hit, t);

      if (hit.Style is FctMotionStyle.Pulse)
      {
        var loC = hit.SideMin + peak / 2.0;
        var hiC = hit.SideMax - peak / 2.0;
        return loC > hiC
          ? (hit.SideMin + hit.SideMax) / 2.0
          : Math.Clamp(hit.X0 + lateral, loC, hiC);
      }

      var loR = hit.SideMin + peak; // the whole box fits, growing leftwards from the rail
      if (loR > hit.SideMax)
      {
        return (hit.SideMin + hit.SideMax) / 2.0;
      }

      /* Rest centre, always: the right edge rides the rail at rest and under every fold's width change, but the
         blowout's SCALE animation is anchored here, at the centre — not against the rail. Pinning the edge through
         an animated scale (the old `rail - w * scale / 2`) made a dying crit walk sideways toward its own rail while
         it collapsed, and on the straight line that read as crits and marks drifting up-and-RIGHT where every
         ordinary row rose straight up. Centre anchoring is what the genre does — pop in and collapse are symmetric
         about the text's anchor — and it can only ever TUCK the drawn box further inside the rail (scale never
         exceeds 1), so the odometer's edge is safe in both directions. Callers that need the drawn box apply their
         own scaled half-width around this centre; scoring and drawing agree to the bit exactly as before. */
      var rail = Math.Clamp(hit.X0 + lateral, loR, hit.SideMax);
      return rail - hit.ValueWidth / 2.0;
    }

    /*
     * How far along its sideways travel a hit is. Hold and fountain share the vertical ease so x and y stay in proportion:
     * a number climbs the straight line it appears to be on, which is what a thrown thing with no gravity looks like.
     *
     * Spray must not, and this was why spray and fountain were hard to tell apart. With both axes driven by one curve, every
     * angle of the cone draws a straight line from origin to apex — the fan existed only in where numbers ended up, never in
     * how they got there, so mid-flight a wide spray was a slanted fountain. Real shrapnel keeps its sideways speed while
     * gravity takes the vertical away, so spray runs laterally on an ease-out: out first, then up, and the path bends over
     * into an arc on its own. Easing to zero slope at the apex instead of running linearly matters too — something that
     * stops dead sideways at the moment it begins to fall has a kink in it you can see.
     */
    private static double LateralProgress(FctHitState hit, double t) =>
      hit.Style is FctMotionStyle.Spray ? EaseOutQuad(t) : Ease(t);

    /* Quadratic ease-out: fastest at the start, arriving at rest. */
    private static double EaseOutQuad(double p) => p * (2 - p);

    /* Crit pop, the pulse swell, or the fall-phase shrink. 1 when none applies. */
    public static double ScaleOf(FctHitState hit, double ageMs)
    {
      if (hit.Blowout)
      {
        return BlowoutScale(ageMs, hit.LifetimeMs);
      }


      if (hit.Style is FctMotionStyle.Pulse)
      {
        return PulseScale(ageMs);
      }

      if (hit.FallDist != 0.0)
      {
        const double riseFrac = 1.0 - FallPhaseFrac;
        var t = Progress(hit, ageMs);
        if (t >= riseFrac)
        {
          var u = (t - riseFrac) / FallPhaseFrac;
          return 1.0 + ((FallScaleEnd - 1.0) * u);
        }
      }

      return 1.0;
    }

    /*
     * Both ends are eased rather than linear. A linear ramp changes slope abruptly at fadeStart — steady, then suddenly
     * dimming, then gone — and a linear start appears as a hard pop; easing each end removes both discontinuities. The
     * mid-fade is slightly steeper as the trade, which reads as the text holding up and then dissolving rather than
     * draining away.
     */
    public static double FadeOpacity(FctHitState hit, double ageMs)
    {
      var o = ageMs < FadeInMs ? Ease(ageMs / FadeInMs) : 1.0;
      var fadeStart = hit.LifetimeMs - hit.FadeMs;

      if (ageMs > fadeStart)
      {
        o *= 1.0 - Ease(Math.Clamp((ageMs - fadeStart) / hit.FadeMs, 0.0, 1.0));
      }

      return Math.Clamp(o, 0.0, 1.0);
    }

    /*
     * The main line. A label hit (FixedText) is literal and must never be replaced by the formatted
     * value: zero-damage records carry Value 0, and formatting that clobbered "Dodge" with "0".
     *
     * Nothing here depends on age any more. While a folded hit counted up its total, the drawn number changed over time and
     * this had to be called every frame; showing one face value plus a hit count means the text only changes when a duplicate
     * folds in, which FctIngest asks for directly.
     */
    public static void RefreshText(FctHitState hit)
    {
      var text = hit.FixedText ?? FctText.FormatHit(hit.Value, hit.MergeCount, hit.Heal);
      if (text == hit.DisplayText)
      {
        return;
      }

      hit.DisplayText = text;
      hit.TextDirty = true;
    }

    /* Widest the drawn text ever gets beyond its measured font, so a one-time clamp still protects the center over its life — and it
     * agrees with FctLayout.TextReserve about the peak, because x and y must not disagree about how big this hit becomes. Blowout is not
     * in here any more: the big class swells in from BELOW full size, so it never draws wider than the font that was already measured. */
    private static double ScaleAllowance(FctHitState hit) =>
      hit.Style is FctMotionStyle.Pulse ? PulsePeakScale : 1.0;

    /*
     * The pulse swell: in over PulseInMs, back down to full size by PulseSettleMs, then nothing — the eased ends keep it
     * from looking like a screen glitch, and it never returns below 1.0 because a number shrinking away reads as
     * leaving, which is the fade's job.
     */
    private static double PulseScale(double ageMs)
    {
      if (ageMs < PulseInMs)
      {
        return PulseStartScale + ((PulsePeakScale - PulseStartScale) * Ease(ageMs / PulseInMs));
      }

      if (ageMs < PulseSettleMs)
      {
        return PulsePeakScale - ((PulsePeakScale - 1.0) * Ease((ageMs - PulseInMs) / (PulseSettleMs - PulseInMs)));
      }

      return 1.0;
    }

    /* Swell in from CritScaleInStart, rest at exactly the size the font says (scale 1.0), collapse out. The rest is 1.0 rather than a hold
       above it because this curve is emphasis only: how big the number IS was decided once, by its class and dial, at birth. */
    private static double BlowoutScale(double ageMs, double lifetimeMs)
    {
      if (ageMs < CritScaleInMs)
      {
        return CritScaleInStart + ((1.0 - CritScaleInStart) * (ageMs / CritScaleInMs));
      }

      if (ageMs < lifetimeMs - CritScaleOutMs)
      {
        return 1.0;
      }

      var p = Math.Clamp((ageMs - (lifetimeMs - CritScaleOutMs)) / CritScaleOutMs, 0.0, 1.0);
      return 1.0 - ((1.0 - CritScaleEnd) * p * p);
    }

    /*
     * Smootherstep (6t⁵ − 15t⁴ + 10t³): velocity *and* acceleration are zero at both ends, so a number leaves
     * without a jerk and arrives without a hard stop. EaseOutQuad — the previous curve — begins at full speed, which
     * is what made a hit look thrown onto the screen rather than floated off it; arc uses the same curve so the two
     * components stay in step and the path does not bend oddly on the way out.
     */
    private static double Ease(double p) => p * p * p * (p * ((p * 6) - 15) + 10);
  }
}