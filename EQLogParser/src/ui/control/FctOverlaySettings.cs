using System;

namespace EQLogParser
{
  /*
   * Overlay presentation settings kept in settings.ini instead of a dialog — there is no FCT configuration UI yet, and
   * the one that exists today (motion style) is a switch a player flips while deciding which reads better in their own HUD,
   * so it lives on the overlay header rather than in a modal. Deliberately outside the fct/
   * folder: the layout and motion maths stay free of ConfigUtil so they can be unit tested on their own, and this is
   * the only place that knows these key names or their historical spelling.
   */
  internal static class FctOverlaySettings
  {
    public const string MotionKey = "FctOverlayMotion";

    /*
     * The region scheme and how halves is oriented (see FctStage). halves came back as the shipped default with its failure mode fixed,
     * so these four keys are read on every start; junk or missing values land on the shipped choice through the parse helpers below.
     */
    public const string LayoutKey = "FctOverlayLayout";
    public const string IncomingSideKey = "FctOverlayIncomingSide";
    public const string IncomingDirectionKey = "FctOverlayIncomingDirection";
    public const string OutgoingDirectionKey = "FctOverlayOutgoingDirection";

    /* The two dials on the configure row, stored as multipliers rather than percentages: the file is a place where a human may look, and the value
       that means something to the code is the one worth writing. Size runs 0.5 - 1.5; speed runs 0.76 - 2.28 around a shipped 1.14, which are the ends of
       a dial measured in percent of time taken rather than in rate (see FctScale). FctScale clamps on the way in, so a hand-edited "big" or 12 lands on
       that dial's default instead of drawing nothing or running at four times tempo. */
    public const string TextScaleKey = "FctOverlayTextScale";
    public const string SpeedKey = "FctOverlaySpeed";

    public static double LoadTextScale() => FctScale.ClampSize(ConfigUtil.GetSettingAsDouble(TextScaleKey, FctScale.SizeDefault));

    public static void SaveTextScale(double scale) => ConfigUtil.SetSetting(TextScaleKey, FctScale.ClampSize(scale));

    /*
     * Retired: FctOverlayTimeScale, which held a duration multiplier while that dial was called "time". It is still read once when the speed key is
     * absent and inverted on the way through, because 0.8 in that key meant "20 % shorter", which is 25 % faster - reusing the number as if it were
     * a speed would quietly set most people's overlay to run slow, in the opposite direction from the one they asked for. It is never written again,
     * so an old key simply stops being mentioned after one session.
     */
    private const string LegacyTimeScaleKey = "FctOverlayTimeScale";

    public static double LoadSpeed()
    {
      var stored = ConfigUtil.GetSettingAsDouble(SpeedKey, double.NaN);
      if (double.IsFinite(stored))
      {
        return FctScale.ClampSpeed(stored);
      }

      /* Absent on both counts: the shipped tempo. A legacy 1.0 is a stored preference for the pace that used to ship, and stays slower than this. */
      return FctScale.SpeedFromTime(ConfigUtil.GetSettingAsDouble(LegacyTimeScaleKey, FctScale.TimeDefault));
    }

    public static void SaveSpeed(double speed) => ConfigUtil.SetSetting(SpeedKey, FctScale.ClampSpeed(speed));

    /*
     * Whether anybody has ever pressed Save on the configure row. The overlay's defaults are defensible but they are opinions, and a first enable that
     * shows nothing but numbers being what the build decided keeps a player from learning that size, speed and motion are theirs to set - so the menu
     * opens configure mode instead (see MainWindow.SetFctOverlayVisible). Written only by Save: Cancel, Esc and closing all leave it unset, which means
     * the offer comes back rather than nagging into a setting nobody agreed to.
     */
    public const string ConfiguredKey = "FctOverlayConfigured";

    public static bool IsConfigured() => ConfigUtil.IfSet(ConfiguredKey);

    public static void SaveConfigured() => ConfigUtil.SetSetting(ConfiguredKey, true);

