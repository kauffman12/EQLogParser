namespace EQLogParser
{
  /*
   * Lane → presentation style. Colors are plain 0xAARRGGBB ints so the table is shared by the Skia and
   * WPF renderers (and assertable from the unit tests) instead of being copied per substrate.
   *
   * Crits are common in EQ (roughly every third number), so their emphasis stays a size step up from
   * normal damage plus color/glow, not a spectacle. Periodic ticks (DoT/HoT) are deliberately the
   * smallest numeric tier: they are the noisiest stream in the game and the first thing grouping and
   * filtering will target — see docs/combat-text-overlay-design.md §4.
   */
  internal static class FctStyle
  {
    public const double DamageDealtFontSize = 31;
    public const double DamageTakenFontSize = 29;
    public const double HealingFontSize = 25;
    public const double CritFontSize = 35;
    public const double DefensiveFontSize = 21; // evade words are informational, not damage

    // periodic ticks and the player's own misses: readable, but never competing with a direct hit
    public const double MinorFontSize = 20;
    public const double SourceFontMin = 12;

    /* The ability/verb line, always a little dimmer than the value. */
    public const int SourceArgb = unchecked(0xF2 << 24 | 0x6E << 16 | 0x93 << 8 | 0xC8);

    public static void ApplyTo(FctHitState hit, FctLane lane, bool minor)
    {
      hit.ValueFontSize = ValueSize(lane, minor);
      hit.ValueArgb = ValueArgb(lane);
      hit.SourceFontSize = SourceSize(hit.ValueFontSize);
      hit.SourceArgb = SourceArgb;
      hit.Blowout = lane == FctLane.Crit;
    }

    /* The source line rides at 42% of the value size, floored so it stays legible on small hits. */
    public static double SourceSize(double valueFontSize) => valueFontSize * 0.42 > SourceFontMin ? valueFontSize * 0.42 : SourceFontMin;

    private static double ValueSize(FctLane lane, bool minor)
    {
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

    private static int ValueArgb(FctLane lane) =>
      lane switch
      {
        FctLane.Crit => Argb(0xFF, 0xA3, 0x2E),       // orange
        FctLane.HealingDealt or FctLane.HealingReceived => Argb(0x7F, 0xE0, 0x61), // green
        FctLane.DamageDealt => Argb(0xFF, 0xD7, 0x5E), // yellow
        FctLane.Defensive => Argb(0x4F, 0xA8, 0xE8),   // blue - a defense that worked for me
        FctLane.Missed => Argb(0x9A, 0xA3, 0xAD),      // dim gray - my own whiff, informational only
        _ => Argb(0xFF, 0x6B, 0x5E),                   // red - damage taken
      };

    private static int Argb(int r, int g, int b) => (0xFF << 24) | (r << 16) | (g << 8) | b;
  }
}
