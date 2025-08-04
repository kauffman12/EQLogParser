using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security;
using System.Windows.Threading;

namespace EQLogParser
{
  internal static class ConfigUtil
  {
    public static string PlayerName;
    public static string ServerName;
    public static string LogsDir;
    public static string ConfigDir;
    internal static event Action<string> EventsLoadingText;
    internal const string AppData = @"%AppData%\EQLogParser";

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly ConcurrentDictionary<string, string> ApplicationSettings = new();
    private const string PetMappingFile = "petmapping.txt";
    private const string PlayersFile = "players.txt";
    private static string _archiveDir;
    private static string _settingsFile;
    private static string _triggersDbFile;
    private static string _triggersLastDbFile;
    private static bool _settingsUpdated;
    private static bool _isDone;

    internal static string GetArchiveDir() => _archiveDir;
    internal static string GetTriggersDbFile() => _triggersDbFile;
    internal static string GetTriggersLastDbFile() => _triggersLastDbFile;
    internal static void SetSetting(string key, bool value) => SetSetting(key, value.ToString());
    internal static void SetSetting(string key, double value) => SetSetting(key, value.ToString(CultureInfo.InvariantCulture));
    internal static void SetSetting(string key, int value) => SetSetting(key, value.ToString(CultureInfo.InvariantCulture));

