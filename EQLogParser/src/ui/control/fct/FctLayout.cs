using System;

namespace EQLogParser
{
  /*
   * Which region scheme a canvas lays hits out in. Bands is the default: direction becomes vertical, so text
   * about my targets rises out of the top of the overlay and text about my own body sinks out of the bottom.
   * Halves is the original left/right split, kept as a fallback for a player whose camera and HUD make the
   * vertical axis awkward (toggled on the overlay header, persisted in settings.ini). Both modes keep the
   * middle of the overlay empty; neither needs the player to memorise a colour to know who acted.
   * Rationale: docs/DesignNotes.md → Floating Combat Text.
   */
  internal enum FctLayoutMode
  {
    Bands,
    Halves
  }

  /*
   * Where a hit spawns and how far it may travel — one table for every backend, so a tuning change is one edit
   * instead of a copy-paste pair. The overlay cannot know where the player or their target are on screen, so in
   * both modes a band across the middle stays clear: that is where EQ's own windows sit and where the spell
   * effects being looked at happen (docs/combat-text-overlay-design.md §1).
   */
  internal static class FctLayout
  {
    /* Half-width of text-free space each half gives up along the midline (halves mode). Keeps the player's cast
     * bar, target ring and spell gems readable underneath the overlay. */
    public const double CenterClearance = 64;

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
     * Spray geometry: half-angle of the cone (radians, ~38°) and how much wider a crit's cone opens; SprayMaxLateralFrac
     * caps sideways travel as a share of canvas width because the cone is aimed from wherever the lane slot put the hit
     * — at the overlay's edge a full spread would leave the window.
     *
     * Reach is deliberately longer than a band is deep. Given only the band's own travel budget, even a wide angle moved
     * no further sideways than a lane-slot jitter — measured identical to hold's existing arc, which already swings 12%
     * of width, so spray would have been invisible. With the longer reach, steep draws top out against the band clamp
     * and wide draws get the lateral distance this style exists for.
     */
    public const double SpraySpreadRadians = 0.66;
    public const double SprayCritSpreadFactor = 1.3;
    public const double SprayReachFactor = 1.9;
    public const double SprayMaxLateralFrac = 0.30;

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
    public static double TextReserve(FctHitState hit)
    {
      var block = (hit.ValueFontSize * TextHeightFactor) +
                  (string.IsNullOrEmpty(hit.Source) ? 0 : hit.SourceFontSize * SourceLineFactor);

      // a crit's blowout outranks its style's swell, exactly as it does in FctMotion.ScaleOf
      var peak = hit.Blowout ? FctMotion.CritPeakScale : hit.Style is FctMotionStyle.Pulse ? FctMotion.PulsePeakScale : 1.0;

      return block * peak;
    }

    /*
     * Whether a lane is about something happening to me — the bottom band in bands mode, the left half in halves
     * mode. Crit is deliberately not handled here: ingest reads this from the producing lane before pooling, and
     * stores it on hit.Incoming, which is what keeps a taken crit on the incoming side.
     */
    public static bool IsIncoming(FctLane lane) => lane is FctLane.DamageTaken or FctLane.HealingReceived or FctLane.Defensive;

    public static void Spawn(FctHitState hit, double w, double h, Random rand, FctLayoutMode mode = FctLayoutMode.Bands)
    {
      if (mode == FctLayoutMode.Halves)
      {
        SpawnHalves(hit, w, h, rand);
        return;
      }

      SpawnBands(hit, w, h, rand);
    }

    /*
     * Ballpark width used until the backend measures the real glyph run. Needed at spawn because the clamp band
     * is derived from the text width, and a zero would let the first frames cross into forbidden space.
     */
    public static double EstimateTextWidth(string text, double fontSize) =>
      string.IsNullOrEmpty(text) ? 0 : text.Length * (fontSize * 0.58);

