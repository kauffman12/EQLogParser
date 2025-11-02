using Microsoft.Win32;
using System;
using System.IO;

namespace EQLogParser
{
  internal static class EqUtil
  {
    // EQ Registry Entry
    private const string EqUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DGC-EverQuest";

    internal static string GetUiFolderFromSpritePath(string spritePath)
    {
      if (TryParseEqSpritePath(spritePath, out var parts))
      {
        try
        {
          var eqUiFolder = Path.GetDirectoryName(parts[0]);
          if (Directory.Exists(eqUiFolder))
          {
            return eqUiFolder;
          }
        }
        catch (Exception)
        {
          // ignore
        }
      }

      return null;
    }

    internal static bool TryParseEqSpritePath(string path, out string[] parts)
    {
      parts = null;

      try
      {
        if (path?.StartsWith("eqsprite://", StringComparison.OrdinalIgnoreCase) == true)
        {
          // Remove the scheme prefix and split on forward slashes
          var pathWithoutScheme = path["eqsprite://".Length..];
          var split = pathWithoutScheme.Split('/');

          // Format: eqsprite://C:/path/to/sheet.tga/col/row
          // We need at least 3 parts: [...path segments..., col, row]
          if (split.Length >= 3)
          {
            parts = split;
            return true;
          }
        }
      }
      catch
      {
        // ignore
      }

      return false;
    }

    // Attempts to find the EverQuest UI "uifiles/default" folder
    internal static bool TryGetEqUiFolder(out string path)
    {
      path = null;

      try
      {
        // try saved setting first
        var eqUiFolderSetting = ConfigUtil.GetSetting("EqUiFolder");
        if (Directory.Exists(eqUiFolderSetting))
        {
          path = eqUiFolderSetting;
          return true;
        }

        // try registry
        if (TryGetEqFolderFromRegistry(out var eqFolderRegistry) && TryGetDefaultUiFolder(eqFolderRegistry, out path))
          return true;

        // try common paths
        if (TryGetEqFolderFromCommonPaths(out var eqFolderCommon) && TryGetDefaultUiFolder(eqFolderCommon, out path))
          return true;
      }
      catch (Exception)
      {
        // can not find folder, may need to prompt
      }

      return false;
    }

    private static bool TryGetDefaultUiFolder(string eqFolder, out string path)
    {
      path = null;
      if (string.IsNullOrEmpty(eqFolder))
        return false;

      var uiFolder = Path.Combine(eqFolder, "uifiles", "default");
      if (Directory.Exists(uiFolder))
      {
        path = uiFolder;
        return true;
      }

      return false;
    }

    private static bool TryGetEqFolderFromCommonPaths(out string path)
    {
      path = null;

      if (NativeMethods.TryGetPublicFolderPath(out var publicFolder))
      {
        var daybreak = Path.Combine(publicFolder, "Daybreak Game Company", "Installed Games", "EverQuest");
        if (Directory.Exists(daybreak))
        {
          path = daybreak;
          return true;
        }

        var sony = Path.Combine(publicFolder, "Sony Online Entertainment", "Installed Games", "EverQuest");
        if (Directory.Exists(sony))
        {
          path = sony;
          return true;
        }
      }

      return false;
    }

    private static bool TryGetEqFolderFromRegistry(out string path)
    {
      // Try HKCU and HLM, 64-bit view first
      return TryGetFromHive(RegistryHive.CurrentUser, RegistryView.Registry64, out path) ||
        TryGetFromHive(RegistryHive.LocalMachine, RegistryView.Registry64, out path);
    }

    private static bool TryGetFromHive(RegistryHive hive, RegistryView view, out string path)
    {
      path = null;

      try
      {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(EqUninstallKey);
        if (key == null)
          return false;

        var uninstallString = key.GetValue("UninstallString") as string;
        if (TryExtractFolderFromCommand(uninstallString, out path))
          return true;

        var displayIcon = key.GetValue("DisplayIcon") as string;
        if (TryExtractFolderFromCommand(displayIcon, out path))
          return true;
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
        var path = cmd.Trim('"')[..lastSlash];
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