    /*
     * Halves came back (FctStage), and with it this key: the pre-release build that had a layout checkbox wrote enum names,
     * so a settings.ini that carries "halves" or "bands" means exactly what it says and needs no migration. The side and
     * direction keys are new; LoadLayout parses all four through the pure helpers below, which is where junk gets the shipped
     * default instead of reaching the geometry as garbage.
     */

    public static FctLayoutChoice LoadLayout() => new(
      ParseMode(ConfigUtil.GetSetting(LayoutKey, null)),
      ParseSide(ConfigUtil.GetSetting(IncomingSideKey, null)),
      ParseUp(ConfigUtil.GetSetting(IncomingDirectionKey, null)),
      ParseUp(ConfigUtil.GetSetting(OutgoingDirectionKey, null)));

    public static void SaveLayout(FctLayoutChoice layout)
    {
      ConfigUtil.SetSetting(LayoutKey, layout.Mode == FctLayoutMode.Halves ? "halves" : "bands");
      ConfigUtil.SetSetting(IncomingSideKey, layout.IncomingSide == FctRegionSide.Right ? "right" : "left");
      ConfigUtil.SetSetting(IncomingDirectionKey, layout.IncomingUp ? "up" : "down");
      ConfigUtil.SetSetting(OutgoingDirectionKey, layout.OutgoingUp ? "up" : "down");
    }

    /*
     * The parse helpers are pure on purpose: settings.ini speaks words and these are where junk gets a default, so a stray or
     * hand-edited value can never reach the geometry as garbage. All of them fall to the shipped choice's own value.
     */
    internal static FctLayoutMode ParseMode(string raw) =>
      string.Equals(raw, "halves", StringComparison.OrdinalIgnoreCase) ? FctLayoutMode.Halves : FctLayoutMode.Bands;

    internal static FctRegionSide ParseSide(string raw) =>
      string.Equals(raw, "right", StringComparison.OrdinalIgnoreCase) ? FctRegionSide.Right : FctRegionSide.Left;

    internal static bool ParseUp(string raw) =>
      string.Equals(raw, "up", StringComparison.OrdinalIgnoreCase);

    /* The pre-motion-style boolean: still read so an upgrade keeps the choreography somebody had already chosen. */
    private const string LegacyFountainKey = "FctOverlayFountain";

    /*
     * Reading order matters: the current key wins, and an absent one inherits the old "fountain" checkbox rather than
     * resetting a player to hold on the first launch after an upgrade. Writing always uses the new key, so the legacy
     * entry fades out on its own and nothing has to migrate a file.
     */
    public static FctMotionStyle LoadMotion() =>
      ConfigUtil.GetSetting(MotionKey, null) is { Length: > 0 } name ? ParseMotion(name)
        : ConfigUtil.IfSet(LegacyFountainKey) ? FctMotionStyle.Fountain
        : FctMotionStyle.Hold;

    public static void SaveMotion(FctMotionStyle style) => ConfigUtil.SetSetting(MotionKey, Name(style));

    /* An unrecognised value falls back to the default, never to an error dialog over a cosmetic setting that a hand-edit
     * in settings.ini can produce by accident. */
    public static FctMotionStyle ParseMotion(string name) =>
      name?.ToLowerInvariant() switch
      {
        "fountain" => FctMotionStyle.Fountain,
        "pulse" => FctMotionStyle.Pulse,
        "spray" => FctMotionStyle.Spray,
        "parabola" => FctMotionStyle.Parabola,
        _ => FctMotionStyle.Hold,
      };

    public static string Name(FctMotionStyle style) =>
      style switch
      {
        FctMotionStyle.Fountain => "fountain",
        FctMotionStyle.Pulse => "pulse",
        FctMotionStyle.Spray => "spray",
        FctMotionStyle.Parabola => "parabola",
        _ => "hold",
      };

    /*
     * Whether the player ever chose a motion at all, as opposed to ParseMotion's hold default. A first-time halves user
     * gets the genre's shape (the parabola) instead of that default — but somebody who picked hold on purpose keeps it.
     */
    public static bool HasStoredMotion()
      => ConfigUtil.GetSetting(MotionKey, null) is { Length: > 0 } || ConfigUtil.IfSet(LegacyFountainKey);
  }
}
