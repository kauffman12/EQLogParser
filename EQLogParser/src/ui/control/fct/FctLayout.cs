using System;

namespace EQLogParser
{
  /*
   * Where a hit spawns and how far it may travel — one table for every backend, so a tuning change is one edit
   * instead of a copy-paste pair. Direction is vertical: text about my targets rises out of the top of the overlay and
   * text about my own body sinks out of the bottom, with a band across the middle kept clear because that is where EQ's
   * own windows sit and where the spell effects being looked at happen (docs/combat-text-overlay-design.md §1).
   *
   * There are two region schemes, and which one is live is a FctStage: bands (the original — the vertical direction above
   * is its invariant) and halves (two side-by-side streams, the genre standard, with per-side direction). Halves was removed
   * once, as a fallback, for two reasons that are now both fixed on the region rather than on the styles: every geometry
   * below is measured against the hit's own region (FctCellGrid included — the old collapse was columns measured against
   * canvas width), and halves has no protected strip to fall into because the regions do not overlap, so the choreographed
   * falls bounce off the far end of their own half instead. Rationale for both: docs/DesignNotes.md → Floating Combat Text.
   */
  internal static class FctLayout
  {
    // breathing room at the outer canvas edges
    public const double EdgePad = 8;

    /*
     * The protected strip in bands mode, as fractions of canvas height. Outgoing text lives above GapTopFrac and
     * rises away from it, incoming text lives below GapBottomFrac and sinks away from it: because both travel
     * outwards the strip is empty by construction instead of by clamping traffic out of it, which is also why a
     * number never has to cross the overlay to be understood as "mine" or "at me".
     */
    public const double GapTopFrac = 0.47;
    public const double GapBottomFrac = 0.55;

    /* Share of a band spent on spawn jitter, and share left unused at the far end as breathing room. */
    private const double OriginJitterFrac = 0.12;
    private const double TravelSlackFrac = 0.10;

    /*
     * Procs stack up in the middle of a fight and read as one blurred column when they share a row with the hits they
     * accompany, so a proc starts further *out* from the protected strip: my procs higher up, procs landing on me lower
     * down. The two streams then sit in different rows of the same band and the eye can ignore one while reading the other.
     * Clamped by the band ends, which already carry the text reserve on the gap-facing side and the edge pad on the other,
     * so a cramped overlay loses separation before it loses text. Pulse mode does not need this at all — FctCellGrid gives
     * procs their own block of cells outright.
     */
    public const double ProcInsetFrac = 0.15;

    /*
     * Spray geometry: half-angle of the cone (radians, ~49°) and how much wider a crit's cone opens; SprayMaxLateralFrac
     * caps sideways travel as a share of canvas width because the cone is aimed from wherever the lane slot put the hit
     * — at the overlay's edge a full spread would leave the window. The cap has to move with the angle or the widest draws all
     * stop at the same wall and the fan comes out flat; 0.34 was set against that, by measuring how much of the cone survived
     * the clamp.
     *
     * Reach is deliberately longer than a band is deep. Given only the band's own travel budget, even a wide angle moved
     * no further sideways than a lane-slot jitter — measured identical to hold's existing arc, which already swings 12%
     * of width, so spray would have been invisible. With the longer reach, steep draws top out against the band clamp
     * and wide draws get the lateral distance this style exists for.
     */
    public const double SpraySpreadRadians = 0.85;
    public const double SprayCritSpreadFactor = 1.25;
    public const double SprayReachFactor = 1.9;
    public const double SprayMaxLateralFrac = 0.34;

    /*
     * How far gravity brings a sprayed number back after its apex, as a share of the height it actually reached — not
     * of the canvas and not of the cone's speed. That choice is the invariant: falling less than it rose means no
     * angle in the cone can return a number to the band edge it started from, so the protected strip stays clear by
     * construction for every random draw rather than for the lucky ones.
     */
    public const double SprayFallFrac = 0.4;

    /*
     * How much vertical space a drawn hit needs below its anchor, as multiples of the font sizes involved: y is the
     * top of the value text, and what actually gets drawn is the value (baseline around 0.82 em, plus descenders),
     * optionally the parenthesised source line under it. Factor rather than measured glyph metrics because the band
     * has to exist at spawn time, before a backend has built any text — see EstimateTextWidth for the same trade.
     */
    public const double TextHeightFactor = 1.35;
    public const double SourceLineFactor = 1.25;

