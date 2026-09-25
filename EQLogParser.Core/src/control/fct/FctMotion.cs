using System;

namespace EQLogParser
{
  /*
   * Frame maths shared by every FCT backend: where a hit is, how big it is, how opaque it is and what
   * text it shows. Pure functions of (hit, age) so the whole animation is unit-testable without a
   * window — see EQLogParser.Wpf.Test/src/ui/control/FctMotionTest.cs. Rationale for the motion window
   * and for resting in place: docs/DesignNotes.md → Floating Combat Text.
   */
  internal static class FctMotion
  {
    /* Rise+arc finish this early in life, after which the hit holds position and only fades. */
    public const double MotionWindowMs = 2000;

    /*
     * The arc’s cadence in ms per pixel of region height: MSBT scrolls every value in an area at ONE rate
     * (ScrollUp is height × progress), and the chain look — each number following the one before along the same path
     * — needs one shared beat rather than a speed per row, so an arc’s lifetime comes from this and its region’s
     * height rather than from the adaptive controller (FctIngest.ApplyRailTempo). Rows of different font sizes cover
     * slightly different distances within the beat, a few percent apart in rate and invisible; 5 ms/px is ~200 px/s,
     * the pace measured playable at default size, and like MSBT’s a taller region is a longer journey, not faster text.
     */
    public const double ArcScrollMsPerPx = 5;

    /*
     * Spray's choreography is deliberately shorter than fountain's. Shape separates the two styles (see LateralProgress),
     * but timing was the other half of why they read as one thing: two 2 second flights look alike whatever path they draw.
     * Shrapnel is quick — out, over and down in about 1.7 s — which also lets a burst clear before the next one lands.
     */
    public const double SprayMotionWindowMs = 1700;

    /* How far the shrink-out takes a choreographed number by the time it is gone. It rides the fade window and not the
       whole descent: size holds while the number can still be read, and collapses as it dims (see ScaleOf). */
    public const double FallScaleEnd = 0.55;

    /*
     * A fountain's fall depth against the height that same number climbed — k in the ballistic solve at ApexFraction.
     * One is the physically honest answer (a thrown thing comes back to the line it left) and it is also the safe one:
     * falling exactly as far as it rose ends the flight on the spawn point, which is inside the band by construction, so
     * the clamp never bites and nothing can park against an edge. Above one would ask for water below its own nozzle — a
     * clipped fall, then a number sitting still while it fades.
     *
     * This replaced a canvas-relative `H × 0.28`, which is where "the numbers don't really fall" came from: against the
     * 0.47 H band that fall is barely over half the climb, so the number died well above where it was born and the return
     * half of the throw was never drawn.
     */
    public const double FountainFallRiseRatio = 1.0;

    /*
     * How much of the DESCENT is spent fading, which is the same statement as how much of it is lit. The rule this
     * replaced was "the fade spans exactly the fall", and that alone made a fountain look like it hung up and melted: the
     * smootherstep fade leaves a number at 10% opacity a third of the way down, so of a 224 px descent only about 13%
     * was ever visible, and the part that was visible was the slow part.
     *
     * Everything in the genre with real users lights the motion and fades the tail: GW2-SCT holds alpha at 1 until the
     * last 20% of a message's life (src/ScrollArea.cpp, `fadeLength = 0.2f`), MSBT runs scroll and fade as two clocks
     * (MSBTAnimationStyles.lua drives position by progress alone), and even NAG keeps opacity at 1.0 through its whole
     * rise and spends only its last 23% reaching zero. Half the descent is that same rule, priced against a flight that
     * now has a fall worth seeing.
     */
    public const double FallFadeFrac = 0.5;

    /*
     * How much of an incoming hit's sink distance it gets back on the way out, in bands mode where a literal downward
     * fall is not available (see FctIngest.AssignLifetime). Half of it reads as the same overshoot-and-settle your own
     * numbers have, mirrored, without crawling back up into the protected strip.
     */
    public const double IncomingFallsBackFrac = 0.5;

