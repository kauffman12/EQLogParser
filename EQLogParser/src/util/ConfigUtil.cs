﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;

namespace EQLogParser
{
  class ConfigUtil
  {
    public static string PlayerName;
    public static string ServerName;

    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const string APP_DATA = @"%AppData%\EQLogParser";
    private const string PETMAP_FILE = "petmapping.txt";
    private const string PETMAP_PATH = @"\{0}";

    private static string ArchiveDir;
    private static string ConfigDir;
    private static string PetMapDir;
    private static string SettingsFile;
    private static bool initDone = false;
    private static bool SettingsUpdated = false;

    private static readonly ConcurrentDictionary<string, string> ApplicationSettings = new ConcurrentDictionary<string, string>();

    internal static string GetArchiveDir()
    {
      Init();
      return ArchiveDir;
    }

    internal static string GetApplicationSetting(string key)
    {
      Init();
      ApplicationSettings.TryGetValue(key, out string setting);
      return setting;
    }

    internal static void SetApplicationSetting(string key, string value)
    {
      Init();

      if (value == null)
      {
        if (ApplicationSettings.TryRemove(key, out _))
        {
          SettingsUpdated = true;
        }
      }
      else
      {
        if (ApplicationSettings.TryGetValue(key, out string existing))
        {
          if (existing != value)
          {
            ApplicationSettings[key] = value;
            SettingsUpdated = true;
          }
        }
        else
        {
          ApplicationSettings[key] = value;
          SettingsUpdated = true;
        }
      }
    }

    internal static ConcurrentDictionary<string, string> ReadPetMapping()
    {
      Init();

      var petMapping = new ConcurrentDictionary<string, string>();
      var fileName = string.Format(CultureInfo.CurrentCulture, PetMapDir, ServerName, PETMAP_FILE) + @"\" + PETMAP_FILE;
      LoadProperties(petMapping, ReadList(fileName));
      return petMapping;
    }

    internal static void SavePetMapping(IEnumerable<KeyValuePair<string, string>> enumeration)
    {
      Init();

      var petDir = string.Format(CultureInfo.CurrentCulture, PetMapDir, ServerName);
      Directory.CreateDirectory(petDir);
      SaveProperties(petDir + @"\" + PETMAP_FILE, enumeration);
    }

    internal static void Save()
    {
      Init();

      if (SettingsUpdated)
      {
        ApplicationSettings.TryRemove("IncludeAEHealing", out _); // not used anymore
        SaveProperties(SettingsFile, ApplicationSettings);
      }
    }

    private static void Init()
    {
      if (!initDone)
      {
        initDone = true;
        ArchiveDir = Environment.ExpandEnvironmentVariables(APP_DATA + @"\archive\");
        ConfigDir = Environment.ExpandEnvironmentVariables(APP_DATA + @"\config\");
        PetMapDir = ConfigDir + PETMAP_PATH;
        SettingsFile = ConfigDir + @"\settings.txt";

        // remove this in the future
        RemoveFileIfExists(ConfigDir + PETMAP_FILE);

        // create config dir if it doesn't exist
        Directory.CreateDirectory(ConfigDir);

        LoadProperties(ApplicationSettings, ReadList(SettingsFile));
      }
    }

    internal static List<string> ReadList(string fileName)
    {
      List<string> result = new List<string>();

      try
      {
        if (File.Exists(fileName))
        {
          result.AddRange(File.ReadAllLines(fileName));
        }
      }
      catch (IOException ex)
      {
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
      catch (SecurityException se)
      {
        LOG.Error(se);
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
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
      catch (SecurityException se)
      {
        LOG.Error(se);
      }
    }

    private static void LoadProperties(ConcurrentDictionary<string, string> properties, List<string> list)
    {
      list.ForEach(line =>
      {
        string[] parts = line.Split('=');
        if (parts != null && parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
        {
          properties[parts[0]] = parts[1];
        }
      });
    }

    private static void SaveProperties(string fileName, IEnumerable<KeyValuePair<string, string>> enumeration)
    {
      try
      {
        List<string> lines = new List<string>();
        foreach (var keypair in enumeration)
        {
          lines.Add(keypair.Key + "=" + keypair.Value);
        }

        File.WriteAllLines(fileName, lines);
      }
      catch (IOException ex)
      {
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
      catch (SecurityException se)
      {
        LOG.Error(se);
      }
    }

    private static void RemoveFileIfExists(string fileName)
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
  }
}