using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security;

namespace EQLogParser
{
  internal static class ConfigUtil
  {
    public delegate void PostConfigCallback();
    public static string PlayerName;
    public static string ServerName;
    public static string LogsDir;
    public static string ConfigDir;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private const string AppData = @"%AppData%\EQLogParser";
    private const string PetmapFile = "petmapping.txt";
    private const string PlayersFile = "players.txt";

    private static string _archiveDir;
    private static string _settingsFile;
    private static string _triggersDbFile;
    private static bool _initDone;
    private static bool _settingsUpdated;

    private static readonly ConcurrentDictionary<string, string> ApplicationSettings = new();
    internal static void SetSetting(string key, bool value) => SetSetting(key, value.ToString());
    internal static void SetSetting(string key, double value) => SetSetting(key, value.ToString(CultureInfo.InvariantCulture));
    internal static void SetSetting(string key, int value) => SetSetting(key, value.ToString(CultureInfo.InvariantCulture));

    internal static string GetArchiveDir()
    {
      Init();
      return _archiveDir;
    }

    internal static bool IfSet(string setting, PostConfigCallback callback = null, bool callByDefault = false)
    {
      var result = false;
      var value = GetSetting(setting);
      if ((value == null && callByDefault) || (value != null && bool.TryParse(value, out var bValue) && bValue))
      {
        result = true;
        callback?.Invoke();
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

    internal static int GetSettingAsInteger(string key)
    {
      if (int.TryParse(GetSetting(key), out var result) == false)
      {
        result = int.MaxValue;
      }
      return result;
    }

    internal static string GetSetting(string key, string def = null)
    {
      Init();
      ApplicationSettings.TryGetValue(key, out var setting);
      return setting ?? def;
    }

    internal static void RemoveSetting(string key)
    {
      Init();
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
      Init();

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
      Init();
      var petMapping = new ConcurrentDictionary<string, string>();
      var fileName = ConfigDir + @"\" + ServerName + @"\" + PetmapFile;
      LoadProperties(petMapping, ReadList(fileName));
      return petMapping;
    }

    internal static List<string> ReadPlayers()
    {
      Init();
      var fileName = ConfigDir + @"\" + ServerName + @"\" + PlayersFile;
      return ReadList(fileName);
    }

    internal static void SavePlayers(List<string> list)
    {
      Init();
      var playerDir = ConfigDir + @"\" + ServerName;
      Directory.CreateDirectory(playerDir);
      SaveList(playerDir + @"\" + PlayersFile, list);
    }

    internal static void SavePetMapping(IEnumerable<KeyValuePair<string, string>> enumeration)
    {
      Init();
      var petDir = ConfigDir + @"\" + ServerName;
      Directory.CreateDirectory(petDir);
      SaveProperties(petDir + @"\" + PetmapFile, enumeration);
    }

    internal static void Save()
    {
      Init();

      if (_settingsUpdated)
      {
        ApplicationSettings.TryRemove("IncludeAEHealing", out _); // not used anymore
        ApplicationSettings.TryRemove("HealingColumns", out _); // not used anymore
        ApplicationSettings.TryRemove("TankingColumns", out _); // not used anymore
        SaveProperties(_settingsFile, ApplicationSettings);
      }
    }

    internal static string GetTriggersDbFile()
    {
      Init();
      return _triggersDbFile;
    }

    private static void Init()
    {
      if (!_initDone)
      {
        _initDone = true;
        _archiveDir = Environment.ExpandEnvironmentVariables(AppData + @"\archive\");
        ConfigDir = Environment.ExpandEnvironmentVariables(AppData + @"\config\");
        LogsDir = Environment.ExpandEnvironmentVariables(AppData + @"\logs\");
        _settingsFile = ConfigDir + @"\settings.txt";
        _triggersDbFile = ConfigDir + @"triggers.db";

        // create config dir if it doesn't exist
        Directory.CreateDirectory(ConfigDir);
        // create logs dir if it doesn't exist
        Directory.CreateDirectory(LogsDir);

        LoadProperties(ApplicationSettings, ReadList(_settingsFile));
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
