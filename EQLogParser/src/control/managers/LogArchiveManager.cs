using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal static partial class LogArchiveManager
  {
    private const long M = 1000000;
    internal const string CompressYes = "Yes";
    internal const string TypeActivity = "Activity";
    internal const string TypeSchedule = "Selected Day/Time";
    internal const string OrganizeInFiles = "Individual Files";
    internal const string OrganizeInFolders = "Server and Character Folders";

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Regex FileNameToArchiveRegex = TheFileNameToArchiveRegex();
    private static readonly object LockObject = new();
    private static readonly HashSet<LogReader> ArchiveQueue = [];
    private static readonly ConcurrentDictionary<string, bool> Archiving = [];
    private static ArchiveScheduler Scheduler;

    private static readonly Dictionary<string, long> ArchiveFileSizes = new()
    {
      { "any size", 0 },
      { "25m", 25 * M },
      { "50m", 50 * M },
      { "75m", 75 * M },
      { "100m", 100 * M },
      { "250m", 250 * M },
      { "500m", 500 * M },
      { "750m", 750 * M },
      { "1g", 1000 * M },
      { "1.5g", 1500 * M },
      { "2g", 2000 * M }
    };

    private static readonly Dictionary<string, int> ArchiveFileAges = new()
    {
      { "any age", 0 },
      { "1 day", 1 },
      { "3 days", 3 },
      { "1 week", 7 },
      { "2 weeks", 14 },
      { "3 weeks", 21 },
      { "1 month", 28 }
    };

    internal static async Task<HashSet<string>> GetOpenLogFilesAsync()
    {
      var logFiles = new HashSet<string>();
      if (await TriggerStateDB.Instance.GetConfig() is var config)
      {
        if (MainWindow.CurrentLogFile is { } currentFile)
        {
          logFiles.Add(currentFile);
        }

        if (config.IsAdvanced)
        {
          foreach (var file in config.Characters.Select(character => character.FilePath))
          {
            logFiles.Add(file);
          }
        }
      }
      return logFiles;
    }

    internal static void SetArchiveSchedule()
    {
      Scheduler?.Dispose();
      Scheduler = null;

      var type = ConfigUtil.GetSetting("LogManagementType", "Activity");
      if (ConfigUtil.IfSet("LogManagementEnabled") && TypeSchedule.Equals(type, StringComparison.OrdinalIgnoreCase))
      {
        var day = ConfigUtil.GetSetting("LogManagementScheduleDay", "Sunday");
        var hour = ConfigUtil.GetSettingAsInteger("LogManagementScheduleHour", 12);
        var minute = ConfigUtil.GetSettingAsInteger("LogManagementScheduleMinute", 0);
        var ampm = ConfigUtil.GetSetting("LogManagementScheduleAMPM", "AM");
        Scheduler = new ArchiveScheduler(day, hour, minute, ampm);
      }
    }

    internal static async void QueueFileArchiveAsync(LogReader logReader)
    {
      var file = Path.GetFileName(logReader.FileName);
      var type = ConfigUtil.GetSetting("LogManagementType", "Activity");
      if (string.IsNullOrEmpty(file) || !FileNameToArchiveRegex.IsMatch(file) || !ConfigUtil.IfSet("LogManagementEnabled") ||
        !TypeActivity.Equals(type, StringComparison.OrdinalIgnoreCase))
      {
        return;
      }

      var startArchive = false;
      lock (LockObject)
      {
        if (ArchiveQueue.Any(item => item.FileName == logReader.FileName))
        {
          return;
        }

        ArchiveQueue.Add(logReader);
        if (ArchiveQueue.Count == 1)
        {
          startArchive = true;
        }
      }

      if (startArchive)
      {
        await Task.Delay(1000);
        await Task.Run(ArchiveProcessAsync);
      }
    }

    internal static async Task ArchiveNowAsync(HashSet<string> files)
    {
      lock (LockObject)
      {
        ArchiveQueue.Clear();
      }

      foreach (var file in files)
      {
        if (File.Exists(file))
        {
          await DoArchiveAsync(file);
        }
      }
    }

    private static async Task ArchiveProcessAsync()
    {
      bool remaining;
      List<string> readyList = [];

      lock (LockObject)
      {
        foreach (var reader in ArchiveQueue.ToArray())
        {
          if (reader.IsInValid())
          {
            ArchiveQueue.Remove(reader);
          }

          if (reader.GetProgress() >= 99.9)
          {
            readyList.Add(reader.FileName);
            ArchiveQueue.Remove(reader);
          }
        }

        remaining = ArchiveQueue.Count != 0;
      }

      foreach (var path in readyList)
      {
        if (!File.Exists(path))
        {
          continue;
        }

        var savedFileSize = ConfigUtil.GetSetting("LogManagementMinFileSize", "500M");
        if (string.IsNullOrEmpty(savedFileSize))
        {
          continue;
        }

        savedFileSize = savedFileSize.ToLower(null);
        if (!ArchiveFileSizes.ContainsKey(savedFileSize))
        {
          continue;
        }

        var savedFileAge = ConfigUtil.GetSetting("LogManagementMinFileAge", "1 Week");
        if (string.IsNullOrEmpty(savedFileAge))
        {
          continue;
        }

        savedFileAge = savedFileAge.ToLower(null);
        if (!ArchiveFileAges.ContainsKey(savedFileAge))
        {
          continue;
        }

        var fileInfo = new FileInfo(path);
        var creationTime = fileInfo.CreationTimeUtc;

        if (creationTime > DateTime.UtcNow.Subtract(TimeSpan.FromDays(ArchiveFileAges[savedFileAge])))
        {
          continue;
        }

        if (fileInfo.Length < ArchiveFileSizes[savedFileSize])
        {
          continue;
        }

        if (Archiving.TryAdd(path, true))
        {
          try
          {
            await DoArchiveAsync(path);
          }
          catch (Exception e)
          {
            Log.Error($"Could not archive log file: {path}", e);
          }
          finally
          {
            Archiving.TryRemove(path, out _);
          }
        }
      }

      if (remaining)
      {
        await Task.Delay(3000);
        await Task.Run(ArchiveProcessAsync);
      }
    }

    private static async Task DoArchiveAsync(string path)
    {
      var fileInfo = new FileInfo(path);
      var creationTime = fileInfo.CreationTimeUtc;

      var archiveFolder = ConfigUtil.GetSetting("LogManagementArchiveFolder");
      if (string.IsNullOrEmpty(archiveFolder) || Path.GetDirectoryName(archiveFolder) == null)
      {
        return;
      }

      var compress = ConfigUtil.GetSetting("LogManagementCompressArchive", CompressYes);
      var organize = ConfigUtil.GetSetting("LogManagementOrganize", OrganizeInFiles);

      var archivePath = archiveFolder;
      if (OrganizeInFolders.Equals(organize, StringComparison.OrdinalIgnoreCase) &&
          FileUtil.ParseFileName(fileInfo.Name, out var name, out var server))
      {
        archivePath = Path.Combine(archivePath, server, name);
      }

      Directory.CreateDirectory(archivePath);
      var formatted = DateTime.Now.ToString("_yyyyMMddHHmm_ssfff", CultureInfo.InvariantCulture) + ".txt";
      var destination = archivePath + Path.DirectorySeparatorChar + fileInfo.Name.Replace(".txt", formatted);
      File.Move(path, destination);

      try
      {
        File.CreateText(path);
      }
      catch (Exception)
      {
        // ignore
      }

      try
      {
        _ = new FileInfo(path)
        {
          CreationTime = DateTime.Now
        };
      }
      catch (Exception)
      {
        // ignore
      }

      if (CompressYes.Equals(compress, StringComparison.OrdinalIgnoreCase))
      {
        var compressedFilePath = $"{destination}.gz";
        var originalFileStream = File.OpenRead(destination);
        var compressedFileStream = File.Create(compressedFilePath);
        var compressionStream = new GZipStream(compressedFileStream, CompressionMode.Compress);
        await originalFileStream.CopyToAsync(compressionStream);
        await compressionStream.DisposeAsync();
        await compressedFileStream.DisposeAsync();
        await originalFileStream.DisposeAsync();

        try
        {
          File.Delete(destination);
        }
        catch (Exception)
        {
          // ignore delete errors
        }
      }

      Log.Info($"Archived File (Originally Created {creationTime.ToLocalTime().ToString(CultureInfo.InvariantCulture)}): {path}");

      await Task.Delay(100);
    }

    [GeneratedRegex(@"^eqlog_([a-zA-Z]+)_([a-zA-Z]+)(?!.*\d).*\.txt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TheFileNameToArchiveRegex();
  }
}
