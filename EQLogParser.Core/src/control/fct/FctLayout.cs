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
   * is its invariant, and the protected strip lives in the middle of the canvas) and split (the genre's side-by-side layout,
   * where each category owns a COLUMN rather than a half and the seam carries no protected band). Everything below is measured
   * against the hit's own region — which is what both schemes are: one layout asked and answered per rect, not two code paths
   * (the old collapse was columns measured against canvas width, and there was no far end for a fall to bounce off).
   * Rationale for both: docs/DesignNotes.md → Floating Combat Text.
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
     * so a cramped overlay loses separation before it loses text.
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
     * no further sideways than a lane-slot jitter — measured identical to freeze's existing sway, which already swings 12%
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
    /*
     * How much vertical room a row is charged, as a multiple of its value's font: 1.2 em, what typographers call leading — the point at which
     * an ascender and the descender above it stop being neighbours without the line below reading as cramped. It was 1.35, two lines of web
     * leading carried over from the first pass, and the extra quarter existed to protect the halo — the wrong thing to reserve air for. A halo
     * is a translucent bloom: two of them meeting brighten a seam rather than hide a number, while the gap they bought ate a fifth of every
     * column and made four ledger columns scroll before they held a fight. Rows whose class swells on arrival (crits) still pay for the swell,
     * because the reserve is taken at PEAK scale (TextReserve) and the lane prices spacing from that same peak (FctConveyor).
     */
    public const double TextHeightFactor = 1.2;
    public const double SourceLineFactor = 1.25;

    /*
     * Where "(source)" sits relative to its amount (FctLabelSide): the canvas owns the choice and stamps it when settings load,
     * because it is geometry twice over, not typography. Vertically it decides what a ROW is — inline, the words share the value's
     * baseline, so a row is exactly as tall as its number and nothing else, which is why moving the labels to the side tightens every
     * column on screen; below, the label is a second line and must be paid for in the same currency the queue spaces rows with.
     * Horizontally it decides where the DRAWN BLOCK reaches — below, the words can overhang either side of their amount; left, they join
     * the value in hanging off the odometer rail; right, they open a reach on the side that had none. Both answers come from here, or the
     * engine spaces and clamps for an arrangement nobody drew.
     */
    internal static FctLabelSide LabelSide = FctLabelSide.Below;

    internal static bool LabelsBelow => LabelSide is FctLabelSide.Below;

    /*
     * Vertical space a hit occupies below its y anchor, including whatever pop its style gives it: DrawHit scales about a
     * pivot partway down the value, so the largest scale the hit will ever reach is applied to the whole block, which
     * over-reserves slightly and never clips. Reserving one em instead is what let incoming hits — which travel downwards
     * and finish their life at the bottom of the band — run their descenders and source line off the edge of the overlay.
     */
    public static double TextReserve(FctHitState hit) => TextHeight(hit) * PeakScaleOf(hit);

    /*
     * The widest block a lane can be asked to draw: a crit-class number at the size its dial allows — six of the widest digit with a thousands
     * comma — plus room for the mark a special event hangs outside its edge. A column's spine and its arc are decided against THIS rather than
     * against whichever number happened to arrive first, because every row on a lane has to share one rail and trace one path. Judged per arriving
     * number, a wide crit spends the whole column on its own glyphs, finds no room left for an arc, and goes up the screen in a dead straight line
     * while everything around it curves. Sized from the class and the dials rather than measured, so it is one value for every row of every column
     * (docs/DesignNotes.md).
     */
    internal static double RailReserve()
    {
      var critSize = FctStyle.DamageDealtFontSize * FctScale.Crit;
      return EstimateTextWidth(RailReserveDigits, critSize) + FctStyle.IconSpan(critSize);
    }

    private const string RailReserveDigits = "999,999";

    /*
     * The drawn block at full size: the value plus its source line, with no scale applied. FctPlacement measures live hits
     * against each other at moments partway through a flight, where a crit is smaller or larger than its peak.
     */
    public static double TextHeight(FctHitState hit) =>
      (hit.ValueFontSize * TextHeightFactor) +
      ((LabelsBelow && !string.IsNullOrEmpty(hit.Source)) ? hit.SourceFontSize * SourceLineFactor : 0);

    /* The word-space between an inline label and its amount, in source-font sizes. DrawHit places with this and geometry charges it here,
       so there is one gap rather than two opinions of it that drift apart the moment anybody edits one.

       It used to be half a source font, which is prose spacing — right for a sentence, wasteful between an amount and its parenthetical, where the
       bracket already says where the name begins. Every pixel here is a fraction of a letter the column will not show, so it came down to a third:
       still clear of the digits' own halo at shipped sizes, about half a character back per row. */
    public const double LabelGapFrac = 0.32;

    /*
     * Forty characters: as long as a name gets to be, and nothing more. It is a ceiling and not a target — the room a column has (FitSource) decides
     * first, so in a narrow window or beside a wide crit names still come out shorter — but it stopped at thirty while the overlay's first-run size was
     * a guess at a small screen, and at 2048 px wide a split column can carry fifty. A ceiling below what the layout can pay for is the one way to cut
     * a name that was never in anyone's way, which is precisely the complaint this whole fitting exists to answer.
     */
    internal const int MaxSourceChars = 40;

    /* The shortest fitted name worth drawing: below this an ellipsis costs more than the letters it replaces, and "(…)" names nothing. */
    private const int MinSourceChars = 3;

    private const string Ellipsis = "\u2026";

    internal static double LabelGap(FctHitState hit) => hit.SourceFontSize * LabelGapFrac;

    /* A cut that lands on a space or a comma would hang the ellipsis off nothing — "(Champion of …)" reads as a typo rather than as a name with
       the rest withheld — so a cut walks back to the last real character. Only ever at a cut, never to a whole name, because a name that happens
       to end in punctuation is the log's own and should be drawn as written. */
    private static string Cut(string text) => text.TrimEnd(' ', ',', '.');

    /*
     * How far the drawn block reaches either side of the value's CENTRE: its own measured box at a given blowout scale, the special-event
     * glyph hanging outside the left edge, and the source label wherever the label side put it. Anything testing this row against a wall or
     * another row charges these numbers, so no "(Glormok)" gets drawn through a column boundary — and the reach is deliberately asymmetric,
     * because a right-aligned amount already hangs its whole width one way: an inline label on that side deepens a reach the row had anyway,
     * while on the other it opens one it did not have.
     */
    internal static (double Left, double Right) BlockAboutCentre(FctHitState hit, double sourceWidth, double scale)
    {
      var half = (hit.ValueWidth * scale) / 2.0;
      var left = half + hit.IconAllowance;
      var right = half;

      if (sourceWidth > 0)
      {
        var words = sourceWidth * scale;
        if (LabelsBelow)
        {
          // a second line is centred under its amount, so it can overhang either side by half the difference between the two
          left = Math.Max(left, words / 2.0);
          right = Math.Max(right, words / 2.0);
        }
        else if (LabelSide is FctLabelSide.Left)
        {
          left = half + LabelGap(hit) + words;
        }
        else
        {
          right = half + LabelGap(hit) + words;
        }
      }

      return (left, right);
    }

    /* The same block from the odometer RAIL, which is where every horizontal clamp lives — and this is the arithmetic that decides whether two
     * numbers in one column line up, because a rail row carries its whole value box to the LEFT of the rail (FctMotion.ArcedX). Each label side is
     * therefore translated on its own terms rather than shifted symmetrically:
     *
     * - inline: it hangs outside the value on its side, so that side pays gap + words and the other keeps the box.
     * - below: the second line is centred under its AMOUNT, whose centre sits a half-width left of the rail — so it reaches past the value's left
     *   edge by words/2 − valueHalf... and past its right edge by exactly as much, measured from there, NOT from the rail. Charging words/2 against
     *   the rail instead (which this used to do) bills the outer side for a label that is not standing there: with a long spell name it asked for
     *   86 px of clearance and with "(Crush)" for none, so each row pinned itself against its own wall and the column's numbers — 46 px apart in a
     *   user's screenshot — were never going to line up. A centred word line is charged about the value's centre, which is where it is drawn. */
    internal static (double Left, double Right) BlockFromRail(FctHitState hit, double sourceWidth, double scale)
    {
      var valueWidth = hit.ValueWidth * scale;
      var left = valueWidth + hit.IconAllowance;   // the whole amount, plus any mark hung outside its left edge
      var right = 0.0;

      if (sourceWidth <= 0)
      {
        return (left, right);
      }

      var words = sourceWidth * scale;
      if (LabelsBelow)
      {
        left = Math.Max(left, (valueWidth / 2.0) + (words / 2.0));
        right = Math.Max(0.0, (words / 2.0) - (valueWidth / 2.0));
      }
      else if (LabelSide is FctLabelSide.Left)
      {
        left += LabelGap(hit) + words;
      }
      else
      {
        right = LabelGap(hit) + words;
      }

      return (left, right);
    }

    /*
     * Half-width for the tests that space rows against each other about the centre — the stream's scorer, which decides how far apart two
     * rows in one column have to be. It charges the words when they sit BESIDE their amount, because at that height a wide "(Glormok)" is
     * exactly as intrusive as a wide number and the row it belongs to no longer reserves a line for it. When they go BELOW they are already
     * paid for vertically (TextHeight charges the second line, so nothing can overlap it), and charging them sideways as well would double-bill
     * the same pixels: rows would braid into neighbouring columns to dodge a label that was never in their way. The column boundary still sees
     * the overhang — ArcedX and Spawn clamp against BlockFromRail either way, which is what keeps a long word out of the next stream.
     */
    internal static double BlockHalf(FctHitState hit)
    {
      var (left, right) = BlockAboutCentre(hit, LabelsBelow ? 0 : hit.SourceWidth, 1.0);
      return Math.Max(left, right);
    }

    /*
     * The source name this row can actually draw, shortened only when its column genuinely runs out of room. Two bounds do the work:
     * MaxSourceChars, past which letters stop being useful, and whatever width survives after the amount itself. Width arrives as a function
     * because there are two honest measurers — an estimate at spawn, before any font exists, and the real glyphs in the canvas, whose answer is
     * the one that decides what the player sees. The full name always stays on the hit (FctHitState.Source), so cutting is a decision re-taken
     * whenever the room changes (a resize, a font dial, another label side) rather than damage done once: widen the window and the name comes
     * back by itself.
     */
    /* The room a label has is not "the column minus the number": it is whatever the rail leaves on EACH side, because that is where each label side
     * draws. A below line may overhang to either hand from under its amount; an inline one lives entirely on one side and cannot borrow from the other.
     * Asking the geometry rather than a caller for that answer also makes a name's budget a property of the COLUMN — every row of a lane is placed on
     * one spine (Spawn), so every row of a lane cuts its names at the same width instead of a crit losing letters before the parry above it does. */
    internal static void FitSource(FctHitState hit, Func<string, double, double> widthOf)
      => FitSource(hit, hit.X0 - hit.SideMin, hit.SideMax - hit.X0, widthOf);

    /* Fitting against a stated total, for callers (and tests) that have no placed row to ask. */
    internal static void FitSource(FctHitState hit, double room, Func<string, double, double> widthOf)
      => FitSource(hit, room, 0, widthOf);

    private static void FitSource(FctHitState hit, double leftRoom, double rightRoom, Func<string, double, double> widthOf)
    {
      if (string.IsNullOrEmpty(hit.Source))
      {
        hit.SourceLabel = null;
        hit.SourceWidth = 0;
        return;
      }

      /* The ceiling, applied once. The ellipsis goes on here rather than only in the trimming loop below, because a slice that happens to end on a
         word boundary — "…Champion of Frost" at exactly forty characters — reads as a whole name that merely has a long ending, and the player is left
         believing the log said "Frost". A cut that does not look like a cut is a wrong fact on screen, which is worse than a clipped one. */
      var name = hit.Source.Length > MaxSourceChars ? Cut(hit.Source[..MaxSourceChars]) + Ellipsis : hit.Source;

      while (true)
      {
        var label = $"({name})";
        var width = widthOf(label, hit.SourceFontSize);
        var (left, right) = BlockFromRail(hit, width, 1.0);

        // unplaced rows (no rail yet: SideMin/SideMax are zero) fall back to the total-block test against whatever room was stated
        var fits = rightRoom > 0
          ? left <= leftRoom && right <= rightRoom
          : left + right <= Math.Max(0, leftRoom);

        if (fits || name.Length <= MinSourceChars)
        {
          hit.SourceLabel = label;
          hit.SourceWidth = width;
          return;
        }

        // two characters a pass: names are short and the room is measured, but a wide-script name should not need twenty rounds
        var trimmed = name.EndsWith(Ellipsis, StringComparison.Ordinal) ? name[..^Ellipsis.Length] : name;
        name = trimmed.Length <= MinSourceChars ? trimmed : Cut(trimmed[..^2]) + Ellipsis;
      }
    }


    /*
     * Where a lane's column sits across the overlay, in bands — the "what" carrier there: damage toward the middle of the
     * band, healing out wide, crits and labels centred. Split has no use for them (its columns ARE the x assignment, and they
     * come from FctStage.AnchoredCentre rather than from here), so its callers never ask. Kept in this class because Spawn and FctPlacement
     * both need it, and a placement search that invented its own columns would be a second layout pretending not to be one.
     */
    public static double LaneSlot(FctLane lane, double w) => lane switch
    {
      FctLane.HealingDealt or FctLane.HealingReceived => w * 0.63,
      FctLane.Crit => w * 0.52,
      FctLane.Defensive or FctLane.Missed => w * 0.50,
      _ => w * 0.42, // DamageDealt, DamageTaken
    };

    /* The largest a hit's block ever gets beyond its measured font. Only the pulse overshoots: the big class carries its size in the
       font itself (its dial, applied once at birth) and its pop swells in from below, so there is nothing left to price above 1.0. */
    private static double PeakScaleOf(FctHitState hit) =>
      1.0; // no shipped style overshoots its measured font: only the deleted pulse pop ever did

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
     * AssignTravel) and the clamp band it stays inside (via Refit). All of it comes out of the hit's stage — bands and split
     * are one code path that differs in which rect owns the number and which sign its travel has, not two layouts.
     */
    public static void Spawn(FctHitState hit, FctStage stage, Random rand, (double X, double Y)? origin = null)
    {
      var region = stage.RegionFor(hit);
      var territory = stage.TerritoryFor(hit);

      /* Split has no lane columns — a category owns its whole lane, so every lane spawns on that lane's spine. Bands keeps them. */
      var cx = stage.Mode is not FctLayoutMode.Bands ? region.X + (region.Width / 2) : LaneSlot(hit.Lane, stage.W);

      hit.X0 = origin is null ? cx + ((rand.NextDouble() * 2 - 1) * (territory * (hit.Blowout ? 0.17 : 0.09))) : origin.Value.X;

      /*
       * Clamped here as well as at draw time by FctMotion.ArcedX, which must hold anyway for a resize mid-flight. The reason to
       * do it here too is candour: two candidates that both end up pinned against a window edge are one position, scored as two
       * they read as empty space — the damage column sits left of centre, so its wide draws went off the left edge first, and
       * numbers started their flight from the screen border with a sway carrying them inland. That is where "why is that hit over
       * there" comes from. In split the walls are the lane's own edges, which is what keeps a column's numbers inside the
       * territory that was set aside for them.
       */
      /* X0 is the right-align RAIL (FctMotion.ArcedX), so on a travelling row the reserved margin sits entirely on the
         left: the whole drawn box — at its widest, a crit's peak — hangs left of the rail, and the rail itself only has
         to stay inside the territory. */
      /* The whole drawn block and not just the number: with the words inline they are the widest part of the row, and charging only the amount
         is how a source name came to be drawn across the seam into the neighbouring column. FitSource has already refused the names this column
         cannot carry; this keeps the ones it accepted inside their territory at every scale they will ever draw at. */
      var peak = PeakScaleOf(hit);
      double reachLeft, reachRight;
      if (FctMotionStyles.IsRail(hit.Style))
      {
        /* One spine per column, placed for the widest AMOUNT the lane can roll and for nothing else — not this row's width, and emphatically not
         * this row's WORDS. Two numbers arriving in the same column routinely carry different labels (one hit "(Crush)", another "(Ethereal Fire XIII
         * Rk. III)") and a label is an annotation of its own row: let it into the spine's arithmetic and every row pins itself against its own wall,
         * which is how a column of right-aligned numbers ends up 46 px out of line with itself. What the spine leaves over is the label's budget, and
         * FitSource cuts names to exactly that — so a column also gets the second, quieter benefit of cutting every name at the same width rather than
         * cutting a crit's name sooner than the parry above it. The max() is containment insurance for a row wider than the estimate (an extreme size
         * dial), where drawing through the wall would be worse than one row's spine sitting slightly further in. */
        var (valueLeft, valueRight) = BlockFromRail(hit, 0, peak);
        reachLeft = Math.Max(RailReserve(), valueLeft);
        reachRight = valueRight;
      }
      else
      {
        (reachLeft, reachRight) = BlockFromRail(hit, hit.SourceWidth, peak);
      }

      /* Which way this column bows: toward the outer edge, decided by the region rather than the row so a lane never contains two shapes. */
      var bowOut = region.X + (region.Width / 2) < stage.W / 2 ? -1.0 : 1.0;

      var xLo = region.X + EdgePad + reachLeft;
      var xHi = region.X + region.Width - EdgePad - reachRight;

      /* The arc also wants its bend reserved beside the spine (RailReserve above gives the width; this gives the curve). Without the bow room
       * the widest rows find none left and climb in a straight line, which AssignTravel then caps to whatever survives. Where a region cannot offer
       * both, containment wins — xHi below takes the clamp — and every row on that column bows equally less. */
      if (hit.Style is FctMotionStyle.Arc)
      {
        var want = territory * ArcBowFrac;
        if (bowOut < 0)
        {
          xLo = Math.Max(xLo, region.X + EdgePad + RailReserve() + want);
        }
        else
        {
          xHi = Math.Min(xHi, region.X + region.Width - EdgePad - reachRight - want);
        }
      }

      hit.X0 = xHi <= xLo
        ? region.X + (region.Width / 2)        // text wider than its territory: nothing to place, so centre it there
        : Math.Clamp(hit.X0, xLo, xHi);

      var up = stage.UpFor(hit);
      Refit(hit, stage);

      /* The spawn edge is whichever end the side starts from: down-travelling numbers start at the top of their band and
       * rising ones at the bottom. Bands arrives here with out-up/in-down, so this is the old rule expressed through the sign;
       * split reads the same two lines for whatever direction each category was given. */
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
       * strip" rule, and in split it is simply deeper inside the lane. The arc takes no depth start: its whole claim
       * is that every value follows the previous one along one path, and a proc beginning part-way down the rail breaks
       * that chain for the sake of a distinction its smaller type already makes. */
      /* The same reason exempts an explicitly requested origin (FctPlacement.Pin), which is how a conveyor row enters at the
         mouth of its column (FctConveyor): that queue bought this row's spacing against the exact edge it entered at, so an inset
         here would spend part of a neighbour's gap — and a lane whose rows do not share one starting edge cannot space anything. */
      if (hit.Proc && hit.Style is not FctMotionStyle.Arc && !origin.HasValue)
      {
        var inset = BandSpan(hit) * ProcInsetFrac;
        hit.Y0 = up > 0 ? Math.Max(hit.BandMinY, hit.Y0 - inset) : Math.Min(hit.BandMaxY, hit.Y0 + inset);
      }

      /* The far end of the band, minus a random share of slack: exactly how much room this hit has to travel in — and
       * no two numbers the same, which is what stops freeze rows parking in one another. The arc takes no
       * slack: its rail is defined by shared endpoints, one per region and direction, so every value stops where the
       * one before it stopped and the chain reads as a train rather than as N separate journeys. */
      var far = FctMotionStyles.IsRail(hit.Style)
        ? (up > 0 ? hit.BandMinY : hit.BandMaxY)
        : up > 0
          ? hit.BandMinY + (BandSpan(hit) * TravelSlackFrac * rand.NextDouble())
          : hit.BandMaxY - (BandSpan(hit) * TravelSlackFrac * rand.NextDouble());

      // outward is the genre's shape, and the only drift that cannot reach the other side's stream (bowOut; bands has no seam, so the sign there is moot)
      AssignTravel(hit, territory, rand, up, Math.Abs(hit.Y0 - far), bowOut);
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
      /* The arc's own bounds; the text half-width allowance is applied on top of these by FctMotion.ArcedX. Split measures them
       * against the lane, which is what keeps a sideways sway inside the column that owns it instead of reaching into its
       * neighbour's pixels. */
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

      /* Split: the whole height of the lane is travel space — there is no strip inside it. The bottom end carries
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
     * Freeze spends all of it once and then stands still, Fountain runs all of it in a straight line, Spray trades height for
     * lateral distance inside a cone, and the rails take the whole distance at one rate (FctConveyor prices the spacing).
     * Adding a style means adding a branch here and, if it needs gravity, one in AssignLifetime; nothing
     * in a backend changes, which is the point of keeping geometry in one table.
     */
    /*
     * The territory parameter is what the sideways amounts measure against — canvas in bands, one lane in split — so
     * "12% of width" keeps meaning 12% of the rect this number actually owns, in either scheme.
     */
    /* How far an arc bows from its column at its vertex — half height — as a share of the side's
     * territory, entering and leaving on the column either way (FctMotion.ArcedX). MSBT's own curve swings a full area
     * width, text running off the side of its area while still fading; this keeps the arc inside the lane where it can
     * be read instead, 0.34 being as far out as the widest crit draw still clears both walls at the vertex
     * (FctArcTest pins the containment). It is a wish rather than a promise: a column too narrow to hold its widest number and this much
     * curve takes less, uniformly for every row on it — Spawn reserves the room, AssignTravel caps what is left of it — but never nothing
     * merely because THAT row happened to be wide. */
    public const double ArcBowFrac = 0.34;

    private static void AssignTravel(FctHitState hit, double territory, Random rand, double up, double usable, double bowDir)
    {

      if (hit.Style is FctMotionStyle.Spray)
      {
        var theta = SpraySpreadRadians * (rand.NextDouble() * 2 - 1) * (hit.Blowout ? SprayCritSpreadFactor : 1.0);
        var reach = usable * SprayReachFactor;

        // height is capped at what the band offers, which is what keeps every angle of the cone out of the strip
        hit.Rise = up * Math.Min(usable, reach * Math.Cos(theta));
        hit.Arc = Math.Clamp(reach * Math.Sin(theta), -(territory * SprayMaxLateralFrac), territory * SprayMaxLateralFrac);
        return;
      }

      if (FctMotionStyles.IsRail(hit.Style))
      {
        /* The shape: a straight vertical scroll at one constant rate with a symmetric arc — out to the vertex at half
         * height and back to the column — whose size is a share of the region, so resize and the speed dial keep working
         * on it untouched. No jitter anywhere in this branch, and none in the far end that produced `usable`: two values
         * a beat apart are meant to trace the same line at the same speed, one behind the other (the conveyor keeps that gap;
         * FctMotion keeps that shape). Straight runs every word of that with the bow taken out — the train needs no arc to exist. */
        hit.Rise = up * usable;
        hit.Arc = 0;
        hit.Bow = hit.Style is FctMotionStyle.Arc ? bowDir * territory * ArcBowFrac : 0.0;

        /* The value hangs its FULL drawn width left of the rail (right-alignment, FctMotion.ArcedX), so a bow toward the outer wall needs that much
         * room or the vertex clips — and a clipped vertex is a flight whose scored shape is not the shape. The outward side is capped against
         * RailReserve rather than this row's own width, deliberately: every row of a lane has to trace ONE path, so how far it bends is a property of
         * the column — whose spawn already left that much room — and not of how many digits this particular hit rolled. A crit and the parry above it
         * therefore bend identically even though one is twice as wide. ArcedX still clamps each row by its own block, as the resize safety net. */
        if (hit.SideMax > hit.SideMin && hit.X0 > 0)
        {
          var (valueLeft, valueRight) = BlockFromRail(hit, 0, PeakScaleOf(hit));   // words excluded: see Spawn
          hit.Bow = hit.Bow < 0
            ? Math.Min(0.0, Math.Max(hit.Bow, -(hit.X0 - hit.SideMin - Math.Max(RailReserve(), valueLeft))))
            : Math.Max(0.0, Math.Min(hit.Bow, hit.SideMax - hit.X0 - valueRight));
        }
        return;
      }

      hit.Rise = up * usable;
      hit.Arc = (rand.NextDouble() * 2 - 1) * territory * (hit.Blowout ? 0.15 : 0.12);
    }

    /*
     * The gravity tail of the choreographed styles, and note the sign runs opposite to Rise because it is screen-relative:
     * positive falls toward the bottom of the screen, negative back up toward the protected strip, which is how the incoming
     * band mirrors an outgoing fountain instead of parking it against its own bottom edge. Freeze has no fall and
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
     * it already spent: in split there is no strip on either side, so a literal screen-down fall at the bottom of a down-
     * travelling lane would park against its own edge exactly as badly as it did in the old incoming band. Fountain and spray
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