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
   * Crits are common in EQ (roughly every third number), so their emphasis is the pop and the halo with a SIZE DELTA on top of
   * whatever the same hit would have been — the crit dial, defaulting to +10 % — rather than a fixed tier standing above every
   * lane no matter what that lane's size was set to. Their hue is pushed deeper than dealt damage rather than brighter, because
   * at 1.3x scale a light orange and the yellow it must stand apart from converge. Periodic ticks (DoT/HoT) are
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

    /* Evade words are informational, but they are the only text on screen that carries a sentence's worth of
     * meaning, so they get a tier of their own rather than being treated as tiny damage. */
    public const double DefensiveFontSize = 24;

    // periodic ticks and the player's own misses: readable, but never competing with a direct hit
    public const double MinorFontSize = 23;
    public const double SourceFontMin = 14;

    /*
     * Procs fire on their own schedule, on top of the swing or cast the player was actually watching for — and they are
     * SUBORDINATE BY TEMPO, NOT BY SIZE: a proc lives and travels shorter (FctLifeController's proc tier), which is what
     * keeps a stack of item procs from cluttering the picture. Shaving their glyphs smaller than the hit that provoked
     * them was tried first and read as two different weights of information rather than two different urgencies; the real
     * damage of a proc is its own event, so it wears its lane's full size like everything else in it.
     */

    /* The ability/verb line, deliberately neutral so it never competes with a value colour for meaning. */
    public const int SourceArgb = unchecked(0xF2 << 24 | 0xC6 << 16 | 0xCF << 8 | 0xDA);

    /* Zero-damage labels are hueless: high value against any zone, low value against each other. The player's own
     * whiff sits a step behind a defence that worked, which is the only ranking here worth encoding. */
    public const int WordArgb = unchecked(0xFF << 24 | 0xCF << 16 | 0xE0 << 8 | 0xEA);
    public const int OwnWordArgb = unchecked(0xFF << 24 | 0xAF << 16 | 0xBB << 8 | 0xC6);

    /* Invulnerable and Absorb mean "every cast from here is wasted", so they are the one label allowed to shout. */
    public const int LoudWordArgb = unchecked(0xFF << 24 | 0xF2 << 16 | 0xC9 << 8 | 0x4C);

    /*
     * The marked events (FctSpecial) share ONE hue — epic purple, the genre's "this is rare" colour — because the
     * glyph already answers WHICH event and a per-event hue would just be five more answers to a question the picture
     * does not ask. It sits outside every lane colour (yellow/orange/red/green/whitish words) so a mark cannot be
     * misread as a lane at a glance, and it colours number and glyph alike.
     */
    public const int SpecialArgb = unchecked(0xFF << 24 | 0xC1 << 16 | 0x6B << 8 | 0xFF);

    /* The reserved glyph: a square at most this fraction of the value's own font, hanging outside the number's left edge
       with this gap — both scale with the text dial because they are born from the font size. Half-height and nearly
       touching were chosen by looking at it: the mark is punctuation on the number, and a glyph the height of the digits
       turned every special event into a picture with a caption. */
    public const double IconSizeFrac = 0.48;
    public const double IconGapPx = 2;

    /*
     * How much room the row's whole sideways appetite needs: the glyph, sized off the value it belongs to (so it includes that event's
     * crit delta), plus the hairline gap - a couple of pixels, scaled with the normal dial like every other pixel constant, so the mark
     * neither drifts off its number when text grows nor overlaps it when text shrinks. Reservation and drawing compute this identically;
     * the odometer charges itself to exactly one IconSpan of drift.
     */
    public static double IconSpan(double valueFontSize) => (valueFontSize * IconSizeFrac) + (IconGapPx * FctScale.Text);

    public static void ApplyTo(FctHitState hit, FctLane lane, bool minor)
    {
      var loud = IsLoudLabel(hit.FixedText);

      /* A big event — a crit, or a marked special attack borrowing a crit's emphasis — is written at ITS OWN KIND'S size times the
         CRIT dial, and nothing else: at 0 % a crit is exactly the size the hit would have been (bigger only by its pop), because the
         dial measures the delta, not an absolute. That is why there is no crit tier any more — a fixed "crit font" above every lane
         made "0 %" a lie and put a floor under the emphasis the player is holding a slider for. The kind comes from the flags Accept
         captured before pooling (a crit's pooled lane no longer says heal or taken), so a heal crit rides the healing size, a taken
         crit the damage-taken size, and the normal dial still moves the whole screen underneath: both multipliers are applied here
         and nowhere else, at birth rather than at draw, so hit.ValueFontSize is the real drawn size from this point on — line height,
         vertical reserve, clamp bands, the pulse grid and glyph measurement all follow without a second place that has to remember to
         scale, and a number never resizes mid-flight. */
      var big = lane == FctLane.Crit || hit.Special is not FctSpecial.None;
      var kind = big
        ? hit.Heal ? FctLane.HealingReceived : hit.Incoming ? FctLane.DamageTaken : FctLane.DamageDealt
        : lane;
      var size = ValueSize(kind, minor && !big, loud) * FctScale.Text * (big ? FctScale.Crit : 1.0);

      hit.ValueFontSize = size;
      hit.ValueArgb = ValueArgb(lane, loud);
      hit.SourceFontSize = SourceSize(hit.ValueFontSize);
      hit.SourceArgb = SourceArgb;
      /* Blowout is the engine's whole idea of "this number is a big deal": the pop curve to CritPeakScale and the hold
         at it, the widest extent every clamp and braid measures against, the top draw pass, the halo, the wider spray
         spread. A special attack runs through that SAME lever — assassinate and friends are crit-sized events whether or
         not the log also called them crits — while keeping their lane's column and direction: the pool stays where it
         was born, so a marked backstab still scrolls in the damage-out column, just huge, on top, and purple. */
      hit.Blowout = big;

      /* The mark overrides the lane colour (the event outranks the stream) and reserves its room before any geometry
         reads the width: clamps, collision and stream spacing all charge for the glyph beside the number. */
      if (hit.Special is not FctSpecial.None)
      {
        hit.ValueArgb = SpecialArgb;
        hit.IconAllowance = IconSpan(size);
      }
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

      if (minor && lane is not FctLane.Defensive)
      {
        return MinorFontSize; // periodic tick or own-miss: the smallest numeric tier (big events pass minor=false; see ApplyTo)
      }

      /* Kind lanes only — a big event asks for its KIND's size and wears the crit dial on top (see ApplyTo); the pooled
         crit lane is a colour and a draw order, not a tier, so it deliberately has no case here. */
      return lane switch
      {
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