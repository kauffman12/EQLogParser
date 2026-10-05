using EQLogParser;

namespace EQLogParser
{
  /*
   * The seam between the damage meter's two questions: "how long is this board quiet?" and "is a pull going on?".
   *
   * It exists because both used to be answered twice - once per engine, with a dial deciding which pair applied to
   * the window. The dial, the other engine and that engine's name are gone (this class was `MirrorMeter`, after the
   * experiment that shipped it; nothing mirrors anything now, the derived engine IS the engine), and what remains is
   * the single place the meter asks the capture, so the list and the board can never again disagree about what is live.
   *
   * Nothing in this class decides policy. The quiet window is the meter's dial (`OverlayDamageMode`, 0 = "on kill") and the
   * live question is answered over derived rows in LiveFights; what lives here is only the seam between them.
   */
  internal static class DerivedMeter
  {
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
