namespace EQLogParser
{
  /*
   * The seven remembered hues: one dial, seven colours. Same contract as FctScale's dials — process globals set from the
   * staged settings whenever anything applies them (startup and a live preview are the same call), read when a row is
   * styled — so a colour change wears the NEXT spawned number and never tugs one already mid-flight, exactly like the size
   * dial's promise. The shipped truth lives in FctStyle's constants; this starts at that table and always returns to it
   * (Reset), which is also what every test of this engine does through FctAmbient.
   *
   * Values are plain 0xAARRGGBB ints like the rest of the style table, so Skia and WPF draw the same thing and tests
   * assert it; settings.ini speaks hex text, translated by FctOverlaySettings.ParseColor at the boundary. Seven colours is
   * a decision, not a limit that ran out: every word (worked defence, own whiff, Invulnerable/Absorb) shares Words —
   * their ranking survived as SIZE — and per-word hues stay unpicked until the seven are ironed out.
   */
  internal static class FctPalette
  {
    public static int DamageDealt = FctStyle.DamageDealtArgb;
    public static int DamageTaken = FctStyle.DamageTakenArgb;
    public static int Healing = FctStyle.HealingArgb;
    public static int Crit = FctStyle.CritArgb;
    public static int Special = FctStyle.SpecialArgb;
    public static int Words = FctStyle.WordsArgb;
    public static int Source = FctStyle.SourceArgb;

    /* Staged settings become dial state — the one call preview and startup share, so a colour cannot be honoured at the
       panel and ignored by the canvas (or, the way these bugs usually go, saved and never read again). */
    internal static void Apply(FctConfigState state)
    {
      DamageDealt = state.ColorDamageDealt;
      DamageTaken = state.ColorDamageTaken;
      Healing = state.ColorHealing;
      Crit = state.ColorCrit;
      Special = state.ColorSpecial;
      Words = state.ColorWords;
      Source = state.ColorSource;
    }

    /* Back to the shipped table — the way back every Reset (and every per-row picker reset) rides. A default state IS the
       shipped palette, so there is no second list of hues to keep in step. */
    internal static void Reset() => Apply(new FctConfigState());
  }
}
