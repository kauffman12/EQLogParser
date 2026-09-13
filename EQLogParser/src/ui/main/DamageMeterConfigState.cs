// The staged configuration of the damage meter, carried between the settings window and the preview stage.
// Load() reads the ini with exactly the guards the overlay constructor uses, so a fresh copy is the truth the user
// started from; the settings window mutates this copy, every change previews it onto the stage, and only Save writes
// it back (CommitMeterState). Geometry deliberately stays OUT of this object: dragging and resizing the stage is the
// geometry editor, and Save captures whatever rectangle the stage ended on.

namespace EQLogParser
{
  internal sealed class DamageMeterConfigState
  {
    internal int MaxRows = 5;                    // 1..10 ranked rows
    internal int FontSize = 12;                  // one of 10/12/14/16
    internal bool MiniBars;                      // slim ranking bars
    internal bool ShowDamagePercent;             // each row's share of the total
    internal bool HideOtherPlayers;              // mask everyone but me
    internal bool StreamerMode;                  // own window style for OBS
    internal int CritRateDisplay;                // 0 off, 1 DoT, 2 nuke, 3 both
    internal int DamageResetMode;                // 0 = on kill, otherwise seconds before zeroing
    internal string SelectedClass = Resource.ANY_CLASS;
    internal string ProgressColor = "#FF1D397E";
    internal string HighlightColor = "Gold";

    // Same keys, same fallbacks the overlay constructor honours, so opening setup always starts from what is on
    // screen right now.
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

      s.MiniBars = ConfigUtil.IfSet("OverlayMiniBars");
      s.ShowDamagePercent = ConfigUtil.IfSet("OverlayShowDamagePercent");
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
