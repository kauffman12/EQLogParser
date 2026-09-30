using EQLogParser.Mirror;

namespace EQLogParser
{
  /*
   * The one place that knows WHICH engine the damage meter is reading.
   *
   * `OverlayDamageFromMirror` used to be read in two components, and the second of them (the auto-open path) still asked
   * FightManager instead: a meter could therefore paint derived numbers while its visibility, its reset and its
   * open-yourself-on-a-pull rule all came from legacy state — which is exactly the arrangement that makes a half-ported
   * surface look finished. Reading the key once here means the setting has one address, and the day FightManager goes away
   * the deletion is a grep rather than an excavation.
   *
   * Nothing in this class decides policy. The quiet window is the meter's dial (`OverlayDamageMode`, 0 = "on kill") and the
   * live question is answered over derived rows in LiveFights; what lives here is only the seam between them.
   */
  internal static class MirrorMeter
  {
    // Read once, like the rest of the settings that need a restart: legacy-replacement-map documents this key as "add to
    // settings.txt and restart", so a per-window read bought nothing but a second place to change the name.
    public static bool Enabled { get; } = ConfigUtil.IfSet("OverlayDamageFromMirror");

    /* The meter's quiet window in seconds. Shared with the board's own expiry so the three rules — zero the board, keep the
     * window open, close a hidden one for real — cannot end up arguing over two different numbers. */
    internal static double TimeoutFor(int damageMode) => LiveFights.TimeoutFor(damageMode);

    // "Is a pull going on?" from the capture instead of FightManager's overlay-fight set. No session, no fight: a mirror that
    // is not capturing has nothing to show, which is the same answer as an empty board and does not fall back to anything.
    internal static bool HasLiveFight(int damageMode) => MirrorSession.Active?.HasLiveFight(TimeoutFor(damageMode)) == true;

    /* The same question for a caller holding no meter window, so no dial is in hand to read: the saved setting is where a
     * window about to open would get it from, which makes this the honest version of the question rather than a default. */
    internal static bool HasLiveFight() => HasLiveFight(ConfigUtil.GetSettingAsInteger("OverlayDamageMode"));
  }
}