    /*
     * Crit blowout, restyled once crit SIZE became a dial: the number swells IN from just under full size over CritScaleInMs, rests at
     * exactly the size its font says, then collapses away over the tail its own fade spans, so it shrinks out instead of blinking off.
     * The collapse is keyed to hit.FadeMs rather than to a fixed window: a big number must never hold full size into a dim phase nor shrink
     * while still bright. A constant did both ends of that badly at once, and at the one lifetime it was written for — 2800 ms, whose fade works
     * out at 614 ms — the 700 ms shrink began 86 ms BEFORE the dimming, so a crit spent its last tenth of a second collapsing at full brightness.
     *
     * The envelope never exceeds 1.0, and that is the whole point. Size belongs to FctStyle.ApplyTo and to nothing else: the big class is
     * written at its own font, so a pop that also scaled the number above 1 would be a second multiplication stacked on the player's dial -
     * which is exactly how this used to work, and how "crit size at its minimum" still produced numbers far bigger than ordinary hits.
     * Emphasis without arithmetic: growth from below reads as ARRIVAL, while the halo, the hue and the
     * top draw pass were never size in the first place.
     */
    public const double CritScaleInStart = 0.72;
    public const double CritScaleInMs = 90;
    public const double CritScaleEnd = 0.06;

    public const double FadeInMs = 160;

    /*
     * Procs run the whole fight at 70% tempo: travel, hold and fade all shorten together so the number is gone shortly
     * after the swing that provoked it instead of lingering as long as that swing's own text. Scaling only the tail would
     * leave a proc sitting on screen at full size while the hit behind it had already faded. See FctIngest.ApplyProcTempo.
     */
    public const double ProcTimeFrac = 0.7;

    /*
     * Where a hit is along its travel, 0..1. The zero guard is not decoration: MotionMs is allowed to be 0 — a style that
     * arrives in place, a life shorter than its travel, a test asking for the endpoint directly — and dividing by it makes
     * t NaN on the hit's very first frame. NaN survives both Math.Clamp and the band clamps (NaN compares false against every
     * limit), so ArcedX and RaisedY would hand back NaN and the number would simply fail to be drawn. A finished motion is a
     * real answer; NaN is not. FctMotionTest pins it.
     */
    /* A conveyor row is somewhere along its column, not somewhere along its life (FctConveyor): the lane's clock is the
       only thing that moves it, which is exactly how a convoy keeps its spacing. Everywhere else age is still the law. */
    public static double Progress(FctHitState hit, double ageMs) =>
      hit.OnConveyor ? ConveyorProgress(hit)
        : hit.MotionMs > 0 ? Math.Clamp(ageMs / hit.MotionMs, 0.0, 1.0) : 1.0;

    /* How much of a conveyor row's flight is spent leaving: the last stretch of rail before it is off, shared by the fade
       and the crit's collapse so the two always arrive together. Everything before it is full opacity — this is the mode
       somebody chooses in order to read every number, so a row that has been on screen for half a second in the middle of
       a column is not allowed to be dim there. */
    public const double ConveyorFadeOutFrac = 0.12;

    /* The first few pixels of a row's trip are its arrival rather than a fade-in: short enough to read as appearing, long
       enough that nothing pops into existence mid-column. */
    private const double ConveyorFadeInPx = 24;

    /*
     * Freeze style: ease out along the hit's travel (up for outgoing, down for an incoming hit in bands mode, whose
     * Rise is negative) and then hold. Fountain and spray fly instead: one parabola through spawn, apex and end of life.
     * FallDist is signed the way Rise is: positive accelerates toward the bottom of the screen, negative back up toward
     * the gap, which is how the incoming band gets a mirrored fountain instead of parking against its own bottom edge.
     *
     * A projectile, not two eased halves stitched together at an apex. It leaves the spawn at its fastest — a fountain
     * throws, and MSBT's 87 px/s and GW2-SCT's 90 px/s crawls are not the pace this overlay was measured at — decelerates
     * at one constant rate to zero vertical speed at the apex, and accelerates on at that same rate for the rest of its
     * life. The apex is therefore the only still point in it, which is what an apex is; and a number that fell as far as
     * it rose is travelling downward at death exactly as fast as it left. Position, velocity and acceleration are all
     * continuous end to end because there is one curve: the old climb-then-ease-in handoff had no velocity kink but
     * reversed its acceleration in a single frame, which is why the descent read as starting from parked rather than as
     * already having been falling.
     *
     * The band clamp still stands behind it for the cases where geometry and flight disagree — a resize that moves the
     * gap under a number already in flight, or a fall asked for more room than the band has. At FountainFallRiseRatio 1
     * neither can happen: the flight ends where it began.
     */
    public static double RaisedY(FctHitState hit, double t) => ClampedToBand(hit, TravelledY(hit, t));

