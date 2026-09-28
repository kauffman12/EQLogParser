using log4net;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

namespace EQLogParser
{
  internal static class ConfigUtil
  {
    public static string PlayerName;
    public static string ServerName;
    public static string LogsDir;
    public static string ConfigDir;
    public static event Action<string> EventsLoadingText;
    internal static void InvokeEventsLoadingText(string text) => EventsLoadingText?.Invoke(text);
    internal const string AppData = @"%AppData%\EQLogParser";

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly ConcurrentDictionary<string, string> ApplicationSettings = new();
    private const string PetMappingFile = "petmapping.txt";
    private const string PlayersFile = "players.txt";

    // R10: "set as player / merc / pet / npc" from the derived fight list, per server (MirrorOverrideStore).
    // Same shape as petmapping.txt - name=Kind - and the same folder, because it is the same kind of claim:
    // something the operator knows that the log does not say.
    private const string MirrorOverrideFile = "mirror-overrides.txt";
    private const string IdentityPriorFile = "identity-priors.txt";
    private static string _archiveDir;
    private static string _settingsFile;
    private static string _triggersDbFile;
    private static string _triggersLastDbFile;
    private static bool _settingsUpdated;

    internal static string GetArchiveDir() => _archiveDir;
    internal static string GetTriggersDbFile() => _triggersDbFile;
    internal static string GetTriggersLastDbFile() => _triggersLastDbFile;
    internal static string GetTimelineLayoutsDir() => Environment.ExpandEnvironmentVariables(AppData + @"\features\savedTimelines\");
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

    internal static Dictionary<string, string> ReadPetMapping()
    {
      var petMapping = new Dictionary<string, string>();
      LoadProperties(petMapping, ReadList(ServerFilePath(PetMappingFile, ServerName)));
      return petMapping;
    }

    internal static List<string> ReadPlayers()
    {
      return ReadList(ServerFilePath(PlayersFile, ServerName));
    }

    /*
     * One per-server file under the config dir. Path.Combine, not the `@"\"` concatenation these used to use:
     * ReadList normalized separators on the way in while the writers did not, so on a host where a backslash is
     * an ordinary filename character the two halves did not name the same path - players.txt and petmapping.txt
     * were written somewhere unreadable and read back as empty, which reads like memory loss rather than a bug.
     * Same reasoning already recorded on ReadMirrorOverrides below. Windows output is unchanged.
     */
    private static string ServerFilePath(string file, string serverName) => Path.Combine(ConfigDir ?? "", serverName ?? "", file);

    // Takes the server name for the same reason Save does: the caller owns which server's verdicts it wants,
    // and ConfigUtil.ServerName may already point at the next log by the time this runs.
    internal static Dictionary<string, string> ReadMirrorOverrides(string serverName)
    {
      var overrides = new Dictionary<string, string>();
      if (string.IsNullOrEmpty(serverName) || string.IsNullOrEmpty(ConfigDir)) return overrides;

      // Path.Combine rather than the `@"\"` concatenation its neighbours use: this has to round-trip on a host
      // where a backslash is a filename character, and the mirror's rule tests run there.
      LoadProperties(overrides, ReadList(Path.Combine(ConfigDir, serverName, MirrorOverrideFile)));
      return overrides;
    }

    /*
     * The cross-log sighting ledger (identity-priors.txt): what a PREVIOUS capture's rules concluded about a name,
     * with the rule code that concluded it and how many captures agreed. Per server like every other identity file -
     * "this name is an NPC" is a claim about One's Everfrost, not about eqmc - and read with the same tolerance: a
     * malformed line is skipped, never guessed at, so a hand-edited file cannot invent a verdict.
     */
    internal static Dictionary<string, string> ReadIdentityPriors(string serverName)
    {
      var priors = new Dictionary<string, string>();
      if (string.IsNullOrEmpty(serverName) || string.IsNullOrEmpty(ConfigDir)) return priors;

      LoadProperties(priors, ReadList(Path.Combine(ConfigDir, serverName, IdentityPriorFile)));
      return priors;
    }

    internal static void SaveIdentityPriors(List<KeyValuePair<string, string>> list, string serverName)
    {
      if (string.IsNullOrEmpty(serverName) || string.IsNullOrEmpty(ConfigDir)) return;

      var priorDir = Path.Combine(ConfigDir, serverName);
      Directory.CreateDirectory(priorDir);
      SaveProperties(Path.Combine(priorDir, IdentityPriorFile), list);
    }

    // pass server name to avoid issue where it was changed before save completes
    internal static void SaveMirrorOverrides(List<KeyValuePair<string, string>> list, string serverName)
    {
      if (string.IsNullOrEmpty(serverName) || string.IsNullOrEmpty(ConfigDir)) return;

      var overrideDir = Path.Combine(ConfigDir, serverName);
      Directory.CreateDirectory(overrideDir);
      SaveProperties(Path.Combine(overrideDir, MirrorOverrideFile), list);
    }

    // pass server name to avoid issue where it was changed before save completes
    internal static void SavePlayers(List<string> list, string serverName)
    {
      if (string.IsNullOrEmpty(serverName) || string.IsNullOrEmpty(ConfigDir))
        return;

      var playerDir = Path.Combine(ConfigDir ?? "", serverName);
      Directory.CreateDirectory(playerDir);
      SaveList(Path.Combine(playerDir, PlayersFile), list);
    }

    // pass server name to avoid issue where it was changed before save completes
    internal static void SavePetMapping(List<KeyValuePair<string, string>> list, string serverName)
    {
      if (string.IsNullOrEmpty(serverName) || string.IsNullOrEmpty(ConfigDir))
        return;

      var petDir = Path.Combine(ConfigDir ?? "", serverName);
      Directory.CreateDirectory(petDir);
      SaveProperties(Path.Combine(petDir, PetMappingFile), list);
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
      // Callers use Windows-style separators; normalize so non-Windows hosts can read too.
      fileName = fileName.Replace('\\', Path.DirectorySeparatorChar);
      var result = new List<string>();
      ExceptionUtil.CatchSecurityExceptions(() =>
      {
        if (File.Exists(fileName))
        {
          result.AddRange(File.ReadAllLines(fileName));
        }
      }, Log.Error);
      return result;
    }

    internal static string ReadConfigFile(string fileName)
    {
      var path = ConfigDir + fileName;
      return ExceptionUtil.CatchSecurityExceptions(() => File.Exists(path) ? File.ReadAllText(path) : null, null, Log.Error);
    }

    internal static void SaveList(string fileName, List<string> list)
    {
      ExceptionUtil.SafeWriteAllLines(fileName, list, Log.Error);
    }

    internal static void RemoveFileIfExists(string fileName)
    {
      ExceptionUtil.CatchIoExceptions(() =>
      {
        if (File.Exists(fileName))
        {
          File.Delete(fileName);
        }
      });
    }

    private static void LoadProperties(IDictionary<string, string> properties, List<string> list)
    {
      foreach (var line in list)
      {
        if (string.IsNullOrWhiteSpace(line))
        {
          continue;
        }

        var parts = line.Split('=');
        if (parts is { Length: 2 } && parts[0].Length > 0 && parts[1].Length > 0)
        {
          properties[parts[0]] = parts[1];
        }
      }
    }

    private static void SaveProperties(string fileName, IEnumerable<KeyValuePair<string, string>> enumeration)
    {
      var lines = new List<string>();
      foreach (var keypair in enumeration)
      {
        lines.Add(keypair.Key + "=" + keypair.Value);
      }

      ExceptionUtil.SafeWriteAllLines(fileName, lines, Log.Error);
    }
  }
}
