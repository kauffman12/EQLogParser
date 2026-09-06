using System;

namespace EQLogParser
{
  /*
   * Overlay presentation settings kept in settings.ini instead of a dialog — there is no FCT configuration UI
   * yet, and the two that exist today (layout) are A/B switches a player flips while trying to decide which reads
   * better in their own HUD. Deliberately outside the fct/ folder: the layout maths stays free of ConfigUtil so it
   * can be unit tested on its own, and this is the only place that knows these key names.
   */
  internal static class FctOverlaySettings
  {
    public const string LayoutKey = "FctOverlayLayout";

    /* Bands is what a first-time user gets; halves stays reachable from the overlay header. */
    public static FctLayoutMode LoadLayout() => Parse(ConfigUtil.GetSetting(LayoutKey, null));

    public static void SaveLayout(FctLayoutMode mode) => ConfigUtil.SetSetting(LayoutKey, Name(mode));

    /* Anything unrecognised falls back to the default rather than to an error dialog over a cosmetic setting. */
    public static FctLayoutMode Parse(string name) =>
      string.Equals(name, "halves", StringComparison.OrdinalIgnoreCase) ? FctLayoutMode.Halves : FctLayoutMode.Bands;

    public static string Name(FctLayoutMode mode) => mode == FctLayoutMode.Halves ? "halves" : "bands";
  }
}
