using System.Globalization;
using System.Windows.Threading;

namespace EQLogParser
{
  class OverlayUtil
  {
    private static bool IsDamageOverlayEnabled = false;
    private static OverlayWindow Overlay = null;

    internal static void CloseOverlay() => Overlay?.Close();

    internal static bool LoadSettings()
    {
      IsDamageOverlayEnabled = ConfigUtil.IfSet("IsDamageOverlayEnabled");
      return IsDamageOverlayEnabled;
    }

    internal static void OpenIfEnabled(Dispatcher dispatcher)
    {
      if (IsDamageOverlayEnabled)
      {
        OpenOverlay(dispatcher);
      }
    }

    internal static void OpenOverlay(Dispatcher dispatcher, bool configure = false, bool saveFirst = false)
    {
      if (saveFirst)
      {
        ConfigUtil.Save();
      }

      dispatcher.InvokeAsync(() =>
      {
        Overlay?.Close();
        Overlay = new OverlayWindow(configure);
        Overlay.Show();
      });
    }

    internal static void ResetOverlay(Dispatcher dispatcher)
    {
      Overlay?.Close();
      DataManager.Instance.ResetOverlayFights();

      if (IsDamageOverlayEnabled)
      {
        OpenOverlay(dispatcher);
      }
    }

    internal static bool ToggleOverlay(Dispatcher dispatcher)
    {
      IsDamageOverlayEnabled = !IsDamageOverlayEnabled;
      ConfigUtil.SetSetting("IsDamageOverlayEnabled", IsDamageOverlayEnabled.ToString(CultureInfo.CurrentCulture));
 
      if (IsDamageOverlayEnabled)
      {
        OpenOverlay(dispatcher, true, false);
      }
      else
      {
        CloseOverlay();
      }

      return IsDamageOverlayEnabled;
    }
  }
}
