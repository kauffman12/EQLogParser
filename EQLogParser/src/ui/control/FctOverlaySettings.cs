using System;

namespace EQLogParser
{
  /*
   * Overlay presentation settings kept in settings.ini instead of a dialog — there is no FCT configuration UI yet, and
   * the ones that exist today (region scheme, motion style) are switches a player flips while deciding which reads
   * better in their own HUD, so they live on the overlay header rather than in a modal. Deliberately outside the fct/
   * folder: the layout and motion maths stay free of ConfigUtil so they can be unit tested on their own, and this is
   * the only place that knows these key names or their historical spelling.
   */
  internal static class FctOverlaySettings
  {
    public const string LayoutKey = "FctOverlayLayout";
    public const string MotionKey = "FctOverlayMotion";

    /* The pre-motion-style boolean: still read so an upgrade keeps the choreography somebody had already chosen. */
    private const string LegacyFountainKey = "FctOverlayFountain";

    /* Bands is what a first-time user gets; halves stays reachable from the overlay header. */
    public static FctLayoutMode LoadLayout() => Parse(ConfigUtil.GetSetting(LayoutKey, null));

    public static void SaveLayout(FctLayoutMode mode) => ConfigUtil.SetSetting(LayoutKey, Name(mode));

    /* Anything unrecognised falls back to the default rather than to an error dialog over a cosmetic setting. */
    public static FctLayoutMode Parse(string name) =>
      string.Equals(name, "halves", StringComparison.OrdinalIgnoreCase) ? FctLayoutMode.Halves : FctLayoutMode.Bands;

    public static string Name(FctLayoutMode mode) => mode == FctLayoutMode.Halves ? "halves" : "bands";

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

    /* Same rule as the layout names: an unrecognised value falls back to the default, never to an error dialog over a
     * cosmetic setting that a hand-edit in settings.ini can produce by accident. */
    public static FctMotionStyle ParseMotion(string name) =>
      name?.ToLowerInvariant() switch
      {
        "fountain" => FctMotionStyle.Fountain,
        "pulse" => FctMotionStyle.Pulse,
        "spray" => FctMotionStyle.Spray,
        _ => FctMotionStyle.Hold,
      };

    public static string Name(FctMotionStyle style) =>
      style switch
      {
        FctMotionStyle.Fountain => "fountain",
        FctMotionStyle.Pulse => "pulse",
        FctMotionStyle.Spray => "spray",
        _ => "hold",
      };
  }
}
