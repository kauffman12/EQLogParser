using System;

namespace EQLogParser
{
  /*
   * Where a hit spawns and how far it may travel — one table for every backend, so a tuning change is
   * one edit instead of a copy-paste pair. Incoming lanes live in the left half, outgoing in the right,
   * and neither ever enters the protected center band: the overlay does not know where the player or
   * their target is on screen, so the middle stays clear (docs/combat-text-overlay-design.md §1).
   */
  internal static class FctLayout
  {
    /* Half-width of text-free space each half gives up along the midline. Keeps the player's cast bar,
     * target ring and spell gems readable underneath the overlay. */
    public const double CenterClearance = 64;

    // breathing room at the outer canvas edges
    public const double EdgePad = 8;

    /*
     * Half of the canvas a feed lane belongs to. Crit is deliberately not handled here: the ingest
     * captures the half from the producing lane before pooling, which is what hit.LeftSide carries.
     */
    public static bool IsLeftSide(FctLane lane) => lane is FctLane.DamageTaken or FctLane.HealingReceived or FctLane.Defensive;

    /*
     * Fills in spawn position, travel and the side clamp band. Call after FctStyle.ApplyTo: the spread
     * keys off Blowout. The half comes from hit.LeftSide, which the caller captured before pooling so a
     * taken crit stays on the incoming side.
     */
    public static void Spawn(FctHitState hit, double w, double h, Random rand)
    {
      var left = hit.LeftSide;

      // home band: each side's lanes centered as a pair within their own half (text is drawn
      // center-aligned on x); the busy damage lane takes the inner, center-near slot on both sides,
      // healing the outer one; crits sit at the middle of their half and spread wider
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

      /* Math.Max/Min keep the band valid on a small overlay: an inverted band used to throw inside
       * Math.Clamp on every frame, and the clamp is what protects the center. */
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

    /*
     * Ballpark width used until the backend measures the real glyph run. Needed at spawn because the
     * clamp band is derived from the text width, and a zero would let the first frames cross the center.
     */
    public static double EstimateTextWidth(string text, double fontSize) =>
      string.IsNullOrEmpty(text) ? 0 : text.Length * (fontSize * 0.58);
  }
}