    /*
     * Where in life the flight peaks, as a share of it — the closed form of the ballistic endpoint solve. With k the fall
     * depth over the climb, requiring height(1) = Rise − Fall gives 1 − 2a = −(1 − k)a², whose root below one is
     * a = 1/(1 + √k). Equal rise and fall peak at the half, because they are the same journey run twice; a shallow fall
     * peaks late and leaves the climb most of the clock. Magnitudes only, which is why an outward fountain and its
     * mirrored incoming twin get the same shape — the sign lives on Rise and FallDist, and reflecting a flight does not
     * move its apex.
     *
     * Degenerate cases answer instead of dividing: with no climb there is nothing to peak at, so the whole life belongs to
     * the fall and TravelledY's s never divides by an apex of zero.
     */
    public static double ApexFraction(FctHitState hit)
    {
      var climb = Math.Abs(hit.Rise);
      if (climb <= 0.0 || double.IsNaN(climb))
      {
        return 1.0;
      }

      return 1.0 / (1.0 + Math.Sqrt(Math.Abs(hit.FallDist) / climb));
    }

    /* The share of life the descent owns — what FctIngest prices the fade against, since a fade is sized to the fall and
       not to the whole flight. */
    public static double DescentFrac(FctHitState hit) => 1.0 - ApexFraction(hit);

    private static double TravelledY(FctHitState hit, double t)
    {
      if (hit.FallDist == 0.0)
      {
        /* The arc scrolls at constant vertical speed — that is the shape: with y linear in t and x quadratic in t,
           x is a function of y², which is why MSBT's maths names it a parabola and the panel names it arc. Everything else eases. */
        var v = FctMotionStyles.IsRail(hit.Style) ? t : Ease(t);
        return hit.Y0 - (hit.Rise * v);
      }

      /* One projectile for the whole flight. Height is v0·τ − ½g·τ²; pinning v0 and g to "peak at a·T" collapses it to
         Rise·(2s − s²) in s = t/a, and height(1) then comes out as Rise·(1 − k) — rose Rise, fell Fall — precisely because
         ApexFraction solved for the a that makes that true. Nothing is tuned per phase, so the shape cannot drift away
         from physics: k is the only input this curve has. */
      if (hit.Rise == 0.0)
      {
        /* Dropped rather than thrown: with no climb there is no apex to peak at, so it accelerates away from rest along
           the fall's own sign for the whole life. ApplyFall cannot make this — it derives depth from the climb — but a
           hand-built hit or a resize that flattens one can, and the silent answer before was a number that did not move. */
        return hit.Y0 + (hit.FallDist * t * t);
      }

      var s = t / ApexFraction(hit);
      return hit.Y0 - (hit.Rise * ((2 * s) - (s * s)));
    }

    /* A layout with no vertical limit leaves the band at 0..0; clamping against that would pin every hit to the
     * top of the canvas, so an unset band means "no vertical clamp". */
    private static double ClampedToBand(FctHitState hit, double y) =>
      hit.BandMaxY > hit.BandMinY ? Math.Clamp(y, hit.BandMinY, hit.BandMaxY) : y;

