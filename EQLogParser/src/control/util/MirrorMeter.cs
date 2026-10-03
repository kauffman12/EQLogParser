using EQLogParser;

namespace EQLogParser
{
  /*
   * The one place that knows WHICH engine the damage meter is reading.
   *
   * While both engines exist there is exactly ONE dial for both surfaces: `EnableCombatMirror`, the same word the fight
   * LIST docks on. That is what makes the meter's visibility, reset and open-yourself-on-a-pull rule agree with the list:
   * one flip, one place to change it. (The old pair, list + `OverlayDamageFromMirror`, let a meter paint derived numbers
   * while its open/close rules still came from legacy state - a half-ported surface that looked finished.)
   *
   * The word is burn-in scaffolding, not a permanent feature: when the legacy engine is deleted it goes with it, and the
   * fight window simply docks and toggles like the old one did - no settings entry. So nothing here should learn to do
   * anything interesting in OFF mode beyond "the legacy path runs exactly as before".
   *
   * Nothing in this class decides policy. The quiet window is the meter's dial (`OverlayDamageMode`, 0 = "on kill") and the
   * live question is answered over derived rows in LiveFights; what lives here is only the seam between them.
   */
  internal static class MirrorMeter
  {
    // Read LIVE off the property MainWindow's toggle writes, so a mid-run icon flip moves the list and the meter together.
    // A captured value (the old shape) left an open overlay window on the other engine until restart - exactly the
    // split-brain this class exists to prevent.
    public static bool Enabled => AppSettings.IsCombatMirrorEnabled;

    /* The meter's quiet window in seconds. Shared with the board's own expiry so the three rules — zero the board, keep the
     * window open, close a hidden one for real — cannot end up arguing over two different numbers. */
    internal static double TimeoutFor(int damageMode) => LiveFights.TimeoutFor(damageMode);

    // "Is a pull going on?" from the capture instead of FightManager's overlay-fight set. No session, no fight: a capture that
    // is not running has nothing to show, which is the same answer as an empty board and does not fall back to anything.
    internal static bool HasLiveFight(int damageMode) => DeriveEngine.Active?.HasLiveFight(TimeoutFor(damageMode)) == true;

    /* The same question for a caller holding no meter window, so no dial is in hand to read: the saved setting is where a
     * window about to open would get it from, which makes this the honest version of the question rather than a default. */
    internal static bool HasLiveFight() => HasLiveFight(ConfigUtil.GetSettingAsInteger("OverlayDamageMode"));
  }
}
