// Shared settings reader for the meter and its staged preview; only Save persists changes.
// Geometry is edited on the stage and captured at Save. MeterColumns owns the column-key fold.
// docs/DesignNotes.md → "Damage meter column switches: hiding one reserves no width".

namespace EQLogParser
{
  internal sealed class DamageMeterConfigState
  {
    internal int MaxRows = 5;                    // 1..10 ranked rows
    internal int FontSize = 12;                  // one of 10/12/14/16
    internal bool MiniBars;                      // slim ranking bars
    internal MeterColumn Columns = MeterColumns.Default;
    internal bool HideOtherPlayers;              // mask everyone but me
    internal bool StreamerMode;                  // own window style for OBS
    internal int CritRateDisplay;                // 0 off, 1 DoT, 2 nuke, 3 both
    internal int DamageResetMode;                // 0 = on kill, otherwise seconds before zeroing

    // Which panels exist at all — and therefore which kinds of activity may open the window or keep it up. Distinct from
    // OverlayShowingDps, which only says which of the two a Both meter is currently looking at.
    internal MeterPanels Panels = MeterPanels.Both;
    internal string SelectedClass = Resource.ANY_CLASS;
    internal string ProgressColor = "#FF1D397E";
    internal string HighlightColor = "Gold";

    /*
     * The panel choice, stored as the flags value itself (Damage 1, Tanking 2, Both 3) so `None` is unreadable: a missing key, junk, or a
     * hand-written 0 all land on Both, which is the one thing that keeps a meter from being configured into having no tabs. Its own method
     * because the derive path asks this every classified pass (the auto-open announcement) and must not build a whole state object to ask
     * one question; this remains the only place the key is judged.
     */
    internal static MeterPanels LoadPanels()
    {
      var panels = ConfigUtil.GetSettingAsInteger("OverlayMeterPanels");
      return panels is (int) MeterPanels.Damage or (int) MeterPanels.Tanking or (int) MeterPanels.Both
        ? (MeterPanels) panels
        : MeterPanels.Both;
    }

    // Every guard lives here and nowhere else: out-of-range or unreadable values fall back to what a meter without that key would
    // draw, which is what both the setup window and a freshly opened overlay show.
    internal static DamageMeterConfigState Load()
    {
      var s = new DamageMeterConfigState();

      var font = ConfigUtil.GetSetting("OverlayFontSize");
      if (font != null && int.TryParse(font, out var f) && (f == 10 || f == 12 || f == 14 || f == 16))
      {
        s.FontSize = f;
      }

      var rows = ConfigUtil.GetSetting("OverlayMaxRows");
      if (rows != null && int.TryParse(rows, out var r) && r >= 1 && r <= 10)
      {
        s.MaxRows = r;
      }

      var mode = ConfigUtil.GetSettingAsInteger("OverlayDamageMode");
      if (mode >= 0 && mode <= 100)
      {
        s.DamageResetMode = mode;
      }

      var crit = ConfigUtil.GetSettingAsInteger("OverlayEnableCritRate");
      s.CritRateDisplay = crit is >= 0 and <= 3 ? crit : 0;

      s.Panels = LoadPanels();

      s.MiniBars = ConfigUtil.IfSet("OverlayMiniBars");
      s.Columns = MeterColumns.Load();
      s.HideOtherPlayers = ConfigUtil.IfSet("OverlayHideOtherPlayers");
      s.StreamerMode = ConfigUtil.IfSet("OverlayStreamerMode");

      var klass = ConfigUtil.GetSetting("OverlaySelectedClass");
      if (klass != null)
      {
        s.SelectedClass = klass;
      }

      var pc = ConfigUtil.GetSetting("OverlayRankColor");
      if (pc != null && System.Windows.Media.ColorConverter.ConvertFromString(pc) != null)
      {
        s.ProgressColor = pc;
      }

      var hc = ConfigUtil.GetSetting("OverlayHighlightColor");
      if (hc != null && System.Windows.Media.ColorConverter.ConvertFromString(hc) != null)
      {
        s.HighlightColor = hc;
      }
      else
      {
        s.HighlightColor = s.ProgressColor;
      }

      return s;
    }
  }
}