    /*
     * Where the value's CENTRE is at time t, for every style, from one rule:
     *
     * Values are an odometer. The spine X0+lateral carries the value's RIGHT
     * edge (its LEFT edge on a column that turned its rows around to lean left — FctHitState.HangRight), so a 950 and a 12,040 in the same column line their ones digit up under each other instead of their
     * middles — Mik's "right-justify" text alignment, the one every parse-heavy player turns on. It ships as the
     * hidden default with no knob: centre-aligning a number column is never what anyone wants. The clamp keeps the
     * WHOLE drawn box left of SideMax and right of SideMin — the box grows LEFTWARDS out of the rail, which is also how a crit blowout pops: the rail
     * holds and the number punches wider, so the animated scale belongs to the renderer (FctMotion.ScaleOf) and never to this answer. Placement scoring
     * calls the same function the canvas does, so the odometer the player sees and the one the collision cost measures are one number.
     *
     * Degenerate case — a label wider than its territory degrades to the territory's middle instead of throwing.
     */
    public static double ArcedX(FctHitState hit, double t)
    {
      /* The drawn block's reach at its widest, from the rail: the value's own box, the reserved special-event glyph, and the source label
         wherever the label side put it — all of it leaves the rail, none of it moves the rail itself. Clamping against the digits alone is what
         let an inline "(Glormok)" be painted across the column boundary (FctLayout.BlockFromRail). */
      var (blockLeft, otherSide) = FctLayout.BlockFromRail(hit, hit.SourceWidth);

      /* The bow is MSBT's geometry as written — x = y²/4a measured from the rail's mid-point, which with y linear
         in t comes out as 4·t(1−t) off the column: a number leaves its column straight up or down, bows out to the
         vertex at half height, and comes BACK to the column by the time it fades. Ends on the column, arc between them:
         that is the semicircle chain in Mik's demos, and it is also why a lane keeps its spacing for the whole
         flight — a shared bow cancels in every difference. (The old t² drift here was a curve with its vertex at the
         spawn: outward forever, never back, and it read as nothing in the genre.) */
      var lateral = FctMotionStyles.IsRail(hit.Style) ? hit.Bow * 4 * t * (1 - t) : hit.Sway * LateralProgress(hit, t);


      var loR = hit.SideMin + blockLeft; // the whole box fits, growing leftwards from the rail
      var hiR = hit.SideMax - otherSide;
      if (loR > hiR)
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
      var rail = Math.Clamp(hit.X0 + lateral, loR, hiR);

      /* Rail to centre, and the hang says which way: an odometer's digits hang LEFT of its rail, but a column that leans left turns
         them around so they hang RIGHT, into the air the lean is spending. That is the entire alignment change — the halo, the special-
         event mark and every label seat are placed from this centre (FctSkiaCanvas), so a mirrored row keeps its internal arrangement
         exactly and only swaps which side of the rail that arrangement sits on. */
      return hit.HangRight ? rail + (hit.ValueWidth / 2.0) : rail - (hit.ValueWidth / 2.0);
    }

    /*
     * How far along its sideways travel a hit is, and the rule is the same one the vertical runs on: a number that is
     * airborne keeps its horizontal speed. The parabola a projectile draws comes from the vertical accelerating while the
     * sideways run stays constant, so anything with a fall (fountain, spray) takes x linear in t.
     *
     * Spray used to run laterally on an ease-out instead, and when the vertical still eased that was the only thing that made
     * a cone read as a cone at all — one curve on both axes draws a straight line from origin to apex, so mid-flight a wide
     * spray was a slanted fountain. Now that the vertical really does accelerate, easing x spends the whole sideways distance
     * before the descent begins: measured at an 800 px canvas the fan's outer numbers had already stopped moving across and
     * fell straight down for the last 40% of their lives, and the widest point of the flight was its middle rather than its
     * end. The "kink you can see" that the ease-out was defending against was a lateral that stops at the apex; a constant
     * sideways velocity never stops, so there is nothing left to hide.
     *
     * Freeze claims no gravity on either axis, so it keeps one eased curve throughout: x and y stay in proportion and its
     * drawn point never leaves the straight line between spawn and apex. Rails are not projectiles at all and answer for
     * themselves.
     */
    private static double LateralProgress(FctHitState hit, double t) => hit.FallDist != 0.0 ? t : Ease(t);