    internal static void Init()
    {
      _archiveDir = Environment.ExpandEnvironmentVariables(AppData + @"\archive\");
      ConfigDir = Environment.ExpandEnvironmentVariables(AppData + @"\config\");
      LogsDir = Environment.ExpandEnvironmentVariables(AppData + @"\logs\");
      _settingsFile = ConfigDir + @"\settings.txt";
      _triggersDbFile = ConfigDir + @"triggers.db";
      _triggersLastDbFile = ConfigDir + @"triggers-2.2.36.db";

      // create config dir if it doesn't exist
      Directory.CreateDirectory(ConfigDir);
      // create logs dir if it doesn't exist
      Directory.CreateDirectory(LogsDir);
      LoadProperties(ApplicationSettings, ReadList(_settingsFile));
    }

    internal static void UpdateStatus(string text)
    {
      if (!_isDone)
      {
        EventsLoadingText?.Invoke(text);
      }

      if (text == "Done")
      {
        _isDone = true;
      }

      // allow splash screen to update
      Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    internal static bool IfSet(string setting, bool callByDefault = false)
    {
      var result = false;
      var value = GetSetting(setting);
      if ((value == null && callByDefault) || (value != null && bool.TryParse(value, out var bValue) && bValue))
      {
        result = true;
      }
      return result;
    }

    internal static bool IfSetOrElse(string setting, bool def = false)
    {
      var result = def;
      var value = GetSetting(setting);
      if (value != null)
      {
        if (bool.TryParse(value, out result) == false)
        {
          result = def;
        }
      }
      return result;
    }

    internal static double GetSettingAsDouble(string key, double def = 0.0)
    {
      // make sure to read and write doubles as the same culture
      if (double.TryParse(GetSetting(key), NumberStyles.Any, CultureInfo.InvariantCulture, out var result) == false)
      {
        result = def;
      }
      return result;
    }

    internal static int GetSettingAsInteger(string key, int def = 0)
    {
      if (int.TryParse(GetSetting(key), out var result) == false)
      {
        result = def;
      }
      return result;
    }

    internal static string GetSetting(string key, string def = null)
    {
      ApplicationSettings.TryGetValue(key, out var setting);
      return setting ?? def;
    }

    internal static void RemoveSetting(string key)
    {
      if (!string.IsNullOrEmpty(key))
      {
        if (ApplicationSettings.TryRemove(key, out var _))
        {
          _settingsUpdated = true;
        }
      }
    }

    internal static void SetSetting(string key, string value)
    {
      if (value == null)
      {
        if (ApplicationSettings.TryRemove(key, out _))
        {
          _settingsUpdated = true;
        }
      }
      else
      {
        if (ApplicationSettings.TryGetValue(key, out var existing))
        {
          if (existing != value)
          {
            ApplicationSettings[key] = value;
            _settingsUpdated = true;
          }
        }
        else
        {
          ApplicationSettings[key] = value;
          _settingsUpdated = true;
        }
      }
    }

    internal static ConcurrentDictionary<string, string> ReadPetMapping()
    {
      var petMapping = new ConcurrentDictionary<string, string>();
      var fileName = ConfigDir + @"\" + ServerName + @"\" + PetMappingFile;
      LoadProperties(petMapping, ReadList(fileName));
      return petMapping;
    }

    internal static List<string> ReadPlayers()
    {
      var fileName = ConfigDir + @"\" + ServerName + @"\" + PlayersFile;
      return ReadList(fileName);
    }

    internal static void SavePlayers(List<string> list)
    {
      var playerDir = ConfigDir + @"\" + ServerName;
      Directory.CreateDirectory(playerDir);
      SaveList(playerDir + @"\" + PlayersFile, list);
    }

    internal static void SavePetMapping(IEnumerable<KeyValuePair<string, string>> enumeration)
    {
      var petDir = ConfigDir + @"\" + ServerName;
      Directory.CreateDirectory(petDir);
      SaveProperties(petDir + @"\" + PetMappingFile, enumeration);
    }

    internal static void Save()
    {
      if (_settingsUpdated)
      {
        ApplicationSettings.TryRemove("IncludeAEHealing", out _); // not used anymore
        ApplicationSettings.TryRemove("HealingColumns", out _); // not used anymore
        ApplicationSettings.TryRemove("TankingColumns", out _); // not used anymore
        ApplicationSettings.TryRemove("AudioTriggersWatchForGINA", out _); // not used anymore);
        ApplicationSettings.TryRemove("TriggersWatchForGINA", out _); // not used anymore);
        ApplicationSettings.TryRemove("AudioTriggersEnabled", out _); // not used anymore);
        ApplicationSettings.TryRemove("OverlayRankColor1", out _); // not used anymore);
        ApplicationSettings.TryRemove("OverlayRankColor2", out _); // not used anymore);
        ApplicationSettings.TryRemove("OverlayRankColor3", out _); // not used anymore);
        ApplicationSettings.TryRemove("OverlayRankColor4", out _); // not used anymore);
        ApplicationSettings.TryRemove("OverlayRankColor5", out _); // not used anymore);
        ApplicationSettings.TryRemove("OverlayRankColor6", out _); // not used anymore);
        ApplicationSettings.TryRemove("OverlayRankColor7", out _); // not used anymore);
        ApplicationSettings.TryRemove("OverlayRankColor8", out _); // not used anymore);
        ApplicationSettings.TryRemove("OverlayRankColor9", out _); // not used anymore);
        ApplicationSettings.TryRemove("OverlayRankColor10", out _); // not used anymore);
        ApplicationSettings.TryRemove("OverlayShowCritRate", out _); // not used anymore);
        ApplicationSettings.TryRemove("EnableHardwareAcceleration", out _); // not used anymore);
        ApplicationSettings.TryRemove("TriggersVoiceRate", out _); // not used anymore);
        ApplicationSettings.TryRemove("TriggersSelectedVoice", out _); // not used anymore);
        ApplicationSettings.TryRemove("ShowDamageSummaryAtStartup", out _); // not used anymore);
        ApplicationSettings.TryRemove("ShowHealingSummaryAtStartup", out _); // not used anymore);
        ApplicationSettings.TryRemove("ShowTankingSummaryAtStartup", out _); // not used anymore);
        SaveProperties(_settingsFile, ApplicationSettings);
        _settingsUpdated = false;
      }
    }

    internal static List<string> ReadList(string fileName)
    {
      var result = new List<string>();

      try
      {
        if (File.Exists(fileName))
        {
          result.AddRange(File.ReadAllLines(fileName));
        }
      }
      catch (IOException ex)
      {
        Log.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        Log.Error(uax);
      }
      catch (SecurityException se)
      {
        Log.Error(se);
      }

      return result;
    }

    internal static string ReadConfigFile(string fileName)
    {
      string result = null;
      var path = ConfigDir + fileName;

      try
      {
        if (File.Exists(path))
        {
          result = File.ReadAllText(path);
        }
      }
      catch (IOException ex)
      {
        Log.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        Log.Error(uax);
      }
      catch (SecurityException se)
      {
        Log.Error(se);
      }

      return result;
    }

    internal static void SaveList(string fileName, List<string> list)
    {
      try
      {
        File.WriteAllLines(fileName, list);
      }
      catch (IOException ex)
      {
        Log.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        Log.Error(uax);
      }
      catch (SecurityException se)
      {
        Log.Error(se);
      }
    }

    internal static void RemoveFileIfExists(string fileName)
    {
      try
      {
        if (File.Exists(fileName))
        {
          File.Delete(fileName);
        }
      }
      catch (IOException)
      {
        // ignore
      }
      catch (UnauthorizedAccessException)
      {
        // ignore
      }
    }

    private static void LoadProperties(ConcurrentDictionary<string, string> properties, List<string> list)
    {
      list.ForEach(line =>
      {
        var parts = line.Split('=');
        if (parts is { Length: 2 } && parts[0].Length > 0 && parts[1].Length > 0)
        {
          properties[parts[0]] = parts[1];
        }
      });
    }

    private static void SaveProperties(string fileName, IEnumerable<KeyValuePair<string, string>> enumeration)
    {
      try
      {
        var lines = new List<string>();
        foreach (var keypair in enumeration)
        {
          lines.Add(keypair.Key + "=" + keypair.Value);
        }

        File.WriteAllLines(fileName, lines);
      }
      catch (IOException ex)
      {
        Log.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        Log.Error(uax);
      }
      catch (SecurityException se)
      {
        Log.Error(se);
      }
    }
  }
}