    /*
     * Vertical space a hit occupies below its y anchor, including whatever pop its style gives it: DrawHit scales about a
     * pivot partway down the value, so the largest scale the hit will ever reach is applied to the whole block, which
     * over-reserves slightly and never clips. Reserving one em instead is what let incoming hits — which travel downwards
     * and finish their life at the bottom of the band — run their descenders and source line off the edge of the overlay.
     */
    public static double TextReserve(FctHitState hit) => TextHeight(hit) * PeakScaleOf(hit);

    /*
     * The drawn block at full size: the value plus its source line, with no scale applied. FctPlacement measures live hits
     * against each other at moments partway through a flight, where a crit is smaller or larger than its peak.
     */
    public static double TextHeight(FctHitState hit) =>
      (hit.ValueFontSize * TextHeightFactor) +
      (string.IsNullOrEmpty(hit.Source) ? 0 : hit.SourceFontSize * SourceLineFactor);

    /*
     * Where a lane's column sits across the overlay, in bands — the "what" carrier there: damage toward the middle of the
     * band, healing out wide, crits and labels centred. Halves has none of these columns (one stream per side; type is read
     * from colour, size and the heal sign), so halves callers never ask it. Kept in this class because Spawn and FctPlacement
     * both need it, and a placement search that invented its own columns would be a second layout pretending not to be one.
     */
    public static double LaneSlot(FctLane lane, double w) => lane switch
    {
      FctLane.HealingDealt or FctLane.HealingReceived => w * 0.63,
      FctLane.Crit => w * 0.52,
      FctLane.Defensive or FctLane.Missed => w * 0.50,
      _ => w * 0.42, // DamageDealt, DamageTaken
    };

    /* The largest a hit's block ever gets — the crit blowout outranks the style's swell, as it does in FctMotion.ScaleOf. */
    private static double PeakScaleOf(FctHitState hit) =>
      hit.Blowout ? FctMotion.CritPeakScale : hit.Style is FctMotionStyle.Pulse ? FctMotion.PulsePeakScale : 1.0;

    /*
     * Whether a lane is about something happening to me — the bottom band. Crit is deliberately not handled here: ingest
     * reads this from the producing lane before pooling, and stores it on hit.Incoming, which is what keeps a taken crit
     * on the incoming side.
     */
    public static bool IsIncoming(FctLane lane) => lane is FctLane.DamageTaken or FctLane.HealingReceived or FctLane.Defensive;

    /*
     * Ballpark width used until the backend measures the real glyph run. Needed at spawn because the clamp band
     * is derived from the text width, and a zero would let the first frames cross into forbidden space.
     */
    public static double EstimateTextWidth(string text, double fontSize) =>
      string.IsNullOrEmpty(text) ? 0 : text.Length * (fontSize * 0.58);

    /*
     * Direction is vertical, so x is only a lane slot: damage toward the middle of the overlay, healing out wide, crits
     * and labels centred over their band. Both bands carry two categories each, which is why the x slots survived the move
     * away from left/right — they stopped meaning who and now mean what.
     *
     * `origin` belongs to FctPlacement: an explicitly requested launch point instead of the layout's own throw, still run
     * through every clamp below and still given travel appropriate to where it ended up. Candidates go through this function so
     * that a search can never invent a position the layout would forbid — the band, its reserve against the protected strip and
     * the window edges apply to a requested origin exactly as they do to a random one.
     */
    public static void Spawn(FctHitState hit, double w, double h, Random rand, (double X, double Y)? origin = null)
      => Spawn(hit, FctStage.Bands(w, h), rand, origin);

