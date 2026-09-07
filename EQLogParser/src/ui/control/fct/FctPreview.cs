using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The examples configure mode draws: five numbers sitting still at chosen moments of their flight, so a size or style
   * change can be seen without waiting for a fight to produce one of each. Combat does not cooperate with a settings row —
   * there is no way to ask for a crit, a tick, a dodge and a heal in one glance — and hovering a slider while real numbers
   * scroll past teaches one thing at a time by accident.
   *
   * They are not real hits. A preview never enters FctIngest: it takes no lane slot, cannot be folded into or evicted, does
   * not age, is not counted as loss and is not counted in the diagnostics readout. Each one carries PreviewPhase, which tells
   * a backend to draw it at that fraction of its life instead of at wall-clock age — that single field is the whole mechanism,
   * and it keeps previews off every policy path, which is what stops an example from displacing a real number.
   *
   * They are spread across the width instead of standing in their lane columns. Five examples side by side cannot share three
   * columns, and what the row needs to show is size, shape and direction — so lane columns give way here, while band (above or
   * below the strip) and direction of travel stay exactly as real hits have them.
   */
  internal static class FctPreview
  {
    /* Fixed seed: an example that jumped position every time a slider was nudged would look like a bug in the overlay,
       and the invariant checks downstream want the same five placements each run. */
    private const int PreviewSeed = 20260214;

    private static readonly Random _rand = new(PreviewSeed);

    /* How far through its life each example is frozen. Capped below the fade: an example that is 90 % transparent is not an
       example, and the useful span is launch → travel → the start of letting go. */
    private const double MinPhase = 0.08;
    private const double MaxPhase = 0.78;

    /*
     * One line per example. The point of choosing these five rather than five damage numbers is that they are the five things
     * a player has to be able to tell apart: the crit pop, a normal hit with its ability line, a zero-damage word, healing on
     * the incoming side, and a periodic tick carrying a fold count — which is also the smallest tier and the only one that
     * says "×3" instead of being one hit.
     *
     * xFrac spreads them left to right; depth is measured outwards from the protected strip (0 = just clear of it, 1 = far
     * edge of the band), so the examples fan away from the middle in both directions and the two bands read as two directions.
     */
    public static List<FctHitState> Build(double w, double h, FctMotionStyle style)
    {
      var hits = new List<FctHitState>(5);

      if (w < FctResize.MinWidth || h < FctResize.MinHeight)
      {
        return hits; // nothing sensible to lay out in a canvas this small; configure mode will show real numbers instead
      }

      /*
       * All five start close to the protected strip (small depth), because that is where real numbers are born and the only
       * placement that lets a fountain number rise, run out of room and turn over — a fall is baked only when the arc wants more
       * height than the band has, so examples placed in the middle of the band would show a style with no fall in it.
       *
       * The vertical spread comes from the phases instead: frozen at 14 % and at 78 % of its own flight, the same starting point
       * puts one example just off the strip and another near the far edge, which is what makes a still image read as motion. The
       * crit sits past its pop (a number caught at 1.25× size would be judging the wrong thing) — and since blowout numbers do not
       * travel at all, its phase controls only size and fade.
       */
      Add(hits, w, h, style, FctLane.Crit, 4217, null, "Kedge", false, false, 1, 0.35, 0.15, 0.06);
      Add(hits, w, h, style, FctLane.DamageDealt, 1180, null, "Backhand", false, false, 1, 0.14, 0.37, 0.02);
      Add(hits, w, h, style, FctLane.Defensive, 0, "DODGE", null, false, false, 1, 0.45, 0.51, 0.04);
      Add(hits, w, h, style, FctLane.HealingReceived, 904, null, "Heal", false, false, 1, 0.62, 0.65, 0.02);
      Add(hits, w, h, style, FctLane.DamageTaken, 1020, null, "Crotbite", true, true, 3, MaxPhase, 0.85, 0.10);

      return hits;
    }

    /* The same construction order as a real hit (style → width estimate → band → travel → tempo) so an example is drawn by the
       identical code path a number takes, which is the only reason it can be trusted to show what the setting does. */
    private static void Add(List<FctHitState> hits, double w, double h, FctMotionStyle style, FctLane lane, double value,
      string fixedText, string source, bool minor, bool periodic, int mergeCount, double phase, double xFrac, double depth)
    {
      var hit = new FctHitState
      {
        Lane = lane,
        Style = style,
        Incoming = FctLayout.IsIncoming(lane),
        Periodic = periodic,
        Value = value,
        FixedText = fixedText,
        Source = source,
        MergeCount = mergeCount,
        PreviewPhase = Math.Clamp(phase, MinPhase, MaxPhase),
      };

      FctStyle.ApplyTo(hit, lane, minor || periodic, false);
      hit.ValueWidth = FctLayout.EstimateTextWidth(fixedText ?? FctText.FormatHit(value, mergeCount), hit.ValueFontSize);

      // the band first: depth is a fraction of it, and Spawn clamps whatever it is given back into range
      FctLayout.Refit(hit, w, h);

      var span = Math.Max(0, hit.BandMaxY - hit.BandMinY);
      var y = hit.Incoming ? hit.BandMinY + (span * depth) : hit.BandMaxY - (span * depth);

      FctLayout.Spawn(hit, w, h, _rand, (w * xFrac, y));
      AssignTempo(hit, style, h);

      hits.Add(hit);
    }

    /*
     * Lifetimes mirror what FctIngest would choose for the style, because a frozen phase is only meaningful if it is a phase of
     * the real flight: fountain and spray get their choreographed window — travel and fade sized to each other, plus the fall that
     * FctIngest bakes for them — while hold and pulse get a travel window plus a hold before the fade. Nothing invents a look: the
     * examples get the same geometry calls in the same order a live hit gets.
     *
     * Deliberately not scaled by FctScale.Time: the examples show what a style looks like, and scaling the whole flight leaves
     * every frozen phase exactly where it was — a slower overlay is something to feel on live numbers over a few seconds, not
     * something a still frame can demonstrate.
     */
    private static void AssignTempo(FctHitState hit, FctMotionStyle style, double h)
    {
      var window = style is FctMotionStyle.Spray ? FctMotion.SprayMotionWindowMs : FctMotion.MotionWindowMs;

      if (style is FctMotionStyle.Fountain or FctMotionStyle.Spray)
      {
        hit.LifetimeMs = window;
        hit.MotionMs = window;
        hit.FadeMs = window * FctMotion.FallPhaseFrac;

        // the same call FctIngest makes for these styles: Spawn deliberately leaves the fall to whoever sets the tempo, so an
        // example that skipped it would be showing a fountain with no turn-over in it
        FctLayout.ApplyFall(hit, h);
        return;
      }

      hit.LifetimeMs = window * 1.4; // a hold long enough that a phase past the travel still shows the number at rest
      hit.MotionMs = Math.Min(window, hit.LifetimeMs);
      hit.FadeMs = Math.Clamp(hit.LifetimeMs * 0.25, 250, 1000);
    }
  }
}
