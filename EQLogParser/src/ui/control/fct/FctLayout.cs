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
      hit.Arc = (rand.NextDouble() * 2 - 1) * w * (hit.Blowout ? 0.15 : 0.12);

      /* Nothing forbids an outgoing number's x any more, so the arc is only held inside the window; the text
       * half-width allowance is applied on top of these by FctMotion.ArcedX. */
      hit.SideMin = EdgePad;
      hit.SideMax = Math.Max(EdgePad + 1, w - EdgePad);

      /* Y0 is the top of the value text, so the band facing the gap gives up one em: an outgoing hit may not
       * drop into the protected strip and an incoming one may not start inside it. Both endpoints of the travel
       * are placed inside the band, so nothing crosses during its life; FctMotion clamps anyway because the
       * fountain fall is the same maths asked to do more. */
      var em = Math.Max(8, hit.ValueFontSize);
      if (hit.Incoming)
      {
        ApplyBand(hit, h * GapBottomFrac, h - EdgePad - em, h);
        hit.Y0 = hit.BandMinY + (BandSpan(hit) * OriginJitterFrac * rand.NextDouble());

        // negative Rise: FctMotion computes Y0 - (Rise * ease), so incoming sinks away from the gap
        hit.Rise = hit.Y0 - (hit.BandMaxY - (BandSpan(hit) * TravelSlackFrac * rand.NextDouble()));
      }
      else
      {
        ApplyBand(hit, EdgePad, (h * GapTopFrac) - em, h);
        hit.Y0 = hit.BandMaxY - (BandSpan(hit) * OriginJitterFrac * rand.NextDouble());
        hit.Rise = hit.Y0 - (hit.BandMinY + (BandSpan(hit) * TravelSlackFrac * rand.NextDouble()));
      }
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
      hit.Y0 = h * (0.68 + (rand.NextDouble() * 0.17));                    // bottom third
      hit.Rise = hit.Y0 - (h * (0.05 + (rand.NextDouble() * 0.08)));       // finish in a top band, fade out there
      hit.Arc = (rand.NextDouble() * 2 - 1) * w * (hit.Blowout ? 0.15 : 0.12);

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