    /* Crit pop or the fall-phase shrink. 1 when neither applies. */
    public static double ScaleOf(FctHitState hit, double ageMs)
    {
      if (hit.Blowout)
      {
        return hit.OnConveyor ? ConveyorBlowoutScale(hit, ageMs) : BlowoutScale(hit, ageMs);
      }


      /* Choreographed numbers shrink out rather than blink out, but on the FADE's clock and not the descent's: while a
         number is bright it keeps the size its font was given, and it collapses as it dims. Measured against the whole
         fall it competed with the motion for the same attention — a thing getting smaller reads as a thing standing still,
         which was the other half of why the old fountain seemed to hang at the top of its climb. */
      if (hit.FallDist != 0.0 && hit.FadeMs > 0.0)
      {
        var fadeStart = hit.LifetimeMs - hit.FadeMs;
        if (ageMs > fadeStart)
        {
          var u = Math.Clamp((ageMs - fadeStart) / hit.FadeMs, 0.0, 1.0);
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
      if (hit.OnConveyor)
      {
        return ConveyorOpacity(hit);
      }

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
      var text = hit.FixedText is null
        ? FctText.FormatHit(hit.Value, hit.MergeCount, hit.Heal)
        : FctText.FormatWord(hit.FixedText, hit.MergeCount);
      if (text == hit.DisplayText)
      {
        return;
      }

      hit.DisplayText = text;
      hit.TextDirty = true;
    }

    /* Swell in from CritScaleInStart, rest at exactly the size the font says (scale 1.0), collapse out over the fade. The rest is 1.0 rather
       than a hold above it because this curve is emphasis only: how big the number IS was decided once, by its class and dial, at birth. A hit
       with no fade of its own (a hand-built fixture, a style that never dims) collapses across its last frame instead of hanging at full size. */
    private static double BlowoutScale(FctHitState hit, double ageMs)
    {
      if (ageMs < CritScaleInMs)
      {
        return CritScaleInStart + ((1.0 - CritScaleInStart) * (ageMs / CritScaleInMs));
      }

      var fade = Math.Max(hit.FadeMs, 1.0);
      if (ageMs < hit.LifetimeMs - fade)
      {
        return 1.0;
      }

      var p = Math.Clamp((ageMs - (hit.LifetimeMs - fade)) / fade, 0.0, 1.0);
      return 1.0 - ((1.0 - CritScaleEnd) * p * p);
    }

    /* Where a conveyor row is along its own column: pixels travelled over pixels of flight, and negative while it waits
       behind the mouth for the slot it bought (clamped to 0, so a queued row sits at the edge — invisible, because opacity
       below returns 0 for it, and folding still finds it). */
    private static double ConveyorProgress(FctHitState hit) =>
      hit.ConveyorTravel > 0 ? Math.Clamp(hit.ConveyorQ / hit.ConveyorTravel, 0.0, 1.0) : 1.0;

    /* Both ends eased like every other style's, but measured on the rail: a conveyor row's position comes from the lane, so
       a fade keyed to its age would disagree with where it is the moment the lane speeds up — and the complaint that made
       this whole mode was numbers leaving before they had been read. */
    private static double ConveyorOpacity(FctHitState hit)
    {
      var q = hit.ConveyorQ;
      if (q <= 0)
      {
        return 0.0;
      }

      var travel = Math.Max(hit.ConveyorTravel, 1.0);
      var o = q < ConveyorFadeInPx ? Ease(q / ConveyorFadeInPx) : 1.0;
      var outAt = travel * (1.0 - ConveyorFadeOutFrac);
      if (q > outAt)
      {
        o *= 1.0 - Ease((q - outAt) / Math.Max(1.0, travel - outAt));
      }

      return Math.Clamp(o, 0.0, 1.0);
    }

    /* The big class on a conveyor: swell in on the clock like anywhere else (arrival belongs to the moment the row appears,
       which is when its slot reaches the mouth), rest at the size the font says, then collapse over the same last stretch of
       rail the fade spans. Measuring the collapse against the lane rather than against a lifetime is what keeps a crit that
       queued for a moment from arriving already shrunk and dying in the middle of the column. */
    private static double ConveyorBlowoutScale(FctHitState hit, double ageMs)
    {
      if (ageMs < CritScaleInMs)
      {
        return CritScaleInStart + ((1.0 - CritScaleInStart) * (ageMs / CritScaleInMs));
      }

      var outAt = 1.0 - ConveyorFadeOutFrac;
      var t = ConveyorProgress(hit);
      if (t <= outAt)
      {
        return 1.0;
      }

      var p = Math.Clamp((t - outAt) / (1.0 - outAt), 0.0, 1.0);
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