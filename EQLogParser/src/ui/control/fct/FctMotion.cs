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

    /* Fountain style: the fall spans the last fraction of life, shrinking as it goes. */
    public const double FallPhaseFrac = 0.45;
    public const double FallScaleEnd = 0.55;

    /*
     * How much of an incoming hit's sink distance it gets back on the way out, in bands mode where a literal downward
     * fall is not available (see FctIngest.AssignLifetime). Half of it reads as the same overshoot-and-settle your own
     * numbers have, mirrored, without crawling back up into the protected strip.
     */
    public const double IncomingFallsBackFrac = 0.5;

    /* Crit blowout: quick ramp to CritPeakScale, hold, then collapse over the last CritScaleOutMs. */
    public const double CritPeakScale = 1.30;
    public const double CritScaleInMs = 90;
    public const double CritScaleOutMs = 700;
    public const double CritScaleEnd = 0.06;

    /*
     * Pulse style: appear a little small, swell just past full size, settle onto it. This style has no travel at all, so
     * the swell is the whole announcement — and its overshoot is deliberately modest (12%) because a lane full of pulses
     * shimmering in and out at 30% would be worse than the motion it replaces.
     */
    public const double PulseStartScale = 0.86;
    public const double PulsePeakScale = 1.12;
    public const double PulseInMs = 130;
    public const double PulseSettleMs = 420;

    public const double FadeInMs = 160;

    /*
     * Procs run the whole fight at 70% tempo: travel, hold and fade all shorten together so the number is gone shortly
     * after the swing that provoked it instead of lingering as long as that swing's own text. Scaling only the tail would
     * leave a proc sitting on screen at full size while the hit behind it had already faded. See FctIngest.ApplyProcTempo.
     */
    public const double ProcTimeFrac = 0.7;

    /* How long a folded-in hit takes to count up to its new total. */
    public const double CountUpMs = 300;

    public static double Progress(FctHitState hit, double ageMs) => Math.Clamp(ageMs / hit.MotionMs, 0.0, 1.0);

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
        return hit.Y0 - (hit.Rise * Ease(t));
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
     * Arc settles with the rise, clamped to the hit's clamp band (the half it was given in halves mode, the window
     * edges in bands mode). The clamp reserves half the drawn width — scaled, so a crit at full blowout still cannot
     * cross into the protected middle — and degrades to the band's middle instead of throwing when a label is wider
     * than the space it has.
     */
    public static double ArcedX(FctHitState hit, double t)
    {
      var half = hit.ValueWidth * ScaleAllowance(hit) / 2.0;
      var lo = hit.SideMin + half;
      var hi = hit.SideMax - half;

      if (lo > hi)
      {
        return (hit.SideMin + hit.SideMax) / 2.0;
      }

      return Math.Clamp(hit.X0 + (hit.Arc * Ease(t)), lo, hi);
    }

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

    /* Whether the drawn number is mid-count-up. Backends use it so a growing total moves nothing else around it. */
    public static bool IsCountingUp(FctHitState hit) => hit.CountUpMs > 0 && hit.TargetValue != hit.CountBaseValue;

    /* The interpolated number while a folded-in hit counts up; the target otherwise. */
    public static double DisplayValue(FctHitState hit, double ageMs)
    {
      if (!IsCountingUp(hit))
      {
        return hit.TargetValue;
      }

      var p = Math.Clamp((ageMs - hit.AgeAtCountStartMs) / hit.CountUpMs, 0.0, 1.0);
      return hit.CountBaseValue + ((hit.TargetValue - hit.CountBaseValue) * p);
    }

    /*
     * The main line. A label hit (FixedText) is literal and must never be replaced by the formatted
     * value: zero-damage records carry Value 0, and formatting that clobbered "Dodge" with "0".
     */
    public static void RefreshText(FctHitState hit, double ageMs)
    {
      var text = hit.FixedText ?? FctText.FormatHitValue(DisplayValue(hit, ageMs));
      if (text == hit.DisplayText)
      {
        return;
      }

      hit.DisplayText = text;
      hit.TextDirty = true;
    }

    /* Widest the drawn text ever gets, so a one-time clamp still protects the center over its life — and it agrees with
     * FctLayout.TextReserve about the peak, because x and y must not disagree about how big this hit becomes. */
    private static double ScaleAllowance(FctHitState hit) =>
      hit.Blowout ? CritPeakScale : hit.Style is FctMotionStyle.Pulse ? PulsePeakScale : 1.0;

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

    private static double BlowoutScale(double ageMs, double lifetimeMs)
    {
      if (ageMs < CritScaleInMs)
      {
        return 1 + ((CritPeakScale - 1) * (ageMs / CritScaleInMs));
      }

      if (ageMs < lifetimeMs - CritScaleOutMs)
      {
        return CritPeakScale;
      }

      var p = Math.Clamp((ageMs - (lifetimeMs - CritScaleOutMs)) / CritScaleOutMs, 0.0, 1.0);
      return CritPeakScale - ((CritPeakScale - CritScaleEnd) * p * p);
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
