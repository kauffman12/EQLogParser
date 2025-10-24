using System;
using System.IO;
using System.Linq;

namespace EQLogParser
{
  internal static class EqUtil
  {
    // Attempts to find the EverQuest UI "uifiles/default" folder from known character log file locations in config.
    public static string GetEqUiFolder()
    {
      try
      {
        var config = TriggerStateManager.Instance.GetConfig().Result;
        if (config?.Characters != null)
        {
          foreach (var c in config.Characters)
          {
            if (!string.IsNullOrEmpty(c.FilePath) && File.Exists(c.FilePath))
            {
              var dir = Path.GetDirectoryName(c.FilePath);
              if (dir == null) continue;
              // walk up looking for "Installed Games\EverQuest" pattern or for "uifiles"
              var cur = dir;
              for (int i = 0; i < 8 && !string.IsNullOrEmpty(cur); i++)
              {
                var candidate = Path.Combine(cur, "uifiles", "default");
                if (Directory.Exists(candidate)) return candidate;
                cur = Path.GetDirectoryName(cur);
              }
            }
          }
        }

        // fallback common path on Windows
        var publicPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        var tryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments).Split(Path.DirectorySeparatorChar)[0] + ":\\", "Users", "Public", "Daybreak Game Company", "Installed Games", "EverQuest", "uifiles", "default");
        if (Directory.Exists(tryPath)) return tryPath;
      }
      catch { }

      return null;
    }
  }
}