    /*
     * Everything a number needs before it can move: where it starts (x and y), how far it may go (Rise/Arc, via
     * AssignTravel) and the clamp band it stays inside (via Refit). All of it comes out of the hit's stage — bands and halves
     * are one code path that differs in which rect owns the side and which sign its travel has, not two layouts.
     */
    public static void Spawn(FctHitState hit, FctStage stage, Random rand, (double X, double Y)? origin = null)
    {
      var region = stage.RegionFor(hit);
      var territory = stage.TerritoryFor(hit);

      /* Halves and by type have no lane columns — one stream per side, so every lane spawns in the side's own centre. Bands keeps them. */
      var cx = stage.Mode is not FctLayoutMode.Bands ? region.X + (region.Width / 2) : LaneSlot(hit.Lane, stage.W);

      hit.X0 = origin is null ? cx + ((rand.NextDouble() * 2 - 1) * (territory * (hit.Blowout ? 0.17 : 0.09))) : origin.Value.X;

      /*
       * Clamped here as well as at draw time by FctMotion.ArcedX, which must hold anyway for a resize mid-flight. The reason to
       * do it here too is candour: two candidates that both end up pinned against a window edge are one position, scored as two
       * they read as empty space — the damage column sits left of centre, so its wide draws went off the left edge first, and
       * numbers started their flight from the screen border with a sway carrying them inland. That is where "why is that hit over
       * there" comes from. In halves the walls are the half's own edges, which is what keeps a stream inside its territory.
       */
      var sideMargin = (hit.ValueWidth * PeakScaleOf(hit)) / 2;
      var xLo = region.X + EdgePad + sideMargin;
      var xHi = region.X + region.Width - EdgePad - sideMargin;
      hit.X0 = xHi <= xLo
        ? region.X + (region.Width / 2)        // text wider than its territory: nothing to place, so centre it there
        : Math.Clamp(hit.X0, xLo, xHi);

      var up = stage.UpFor(hit.Incoming);
      Refit(hit, stage);

      /* The spawn edge is whichever end the side starts from: down-travelling numbers start at the top of their band and
       * rising ones at the bottom. Bands arrives here with out-up/in-down, so this is the old rule expressed through the sign;
       * halves reads the same two lines for whatever direction each side was given. */
      hit.Y0 = up > 0
        ? hit.BandMaxY - (BandSpan(hit) * OriginJitterFrac * rand.NextDouble())
        : hit.BandMinY + (BandSpan(hit) * OriginJitterFrac * rand.NextDouble());

      /* An explicitly requested origin skips the depth jitter but not the travel: how far a number may go depends on where it
       * started, and FctPlacement asks for origins precisely so it can compare whole flights, not just resting spots. */
      if (origin.HasValue)
      {
        hit.Y0 = Math.Clamp(origin.Value.Y, hit.BandMinY, hit.BandMaxY);
      }

      /* A proc starts deeper in its band than the row of hits it arrived beside — deeper being further along the travel,
       * away from the spawn edge. Clamped by the band ends, which already carry the vertical reserve on one side and the
       * edge pad on the other, so this cannot push anything out of the window; in bands it is the old "further from the
       * strip" rule, and in halves there is no strip to measure from. The parabola takes no depth start: its whole claim
       * is that every value follows the previous one along one path (FctStream), and a proc beginning part-way down the
       * rail breaks that chain for the sake of a distinction its smaller type already makes. */
      if (hit.Proc && hit.Style is not FctMotionStyle.Parabola)
      {
        var inset = BandSpan(hit) * ProcInsetFrac;
        hit.Y0 = up > 0 ? Math.Max(hit.BandMinY, hit.Y0 - inset) : Math.Min(hit.BandMaxY, hit.Y0 + inset);
      }

      /* The far end of the band, minus a random share of slack: exactly how much room this hit has to travel in — and
       * no two numbers the same, which is what stops hold-style rows parking in one another. The parabola takes no
       * slack: its rail is defined by shared endpoints, one per region and direction, so every value stops where the
       * one before it stopped and the chain reads as a train rather than as N separate journeys. */
      var far = hit.Style is FctMotionStyle.Parabola
        ? (up > 0 ? hit.BandMinY : hit.BandMaxY)
        : up > 0
          ? hit.BandMinY + (BandSpan(hit) * TravelSlackFrac * rand.NextDouble())
          : hit.BandMaxY - (BandSpan(hit) * TravelSlackFrac * rand.NextDouble());

      // a parabola bows away from the seam: outward is the genre's shape and the only drift that cannot reach the other
      // side's stream. Bands has no seam, so the sign is moot there (ingest will not run a parabola in it).
      var bowDir = region.X + (region.Width / 2) < stage.W / 2 ? -1.0 : 1.0;
      AssignTravel(hit, territory, rand, up, Math.Abs(hit.Y0 - far), bowDir);
    }

