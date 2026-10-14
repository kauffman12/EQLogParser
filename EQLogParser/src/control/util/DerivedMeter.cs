using EQLogParser;

namespace EQLogParser
{
  /*
   * The seam between the damage meter's two questions: "how long is this board quiet?" and "is a pull going on?".
   *
   * It exists because both used to be answered twice - once per engine, with a dial deciding which pair applied to
   * the window. The dial, the other engine and that engine's name are gone - this class was `MirrorMeter`, named after
   * the experiment that built it, and never installed under that name. Nothing mirrors anything now: the derived engine
   * IS the engine. What remains is the single place the meter asks the capture, so the list and the board can never
   * again disagree about what is live.
   *
   * Nothing in this class decides policy. The quiet window is the meter's dial (`OverlayDamageMode`, 0 = "on kill") and the
   * live question is answered over derived rows in LiveFights; what lives here is only the seam between them.
   */
  internal static class DerivedMeter
  {
    /*
     * "Is a pull still going?" in seconds — the OPENING dial reading, and what `HasLiveFight` asks with. For mode 0 that is the
     * engagement gap (`GapS`, legacy's `FightManager.FightTimeout`), so a 12 s hole inside a fight does not read as over.
     */
    internal static double TimeoutFor(int damageMode) => LiveFights.TimeoutFor(damageMode);

    /*
     * "How long may this board stay up with nothing new?" in seconds — the EXPIRY dial reading, and what the overlay's window rule
     * asks with. Time-based modes are that many seconds. Mode 0 ("on kill") is `KillQuietCloseS` after a death we SAW and `GapS`
     * otherwise, because the only other thing that ends an unnoticed pull is the same 30 s of nothing that starts a new fight-list row
     * for the next swing (`FightProjection.EngagementGapS`) — legacy's `FightTimeout`, and the reason damage recorded under this dial
     * did not depend on how long the board stayed visible.
     *
     * `killObserved` means a row ended `Slain` (`DeriveEngine.LastKillT`) AND nothing has landed since it — a kill followed by more
     * damage is a fight still going and must not inherit the dead mob's shorter timer. Liveness (`TimeoutFor`) is deliberately NOT this
     * number: a window may stay open while its board blanks, never the other way round.
     */
    internal static double ExpiryFor(int damageMode, bool killObserved) => LiveFights.ExpiryFor(damageMode, killObserved);

    // "Is a pull going on?" from the capture instead of FightManager's overlay-fight set. No session, no fight: a capture that
    // is not running has nothing to show, which is the same answer as an empty board and does not fall back to anything.
    internal static bool HasLiveFight(int damageMode) => DeriveEngine.Active?.HasLiveFight(TimeoutFor(damageMode)) == true;

    /* The same question for a caller holding no meter window, so no dial is in hand to read: the saved setting is where a
     * window about to open would get it from, which makes this the honest version of the question rather than a default. */
    internal static bool HasLiveFight() => HasLiveFight(ConfigUtil.GetSettingAsInteger("OverlayDamageMode"));
  }
}
