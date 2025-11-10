using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace EQLogParser
{
  public static partial class FileUtil
  {
    private const long M = 1000000;
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Regex ArchivedFileNameRegex = TheArchivedFileNameRegex();
    private static readonly Regex FileNameToArchiveRegex = TheFileNameToArchiveRegex();
    private static readonly Regex ServerFileNameRegex = TheServerFileNameRegex();
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

    // public so backup util can use
    public static string BuildBackupFilename()
    {
      // get file name
      var version = Application.ResourceAssembly.GetName().Version?.ToString();
      var dateTime = DateTime.Now.ToString("yyyyMMdd-ssfff", CultureInfo.InvariantCulture);
      version = string.IsNullOrEmpty(version) ? "unknown" : version[..^2];
      return $"EQLogParser_backup_{version}_{dateTime}.zip";
    }

    internal static string GetDirFromPath(string path)
    {
      if (!string.IsNullOrEmpty(path))
      {
        try
        {
          var name = Path.GetDirectoryName(path);
          if (Directory.Exists(name))
          {
            return name;
          }
        }
        catch (Exception)
        {
          // ignore
        }
      }

      return null;
    }

    internal static async Task<HashSet<string>> GetOpenLogFilesAsync()
    {
      var logFiles = new HashSet<string>();
      if (await TriggerStateManager.Instance.GetConfig() is var config)
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

    internal static List<string> FindArchivedLogFiles(string player, string server, double start)
    {
      var matchingFiles = new List<(string filePath, DateTime date)>();

      var archiveFolder = ConfigUtil.GetSetting("LogManagementArchiveFolder");
      if (string.IsNullOrEmpty(archiveFolder) || Path.GetDirectoryName(archiveFolder) == null)
      {
        return [];
      }

      var formats = new[] { "yyyyMMddHHmm", "yyyyMMdd" };

      foreach (var file in Directory.EnumerateFiles(archiveFolder, "*.*", SearchOption.AllDirectories))
      {
        var fileName = Path.GetFileName(file);
        var match = ArchivedFileNameRegex.Match(fileName);

        if (match.Success &&
            string.Equals(match.Groups["player"].Value, player, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(match.Groups["server"].Value, server, StringComparison.OrdinalIgnoreCase) &&
            DateTime.TryParseExact(match.Groups["datetime"].Value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var fileDate))
        {
          if (start == 0 || DateUtil.ToDouble(fileDate) >= start)
          {
            matchingFiles.Add((file, fileDate));
          }
        }
      }

      return [.. matchingFiles.OrderByDescending(f => f.date).Select(f => f.filePath)];
    }

    internal static void SetArchiveSchedule()
    {
      // turn off
      Scheduler?.Dispose();
      Scheduler = null;

      // if enabled
      var type = ConfigUtil.GetSetting("LogManagementType", "Activity");
      if (ConfigUtil.IfSet("LogManagementEnabled") && LogManagementWindow.TypeSchedule.Equals(type, StringComparison.OrdinalIgnoreCase))
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
        !LogManagementWindow.TypeActivity.Equals(type, StringComparison.OrdinalIgnoreCase))
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
          // was archived by another process?
          continue;
        }

        // can remove defaults in the future. fixing issue where config was enabled but settings were never chosen/saved
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

        // make sure another thread isn't in the middle of an archive
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
            // no longer processing
            Archiving.TryRemove(path, out _);
          }
        }
      }

      // try again
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

      var compress = ConfigUtil.GetSetting("LogManagementCompressArchive", LogManagementWindow.CompressYes);
      var organize = ConfigUtil.GetSetting("LogManagementOrganize", LogManagementWindow.OrganizeInFiles);

      var archivePath = archiveFolder;
      if (LogManagementWindow.OrganizeInFolders.Equals(organize, StringComparison.OrdinalIgnoreCase) &&
          ParseFileName(fileInfo.Name, out var name, out var server))
      {
        archivePath = Path.Combine(archivePath, server, name);
      }

      Directory.CreateDirectory(archivePath);
      var formatted = DateTime.Now.ToString("_yyyyMMddHHmm_ssfff", CultureInfo.InvariantCulture) + ".txt";
      var destination = archivePath + Path.DirectorySeparatorChar + fileInfo.Name.Replace(".txt", formatted);
      File.Move(path, destination);

      try
      {
        // create new empty file or EQ may do this for us
        File.CreateText(path);
      }
      catch (Exception)
      {
        // ignore
      }

      try
      {
        // fix creation time as a workaround
        _ = new FileInfo(path)
        {
          CreationTime = DateTime.Now
        };
      }
      catch (Exception)
      {
        // ignore
      }

      // compress if specified
      if (LogManagementWindow.CompressYes.Equals(compress, StringComparison.OrdinalIgnoreCase))
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

      // pause before archiving each file
      await Task.Delay(100);
    }

    internal static bool ParseFileName(string theFile, out string name, out string server)
    {
      name = "You";
      server = "Unknown";
      var found = 0;
      var file = Path.GetFileName(theFile);
      var matches = ServerFileNameRegex.Matches(file);

      if (matches.Count == 1)
      {
        if (matches[0].Groups.Count > 1)
        {
          name = matches[0].Groups[1].Value;
          found++;
        }

        if (matches[0].Groups.Count > 2)
        {
          server = matches[0].Groups[2].Value;
          found++;
        }
      }

      return found == 2;
    }

    internal static StreamReader GetStreamReader(FileStream f, double start = 0)
    {
      StreamReader s;
      if (!f.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
      {
        if (f.Length > 100000000 && start > 0)
        {
          SetStartingPosition(f, start);
        }

        s = new StreamReader(f);
      }
      else
      {
        var gs = new GZipStream(f, CompressionMode.Decompress);
        s = new StreamReader(gs, Encoding.UTF8, true, 4096);
      }

      return s;
    }

    internal static void SetStartingPosition(FileStream f, double time, long left = 0, long right = 0, long good = 0, int count = 0)
    {
      if (count <= 8)
      {
        if (f.Position == 0)
        {
          right = f.Length;
          f.Seek(f.Length / 2, SeekOrigin.Begin);
        }

        try
        {
          var s = new StreamReader(f);
          s.ReadLine();
          var check = TimeRange.TimeCheck(s.ReadLine(), time);
          s.DiscardBufferedData();

          long pos;
          if (check)
          {
            pos = left + ((f.Position - left) / 2);
            right = f.Position;
          }
          else
          {
            pos = right - ((right - f.Position) / 2);
            good = left = f.Position;
          }

          f.Seek(pos, SeekOrigin.Begin);
          SetStartingPosition(f, time, left, right, good, count + 1);
        }
        catch (IOException ioe)
        {
          Log.Error("Problem searching log file", ioe);
        }
        catch (OutOfMemoryException ome)
        {
          Log.Debug("Out of memory", ome);
        }
      }
      else if (f.Position != good)
      {
        f.Seek(good, SeekOrigin.Begin);
      }
    }

    [GeneratedRegex(@"^eqlog_([a-zA-Z]+)_([a-zA-Z]+)(?!.*\d).*\.txt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TheFileNameToArchiveRegex();

    [GeneratedRegex(@"^eqlog_([a-zA-Z]+)_([a-zA-Z]+).*\.(txt|log)(?:\.gz)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TheServerFileNameRegex();

    [GeneratedRegex(@"^eqlog_(?<player>[^_]+)_(?<server>[^_]+)_(?<datetime>\d{8}(?:\d{4})?)_\d+\.txt(?:\.gz)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TheArchivedFileNameRegex();
  }
}
