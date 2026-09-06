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

    /* Crit blowout: quick ramp to CritPeakScale, hold, then collapse over the last CritScaleOutMs. */
    public const double CritPeakScale = 1.30;
    public const double CritScaleInMs = 90;
    public const double CritScaleOutMs = 700;
    public const double CritScaleEnd = 0.06;

    public const double FadeInMs = 160;

    /* How long a folded-in hit takes to count up to its new total. */
    public const double CountUpMs = 300;

    public static double Progress(FctHitState hit, double ageMs) => Math.Clamp(ageMs / hit.MotionMs, 0.0, 1.0);

    /*
     * Hold style: ease out to the top band and stay. Fountain style: same rise, then fall with gravity
     * over the last FallPhaseFrac of life (paired with shrink + fade by the caller).
     */
    public static double RaisedY(FctHitState hit, double t)
    {
      if (hit.FallDist <= 0.0)
      {
        return hit.Y0 - (hit.Rise * EaseOutQuad(t));
      }

      const double riseFrac = 1.0 - FallPhaseFrac;
      if (t < riseFrac)
      {
        return hit.Y0 - (hit.Rise * EaseOutQuad(t / riseFrac));
      }

      var u = (t - riseFrac) / FallPhaseFrac;
      return (hit.Y0 - hit.Rise) + (hit.FallDist * u * u); // ease-in: accelerate downward
    }

    /*
     * Arc settles with the rise, clamped to the hit's half of the canvas. The clamp reserves half the
     * drawn width — scaled, so a crit at full blowout still cannot cross into the protected center —
     * and degrades to the band's middle instead of throwing when a label is wider than its half.
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

      return Math.Clamp(hit.X0 + (hit.Arc * EaseOutQuad(t)), lo, hi);
    }

    /* Crit pop, or the fountain shrink over the fall phase. 1 when neither applies. */
    public static double ScaleOf(FctHitState hit, double ageMs)
    {
      if (hit.Blowout)
      {
        return BlowoutScale(ageMs, hit.LifetimeMs);
      }

      if (hit.FallDist > 0.0)
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

    public static double FadeOpacity(FctHitState hit, double ageMs)
    {
      var o = ageMs < FadeInMs ? ageMs / FadeInMs : 1.0;
      var fadeStart = hit.LifetimeMs - hit.FadeMs;

      if (ageMs > fadeStart)
      {
        o *= Math.Max(0, 1 - ((ageMs - fadeStart) / hit.FadeMs));
      }

      return Math.Clamp(o, 0.0, 1.0);
    }

    /* The interpolated number while a folded-in hit counts up; the target otherwise. */
    public static double DisplayValue(FctHitState hit, double ageMs)
    {
      if (hit.CountUpMs <= 0 || hit.TargetValue == hit.CountBaseValue)
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

    /* Widest the drawn text ever gets, so a one-time clamp still protects the center over its life. */
    private static double ScaleAllowance(FctHitState hit) => hit.Blowout ? CritPeakScale : 1.0;

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

    private static double EaseOutQuad(double p) => 1 - ((1 - p) * (1 - p));
  }
}
