namespace EQLogParser
{
  /*
   * Lane → presentation style. Colors are plain 0xAARRGGBB ints so the table is shared by the Skia and WPF
   * renderers (and assertable from the unit tests) instead of being copied per substrate.
   *
   * Colour answers "what kind of event is this", never "who did it": that question is answered by the band a hit
   * lives in and the direction it travels (FctLayout), and putting a hue on it as well would give two contradictory
   * answers. In particular nothing here is blue — blue reads as mana, arcane damage or a friendly nameplate to
   * anyone coming from another MMO, so it was the wrong sign for "a defence that worked".
   *
   * Crits are common in EQ (roughly every third number), so their emphasis stays a size step up from normal damage
   * plus the pop, not a spectacle; their hue is pushed deeper than dealt damage rather than brighter, because at
   * 1.3x scale a light orange and the yellow it must stand apart from converge. Periodic ticks (DoT/HoT) are
   * deliberately the smallest numeric tier: they are the noisiest stream in the game and the first thing grouping
   * and filtering will target — see docs/combat-text-overlay-design.md §4, docs/DesignNotes.md.
   */
  internal static class FctStyle
  {
    /* Sizes are what the overlay is read at: across a game window, in peripheral vision, while moving. The first
     * pass borrowed web-scale sizes and every tier went up by at least two points once it was looked at in game —
     * the ratios below (crit well above dealt, labels above the smallest numeric tier) survived, the absolute
     * numbers did not. Raising them also forces the vertical reserve in FctLayout.TextReserve to keep up.
     */
    public const double DamageDealtFontSize = 34;
    public const double DamageTakenFontSize = 32;
    public const double HealingFontSize = 28;
    public const double CritFontSize = 40;

    /* Evade words are informational, but they are the only text on screen that carries a sentence's worth of
     * meaning, so they get a tier of their own rather than being treated as tiny damage. */
    public const double DefensiveFontSize = 24;

    // periodic ticks and the player's own misses: readable, but never competing with a direct hit
    public const double MinorFontSize = 23;
    public const double SourceFontMin = 14;

    /*
     * A proc fires on its own schedule, on top of the swing or cast the player was actually watching for, and several
     * items fire several times a pull. It is real damage and stays legible, but it should not outshout the hit that
     * provoked it, so it rides a fraction below its lane's size instead of being pushed down to the periodic tier —
     * 34 becomes about 27 — between a direct hit and the periodic tier, subordinate without being a footnote. A crit proc
     * keeps the full size: the pop is the answer to "did something big happen", and shrinking the loudest number in the
     * log would be a lie.
     */
    public const double ProcSizeFrac = 0.78;

    /* The ability/verb line, deliberately neutral so it never competes with a value colour for meaning. */
    public const int SourceArgb = unchecked(0xF2 << 24 | 0xC6 << 16 | 0xCF << 8 | 0xDA);

    /* Zero-damage labels are hueless: high value against any zone, low value against each other. The player's own
     * whiff sits a step behind a defence that worked, which is the only ranking here worth encoding. */
    public const int WordArgb = unchecked(0xFF << 24 | 0xCF << 16 | 0xE0 << 8 | 0xEA);
    public const int OwnWordArgb = unchecked(0xFF << 24 | 0xAF << 16 | 0xBB << 8 | 0xC6);

    /* Invulnerable and Absorb mean "every cast from here is wasted", so they are the one label allowed to shout. */
    public const int LoudWordArgb = unchecked(0xFF << 24 | 0xF2 << 16 | 0xC9 << 8 | 0x4C);

    public static void ApplyTo(FctHitState hit, FctLane lane, bool minor, bool proc = false)
    {
      var loud = IsLoudLabel(hit.FixedText);

      /* The player's own size preference (FctScale.Text, +/-50 % of measured) goes in here, once, so hit.ValueFontSize is the real drawn
         size from this point on: line height, vertical reserve, clamp bands, the pulse grid and glyph measurement all read it
         and follow without a second place that has to remember to scale. A hit keeps the size it was born with, so moving the
         slider changes what comes next rather than resizing text already in flight. */
      var size = ValueSize(lane, minor, loud) * FctScale.Text;

      // scaled here rather than at draw time so the vertical reserve and the width estimate see the real size
      if (proc && lane is not FctLane.Crit)
      {
        size *= ProcSizeFrac;
      }

      hit.ValueFontSize = size;
      hit.ValueArgb = ValueArgb(lane, loud);
      hit.SourceFontSize = SourceSize(hit.ValueFontSize);
      hit.SourceArgb = SourceArgb;
      hit.Blowout = lane == FctLane.Crit;
    }

    /*
     * The source line rides at 42% of the value size, floored so it stays legible on small hits. The floor scales with the
     * player's size setting too: an unscaled 14 px minimum would be a much bigger share of a -50 % number than it is of a
     * full-size one, and the label under a small hit would end up proportionally larger than the label under a crit.
     */
    public static double SourceSize(double valueFontSize) =>
      valueFontSize * 0.42 > SourceFontMin * FctScale.Text ? valueFontSize * 0.42 : SourceFontMin * FctScale.Text;

    /* The labels that mean "stop casting at this", as written by DamageLineParser into FctHitCommand.ValueText. */
    public static bool IsLoudLabel(string fixedText) => fixedText is Labels.Invulnerable or Labels.Absorb;

    private static double ValueSize(FctLane lane, bool minor, bool loud)
    {
      if (loud)
      {
        return DefensiveFontSize; // a wasted-cast warning is not a footnote, whatever made it
      }

      if (minor && lane is not (FctLane.Crit or FctLane.Defensive))
      {
        return MinorFontSize; // periodic tick or own-miss: the smallest numeric tier
      }

      return lane switch
      {
        FctLane.Crit => CritFontSize,
        FctLane.HealingDealt or FctLane.HealingReceived => HealingFontSize,
        FctLane.DamageDealt => DamageDealtFontSize,
        FctLane.Defensive => DefensiveFontSize,
        FctLane.Missed => MinorFontSize,
        _ => DamageTakenFontSize, // DamageTaken
      };
    }

    private static int ValueArgb(FctLane lane, bool loud) =>
      lane switch
      {
        FctLane.Crit => Argb(0xFF, 0x8A, 0x1E),       // deep orange - away from dealt yellow, not toward taken red
        FctLane.HealingDealt or FctLane.HealingReceived => Argb(0x7F, 0xE0, 0x61), // green
        FctLane.DamageDealt => Argb(0xFF, 0xD7, 0x5E), // yellow
        // hueless labels; amber only where the message is "stop casting", dimmer step for my own whiff
        FctLane.Defensive => loud ? LoudWordArgb : WordArgb,
        FctLane.Missed => loud ? LoudWordArgb : OwnWordArgb,
        _ => Argb(0xFF, 0x6B, 0x5E),                   // red - damage taken
      };

    private static int Argb(int r, int g, int b) => (0xFF << 24) | (r << 16) | (g << 8) | b;
  }
}