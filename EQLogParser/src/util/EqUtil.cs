using Microsoft.Win32;
using System;
using System.IO;

namespace EQLogParser
{
  internal static class EqUtil
  {
    // the key you mentioned
    private const string EqUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DGC-EverQuest";

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
              for (var i = 0; i < 8 && !string.IsNullOrEmpty(cur); i++)
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

    internal static string GetEverQuestFolderFromRegistry()
    {
      // 1. Try HKCU, 64-bit view first
      if (TryGetFromHive(RegistryHive.CurrentUser, RegistryView.Registry64, out var hkcuPath))
      {
        return hkcuPath;
      }

      // 2. Try HKLM, 64-bit view
      if (TryGetFromHive(RegistryHive.LocalMachine, RegistryView.Registry64, out var hklmPath))
      {
        return hklmPath;
      }

      return null;
    }

    private static bool TryGetFromHive(RegistryHive hive, RegistryView view, out string result)
    {
      result = null;

      try
      {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(EqUninstallKey);
        if (key == null) return false;

        var uninstallString = key.GetValue("UninstallString") as string;
        if (TryExtractFolderFromCommand(uninstallString, out result))
        {
          return true;
        }

        var displayIcon = key.GetValue("DisplayIcon") as string;
        if (TryExtractFolderFromCommand(displayIcon, out result))
        {
          return true;
        }
      }
      catch
      {
        // ignore
      }

      return false;
    }

    private static bool TryExtractFolderFromCommand(string cmd, out string result)
    {
      result = null;

      if (!string.IsNullOrWhiteSpace(cmd) && cmd.LastIndexOf('\\') is > 0 and var lastSlash)
      {
        var path = cmd[..lastSlash];
        if (Directory.Exists(path))
        {
          result = path;
          return true;
        }
      }

      return false;
    }
  }
}