    /*
     * A hit's vertical band and its sideways clamp for a given canvas size: derived from the size and from how tall this text is,
     * never from where the hit happens to be, so asking twice with the same size answers the same thing.
     *
     * Spawn calls it to place a new number. FctResize calls it when the window changes size under a number already in flight,
     * which is the difference between a resize and a resize that leaves most of the overlay's numbers drawing where the old window
     * used to be — travel is a pure function of (hit, age), so nothing else ever revisits these bounds.
     *
     * The band facing the gap gives up the hit's whole drawn height — value, source line and crit pop — so an outgoing hit cannot
     * drop into the protected strip and an incoming one cannot start inside it, and the band edge away from the gap keeps the same
     * reserve against the window border. Both endpoints of the travel sit inside the band, so nothing crosses during its life;
     * FctMotion clamps anyway because the fountain fall is the same maths asked to do more.
     */
    public static void Refit(FctHitState hit, double w, double h)
      => Refit(hit, FctStage.Bands(w, h));

    public static void Refit(FctHitState hit, FctStage stage)
    {
      /* The arc's own bounds; the text half-width allowance is applied on top of these by FctMotion.ArcedX. Halves measures them
       * against the half, which is what keeps a sideways sway inside one stream instead of reaching across the seam. */
      var region = stage.RegionFor(hit);
      hit.SideMin = region.X + EdgePad;
      hit.SideMax = Math.Max(region.X + EdgePad + 1, region.X + region.Width - EdgePad);

      var reserve = TextReserve(hit);
      if (stage.Mode is FctLayoutMode.Bands)
      {
        if (hit.Incoming)
        {
          ApplyBand(hit, stage.H * GapBottomFrac, Math.Max(stage.H * GapBottomFrac, stage.H - EdgePad - reserve),
            EdgePad, Math.Max(EdgePad + 1, stage.H - EdgePad));
        }
        else
        {
          ApplyBand(hit, EdgePad, Math.Max(EdgePad + 1, (stage.H * GapTopFrac) - reserve),
            EdgePad, Math.Max(EdgePad + 1, stage.H - EdgePad));
        }
        return;
      }

      /* Halves: the whole height of the side's own half is travel space — there is no strip inside it. The bottom end carries
       * the text reserve in both schemes, because the drawn block hangs down from its anchor and the bottom edge is what it
       * must not leave. A window short enough to invert the band degrades to the region inset by its own edge pad, never to a
       * clamp that throws every frame. */
      var top = region.Y + EdgePad;
      ApplyBand(hit, top, Math.Max(top + 1, region.Y + region.Height - EdgePad - reserve),
        top, Math.Max(top + 1, region.Y + region.Height - EdgePad));
    }

    /*
     * Travel per motion style, always measured from the origin *away* from the protected strip: `up` is +1 when the hit
     * rises and -1 when it sinks (bands mode's incoming band), and `usable` is how far it may go from where it spawned.
     * Pulse spends none of it, Hold and Fountain all of it in a straight line, Spray trades height for lateral distance
     * inside a cone. Adding a style means adding a branch here and, if it needs gravity, one in AssignLifetime; nothing
     * in a backend changes, which is the point of keeping geometry in one table.
     */
    /*
     * The territory parameter is what the sideways amounts measure against — canvas in bands, half-width in halves — so
     * "12% of width" keeps meaning 12% of this stream's own territory in both schemes.
     */
    /* How far a parabola bows from its column at the vertex of its arc — half height — as a share of the side's
     * territory, entering and leaving on the column either way (FctMotion.ArcedX). MSBT's own curve swings a full area
     * width, text running off the side of its area while still fading; this keeps the arc inside the half where it can
     * be read instead, 0.34 being as far out as the widest crit draw still clears both walls at the vertex
     * (FctHalvesTest pins the containment). */
    public const double ParabolaBowFrac = 0.34;