    /*
     * Direction is vertical here, so x is only a lane slot: damage toward the middle of the overlay, healing out
     * wide, crits and labels centred over their band. Both bands carry two categories each, which is why the x
     * slots survive the move away from left/right — they stopped meaning who and now mean what.
     */
    private static void SpawnBands(FctHitState hit, double w, double h, Random rand)
    {
      var cx = hit.Lane switch
      {
        FctLane.HealingDealt or FctLane.HealingReceived => w * 0.63,
        FctLane.Crit => w * 0.52,
        FctLane.Defensive or FctLane.Missed => w * 0.50,
        _ => w * 0.42, // DamageDealt, DamageTaken
      };

      var spread = hit.Blowout ? 0.17 : 0.09;
      hit.X0 = cx + ((rand.NextDouble() * 2 - 1) * (w * spread));

      /* Nothing forbids an outgoing number's x any more, so the arc is only held inside the window; the text
       * half-width allowance is applied on top of these by FctMotion.ArcedX. */
      hit.SideMin = EdgePad;
      hit.SideMax = Math.Max(EdgePad + 1, w - EdgePad);

      /* The band facing the gap gives up the hit's whole drawn height — value, source line and crit pop — so an
       * outgoing hit cannot drop into the protected strip and an incoming one cannot start inside it, and the band
       * edge away from the gap keeps the same reserve against the window border. Both endpoints of the travel are
       * placed inside the band, so nothing crosses during its life; FctMotion clamps anyway because the fountain fall
       * is the same maths asked to do more. */
      var reserve = TextReserve(hit);
      double up;
      if (hit.Incoming)
      {
        ApplyBand(hit, h * GapBottomFrac, Math.Max(h * GapBottomFrac, h - EdgePad - reserve), h);
        hit.Y0 = hit.BandMinY + (BandSpan(hit) * OriginJitterFrac * rand.NextDouble());
        up = -1;  // away from the gap is downward here
      }
      else
      {
        ApplyBand(hit, EdgePad, Math.Max(EdgePad + 1, (h * GapTopFrac) - reserve), h);
        hit.Y0 = hit.BandMaxY - (BandSpan(hit) * OriginJitterFrac * rand.NextDouble());
        up = 1;
      }

      /* A proc starts deeper in its band than the row of hits it arrived beside. Clamped by the band ends, which already
       * carry the vertical reserve on one side and the edge pad on the other, so this cannot push anything into the strip or
       * out of the window — it only ever moves hits away from the strip, further up in my band and further down in theirs. */
      if (hit.Proc)
      {
        var inset = BandSpan(hit) * ProcInsetFrac;
        hit.Y0 = hit.Incoming
          ? Math.Min(hit.BandMaxY, hit.Y0 + inset)
          : Math.Max(hit.BandMinY, hit.Y0 - inset);
      }

      // the far end of the band, minus a random share of slack: exactly how much room this hit has to travel in
      var far = hit.Incoming
        ? hit.BandMaxY - (BandSpan(hit) * TravelSlackFrac * rand.NextDouble())
        : hit.BandMinY + (BandSpan(hit) * TravelSlackFrac * rand.NextDouble());

      AssignTravel(hit, w, rand, up, Math.Abs(hit.Y0 - far));
    }

    /*
     * Travel per motion style, always measured from the origin *away* from the protected strip: `up` is +1 when the hit
     * rises and -1 when it sinks (bands mode's incoming band), and `usable` is how far it may go from where it spawned.
     * Pulse spends none of it, Hold and Fountain all of it in a straight line, Spray trades height for lateral distance
     * inside a cone. Adding a style means adding a branch here and, if it needs gravity, one in AssignLifetime; nothing
     * in a backend changes, which is the point of keeping geometry in one table.
     */
    private static void AssignTravel(FctHitState hit, double w, Random rand, double up, double usable)
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
        hit.Arc = Math.Clamp(reach * Math.Sin(theta), -(w * SprayMaxLateralFrac), w * SprayMaxLateralFrac);
        return;
      }

      hit.Rise = up * usable;
      hit.Arc = (rand.NextDouble() * 2 - 1) * w * (hit.Blowout ? 0.15 : 0.12);
    }

    /* The original split: each half owns its lanes and neither enters the protected centre column. */
    private static void SpawnHalves(FctHitState hit, double w, double h, Random rand)
    {
      var left = hit.Incoming;

      // each side's lanes centered as a pair within their own half (text is drawn center-aligned on x); the busy
      // damage lane takes the inner, center-near slot on both sides, healing the outer one, crits sit at the
      // middle of their half and spread wider
      var cx = hit.Lane switch
      {
        FctLane.DamageTaken => w * 0.35,
        FctLane.HealingReceived => w * 0.14,
        FctLane.Crit => left ? w * 0.25 : w * 0.75,
        // evades ride with their side's damage lane - a miss lands where the whiffed swing would
        FctLane.Defensive => w * 0.35,
        FctLane.Missed => w * 0.65,
        FctLane.HealingDealt => w * 0.86,
        _ => w * 0.65, // DamageDealt
      };

      var spread = hit.Blowout ? 0.17 : 0.09;
      hit.X0 = cx + ((rand.NextDouble() * 2 - 1) * (w * spread));

      // bottom third, but never so low that the drawn block hangs off the window; then rise to a top band
      var lowestStart = Math.Max(EdgePad, h - EdgePad - TextReserve(hit));
      hit.Y0 = Math.Min(h * (0.68 + (rand.NextDouble() * 0.17)), lowestStart);
      AssignTravel(hit, w, rand, up: 1, Math.Max(0, hit.Y0 - (h * (0.05 + (rand.NextDouble() * 0.08)))));

      /* Math.Max/Min keep the band valid on a small overlay: an inverted band used to throw inside Math.Clamp on
       * every frame, and the clamp is what protects the center. */
      var innerLeft = Math.Max(EdgePad + 1, (w / 2) - CenterClearance);
      var innerRight = Math.Min(w - EdgePad - 1, (w / 2) + CenterClearance);

      if (left)
      {
        hit.SideMin = EdgePad;
        hit.SideMax = innerLeft;
      }
      else
      {
        hit.SideMin = innerRight;
        hit.SideMax = w - EdgePad;
      }
    }

    /* Keeps the band drawable: a window short enough to invert it degrades to "inside the edges" rather than to a
     * clamp that throws every frame. */
    private static void ApplyBand(FctHitState hit, double top, double bottom, double h)
    {
      if (bottom > top)
      {
        hit.BandMinY = top;
        hit.BandMaxY = bottom;
        return;
      }

      hit.BandMinY = EdgePad;
      hit.BandMaxY = Math.Max(EdgePad + 1, h - EdgePad);
    }

    private static double BandSpan(FctHitState hit) => Math.Max(0, hit.BandMaxY - hit.BandMinY);
  }
}
