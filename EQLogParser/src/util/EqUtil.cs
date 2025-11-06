using Microsoft.Extensions.Caching.Memory;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal static class EqUtil
  {
    // EQ Registry Entry
    private const string EqUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DGC-EverQuest";

    // Validate a sprite file path. Used during importing to a new system which may have EQ installed someplace else
    internal static string ValidateSpritePath(TriggerConfig config, string spritePath)
    {
      if (TryParseEqSpritePath(spritePath, out var parts))
      {
        // sprite path missing
        if (File.Exists(parts[0]))
        {
          return spritePath;
        }
        else
        {
          // search for similar icon file
          if (Path.GetFileName(parts[0]) is { } iconFile && Directory.GetParent(parts[0]) is { } custom &&
            Directory.GetParent(custom.FullName) is { } uifiles && "uifiles".Equals(uifiles.Name, StringComparison.OrdinalIgnoreCase))
          {
            var imagePath = $"{uifiles.Name}\\{custom.Name}\\{iconFile}";
            foreach (var eqFolder in GetEqFolders(config))
            {
              var newPath = $"{eqFolder}\\{imagePath}";
              if (File.Exists(newPath))
              {
                // found similar so use this one
                return $"eqsprite://{newPath}/{parts[1]}/{parts[2]}";
              }
            }
          }
        }
      }

      return null;
    }

    // Parses the folder containing the sprites (uifiles/default)
    internal static string GetUiFolderFromSpritePath(string spritePath)
    {
      if (TryParseEqSpritePath(spritePath, out var parts))
      {
        try
        {
          var eqUiFolder = Path.GetDirectoryName(parts[0]);
          if (Directory.Exists(eqUiFolder))
            return eqUiFolder;
        }
        catch (Exception)
        {
          // ignore
        }
      }

      return null;
    }

    // Parses the sprite path itself for the sprite file and params
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

    // Attempts to find an EverQuest UI "uifiles/default" folder
    internal static async Task<string> GetEqUiFolderAsync(string id)
    {
      try
      {
        // try saved setting first
        var eqUiFolderSetting = ConfigUtil.GetSetting("EqUiFolder");
        if (Directory.Exists(eqUiFolderSetting))
          return eqUiFolderSetting;

        // try all known folders
        foreach (var eqFolder in await GetEqFoldersAsync(id))
        {
          if (TryGetDefaultUiFolder(eqFolder, out var path))
            return path;
        }
      }
      catch (Exception)
      {
        // can not find folder, may need to prompt
      }

      return null;
    }

    // Returns unique list of all known EQ folders
    private static List<string> GetEqFolders(TriggerConfig config)
    {
      return App.AppCache.GetOrCreate($"eqfolder-cache-all", entry =>
      {
        // cache results for a few minutes. mainly for quickshare/import
        entry.SetSlidingExpiration(TimeSpan.FromMinutes(5));

        // add paths associated with characters
        var eqFolderList = new List<string>();
        PopulateEqFoldersFromAllCharacters(eqFolderList, config);

        // add standard paths from common places and registry settings
        PopulateStandardEqFolderPaths(eqFolderList);
        var unique = eqFolderList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        entry.SetSize(unique.Select(path => path.Length).Sum());
        return unique;
      });
    }

    // Returns unique list of EQ folders for a specific character
    private static async Task<List<string>> GetEqFoldersAsync(string id)
    {
      if (string.IsNullOrEmpty(id))
        return await Task.FromResult<List<string>>([]);

      return await App.AppCache.GetOrCreateAsync($"eqfolder-cache-all", async entry =>
      {
        // cache results for a few minutes. mainly for quickshare/import
        entry.SetSlidingExpiration(TimeSpan.FromMinutes(5));

        // add paths associated with characters
        var eqFolderList = new List<string>();
        await PopulateEqFoldersFromCharacterAsync(eqFolderList, id);

        // add standard paths from common places and registry settings
        PopulateStandardEqFolderPaths(eqFolderList);
        var unique = eqFolderList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        entry.SetSize(unique.Select(path => path.Length).Sum());
        return unique;
      });
    }

    // Finds EQ Folders associated with all characters configured in Trigger Manager
    private static void PopulateEqFoldersFromAllCharacters(List<string> eqFolderList, TriggerConfig config)
    {
      try
      {
        // basic mode uses current file
        if (GetEqFolderFromPath(MainWindow.CurrentLogFile) is { } currentFolder)
        {
          eqFolderList?.Add(currentFolder);
        }

        // all advanced mode characters
        if (config != null)
        {
          foreach (var character in config.Characters)
          {
            if (GetEqFolderFromPath(character.FilePath) is { } characterFolder)
            {
              eqFolderList?.Add(characterFolder);
            }
          }
        }
      }
      catch (Exception)
      {
        // ignore
      }
    }

    // Finds EQ Folders associated with a specific character in Trigger Manager
    private static async Task PopulateEqFoldersFromCharacterAsync(List<string> eqFolderList, string id)
    {
      try
      {
        // basic mode uses current file
        if (id == TriggerStateManager.DefaultUser && GetEqFolderFromPath(MainWindow.CurrentLogFile) is { } currentFolder)
        {
          eqFolderList?.Add(currentFolder);
          return;
        }

        // all advanced mode characters
        var config = await TriggerStateManager.Instance.GetConfig();
        foreach (var character in config?.Characters ?? [])
        {
          if (character.Id == id && GetEqFolderFromPath(character.FilePath) is { } characterFolder)
          {
            eqFolderList?.Add(characterFolder);
          }
        }
      }
      catch (Exception)
      {
        // ignore
      }
    }

    // Returns cached list of system defined paths for EQ folders
    private static void PopulateStandardEqFolderPaths(List<string> eqFolderList)
    {
      try
      {
        // add each common path found
        foreach (var common in GetEqFolderFromCommonPaths())
          eqFolderList?.Add(common);

        // add path from registry
        if (TryGetEqFolderFromRegistry(out var regPath))
          eqFolderList?.Add(regPath);
      }
      catch (Exception)
      {
        // ignore
      }
    }

    // Returns list of EQ folders found where the game is typically installed
    private static List<string> GetEqFolderFromCommonPaths()
    {
      var paths = new List<string>(2);
      if (NativeMethods.TryGetPublicFolderPath(out var publicFolder))
      {
        var daybreak = Path.Combine(publicFolder, "Daybreak Game Company", "Installed Games", "EverQuest");
        if (Directory.Exists(daybreak))
        {
          paths.Add(daybreak);
        }

        var sony = Path.Combine(publicFolder, "Sony Online Entertainment", "Installed Games", "EverQuest");
        if (Directory.Exists(sony))
        {
          paths.Add(sony);
        }
      }

      return paths;
    }

    // Try to query the EQ folder based on registry settings
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

    private static string GetEqFolderFromPath(string path)
    {
      if (!string.IsNullOrEmpty(path))
      {
        try
        {
          // check that it looks like a valid EQ folder
          if (Directory.GetParent(path) is { } parent && "Logs".Equals(parent.Name, StringComparison.OrdinalIgnoreCase) && parent.Parent is { } root &&
            TryGetDefaultUiFolder(root.FullName, out _))
          {
            return root.FullName;
          }
        }
        catch (Exception)
        {
          // ignore
        }
      }

      return null;
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
  }
}