    private static void AssignTravel(FctHitState hit, double territory, Random rand, double up, double usable, double bowDir)
    {
      if (hit.Style is FctMotionStyle.Pulse)
      {
        // the whole style: appear, swell, settle and fade exactly where it landed
        hit.Rise = 0;
        hit.Arc = 0;
        return;
      }

      if (hit.Style is FctMotionStyle.Spray)
      {
        var theta = SpraySpreadRadians * (rand.NextDouble() * 2 - 1) * (hit.Blowout ? SprayCritSpreadFactor : 1.0);
        var reach = usable * SprayReachFactor;

        // height is capped at what the band offers, which is what keeps every angle of the cone out of the strip
        hit.Rise = up * Math.Min(usable, reach * Math.Cos(theta));
        hit.Arc = Math.Clamp(reach * Math.Sin(theta), -(territory * SprayMaxLateralFrac), territory * SprayMaxLateralFrac);
        return;
      }

      if (hit.Style is FctMotionStyle.Parabola)
      {
        /* The shape: a straight vertical scroll at one constant rate with a symmetric arc — out to the vertex at half
         * height and back to the column — whose size is a share of the region, so resize and the speed dial keep working
         * on it untouched. No jitter anywhere in this branch, and none in the far end that produced `usable`: two values
         * a beat apart are meant to trace the same line at the same speed, one behind the other (FctStream). */
        hit.Rise = up * usable;
        hit.Arc = 0;
        hit.Bow = bowDir * territory * ParabolaBowFrac;
        return;
      }

      hit.Rise = up * usable;
      hit.Arc = (rand.NextDouble() * 2 - 1) * territory * (hit.Blowout ? 0.15 : 0.12);
    }

    /*
     * The gravity tail of the choreographed styles, and note the sign runs opposite to Rise because it is screen-relative:
     * positive falls toward the bottom of the screen, negative back up toward the protected strip, which is how the incoming
     * band mirrors an outgoing fountain instead of parking it against its own bottom edge. Hold and pulse have no fall and
     * are left at zero.
     *
     * Depth itself is deliberately not one number. Spray measures against the height this particular number reached —
     * falling less than it rose is what stops any angle of the cone from returning a number to the band edge it left, so the
     * strip stays clear for every random draw rather than for the lucky ones. A mirrored fountain uses a share of how far it
     * sank; an outgoing one takes the canvas-relative throw it has always had, held honest by the band clamp.
     */
    public static void ApplyFall(FctHitState hit, double h)
      => ApplyFall(hit, FctStage.Bands(0, h));

    /*
     * The sign follows the travel, which is how both schemes get the same shape: a number that rose gets its fall back down
     * (screen-positive), one that sank gets it back up. In bands the outgoing rise falls a canvas-relative h*0.28 — the strip
     * is behind it and the bottom edge is clamped — while every sink, in both schemes, bounces a fixed share of the distance
     * it already spent: in halves there is no strip on either side, so a literal screen-down fall at the bottom of a down-
     * travelling half would park against its own edge exactly as badly as it did in the old incoming band. Fountain and spray
     * always carry a nonzero Rise (spawn and far end are distinct), which is what the sign is read from.
     */
    public static void ApplyFall(FctHitState hit, FctStage stage)
    {
      if (hit.Style is not (FctMotionStyle.Fountain or FctMotionStyle.Spray))
      {
        return;
      }

      var rose = hit.Rise >= 0;
      var depth = hit.Style is FctMotionStyle.Spray ? Math.Abs(hit.Rise) * SprayFallFrac
        : stage.Mode is not FctLayoutMode.Bands || !rose
          ? Math.Abs(hit.Rise) * FctMotion.IncomingFallsBackFrac
          : stage.H * 0.28;

      hit.FallDist = rose ? depth : -depth;
    }

    /* Keeps the band drawable: a window short enough to invert it degrades to the region's own edge pad rather than to a
     * clamp that throws every frame. */
    private static void ApplyBand(FctHitState hit, double top, double bottom, double fallbackTop, double fallbackBottom)
    {
      if (bottom > top)
      {
        hit.BandMinY = top;
        hit.BandMaxY = bottom;
        return;
      }

      hit.BandMinY = fallbackTop;
      hit.BandMaxY = Math.Max(fallbackTop + 1, fallbackBottom);
    }

    private static double BandSpan(FctHitState hit) => Math.Max(0, hit.BandMaxY - hit.BandMinY);
  }
}
